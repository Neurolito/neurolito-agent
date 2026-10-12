using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Tharga.Communication.Client;
using Tharga.Communication.Client.Communication;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Desktop;

/// <summary>Tells the server the desktop settings: after every connect, and whenever one of them changes.</summary>
public interface IDesktopSettingsReporter
{
    /// <summary>The settings as they are now.</summary>
    DesktopSettings Compose();

    /// <summary>Sends the settings now, with why the change just asked for could not be made, or null when it was.</summary>
    Task ReportAsync(string lastError);
}

/// <remarks>
/// The agent's own settings and the users' screensavers say when they change. The power plan can be changed in Windows
/// at any time without the agent hearing of it, so it is read again every <see cref="PowerTick"/>.
/// </remarks>
internal sealed class DesktopSettingsReporter : BackgroundService, IDesktopSettingsReporter
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PowerTick = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IDesktopSettingsStore _store;
    private readonly IPowerPlan _power;
    private readonly IUserScreensavers _screensavers;
    private readonly IClientCommunication _communication;
    private readonly ISignalRHostedService _connection;
    private readonly ILogger<DesktopSettingsReporter> _logger;
    private readonly SemaphoreSlim _report = new(1, 1);

    private PowerTimeouts _plan;
    private DateTime _planReadAt = DateTime.MinValue;
    private volatile string _lastError;
    private string _reported;
    private HubConnectionState _lastConnection = HubConnectionState.Disconnected;

    public DesktopSettingsReporter(IDesktopSettingsStore store, IUserScreensavers screensavers, IClientCommunication communication, ISignalRHostedService connection,
        ILogger<DesktopSettingsReporter> logger, IPowerPlan power = null)
    {
        _store = store;
        _power = power;
        _screensavers = screensavers;
        _communication = communication;
        _connection = connection;
        _logger = logger;
    }

    public DesktopSettings Compose() => Compose(_store, Volatile.Read(ref _plan), _screensavers.Current, _lastError);

    internal static DesktopSettings Compose(IDesktopSettingsStore store, PowerTimeouts power, IEnumerable<UserScreensaver> screensavers, string lastError) => new()
    {
        EnergyMode = Enum.Parse<DesktopEnergyMode>($"{store.EnergyMode}"),
        AlwaysWork = store.AlwaysWork,
        ResumeDelaySeconds = store.ResumeDelaySeconds,
        Power = power,
        Screensavers = screensavers.OrderBy(x => x.User, StringComparer.OrdinalIgnoreCase).ToArray(),
        LastError = lastError
    };

    /// <summary>What the server has been told, as text: two settings that would show the same compare equal.</summary>
    internal static string Fingerprint(DesktopSettings settings) => JsonSerializer.Serialize(settings with
    {
        Screensavers = (settings.Screensavers ?? []).OrderBy(x => x.User, StringComparer.OrdinalIgnoreCase).ToArray()
    }, Json);

    public async Task ReportAsync(string lastError)
    {
        _lastError = lastError;
        ReadPower();
        await SendAsync(force: true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow - _planReadAt >= PowerTick) ReadPower();

                //NOTE: The server knows nothing of a new connection's settings, so say them again.
                var state = _connection.State;
                if (state == HubConnectionState.Connected && _lastConnection != HubConnectionState.Connected) _reported = null;
                _lastConnection = state;
                await SendAsync(force: false);

                await Task.Delay(Tick, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "The desktop settings check failed.");
                await Task.Delay(Tick, stoppingToken);
            }
        }
    }

    private void ReadPower()
    {
        _planReadAt = DateTime.UtcNow;
        try
        {
            Volatile.Write(ref _plan, _power?.Read());
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "The power plan could not be read.");
        }
    }

    private async Task SendAsync(bool force)
    {
        if (_connection.State != HubConnectionState.Connected) return;

        await _report.WaitAsync();
        try
        {
            var settings = Compose();
            var fingerprint = Fingerprint(settings);
            if (!force && fingerprint == _reported) return;

            await _communication.PostAsync(new DesktopSettingsMessage { Settings = settings });
            _reported = fingerprint;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not tell the server the desktop settings.");
        }
        finally
        {
            _report.Release();
        }
    }
}
