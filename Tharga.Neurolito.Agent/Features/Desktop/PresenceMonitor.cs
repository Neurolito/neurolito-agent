using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using Tharga.Communication.Client;
using Tharga.Communication.Client.Communication;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Desktop;

/// <summary>
/// Decides whether someone is using the machine, and tells the server, with the screensaver as the trigger.
/// </summary>
/// <remarks>
/// A service in session 0 cannot see the user's input. What it can see: a screensaver saying it runs (over the pipe),
/// whether the console is locked, and whether anyone is logged on at all. The machine is free when any of those says
/// so. Becoming in use counts at once; becoming free counts after <see cref="DesktopOptions.ResumeDelaySeconds"/>, so a
/// moment away does not get a model loaded under someone. The server is told on every change, and again whenever the
/// connection comes back, since a new connection starts as available.
/// </remarks>
public interface IPresenceMonitor
{
    event EventHandler<MachinePresence> Changed;

    MachinePresence Current { get; }

    /// <summary>A screensaver connected to the pipe said it runs (true) or ended (false).</summary>
    void ScreensaverRunning(bool running);

    /// <summary>The machine is about to sleep: tell the server now, while the connection still exists.</summary>
    Task GoingToSleepAsync();

    /// <summary>The machine woke up.</summary>
    void Resumed();

    /// <summary>In use for a reason of the agent's own, such as an update, until <see cref="Release"/>: no new job arrives.</summary>
    void Hold(string reason);

    /// <summary>Ends a <see cref="Hold"/>.</summary>
    void Release();

    /// <summary>
    /// A session event from Windows: what it says about the console, for when the console cannot be read directly.
    /// Null for what the event does not say.
    /// </summary>
    void SessionChanged(bool? locked, bool? loggedOn);
}

internal sealed class PresenceMonitor : BackgroundService, IPresenceMonitor
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);

    private readonly DesktopOptions _options;
    private readonly IClientCommunication _communication;
    private readonly ISignalRHostedService _connection;
    private readonly ILogger<PresenceMonitor> _logger;
    private readonly SemaphoreSlim _report = new(1, 1);
    private readonly SemaphoreSlim _evaluate = new(1, 1);

    private int _screensavers;
    private DateTime? _freeSince;
    private volatile bool _asleep;
    private volatile string _hold;
    private bool? _lockedByEvent;
    private bool? _loggedOnByEvent;
    private MachinePresence _current = MachinePresence.InUse;
    private string _reason = "starting";
    private MachinePresence? _reported;
    private HubConnectionState _lastConnection = HubConnectionState.Disconnected;

    public PresenceMonitor(IOptions<DesktopOptions> options, IClientCommunication communication, ISignalRHostedService connection, ILogger<PresenceMonitor> logger)
    {
        _options = options.Value;
        _communication = communication;
        _connection = connection;
        _logger = logger;
    }

    public event EventHandler<MachinePresence> Changed;

    public MachinePresence Current => _current;

    public string Reason => _reason;

    public void ScreensaverRunning(bool running)
    {
        if (running) Interlocked.Increment(ref _screensavers);
        else if (Interlocked.Decrement(ref _screensavers) < 0) Interlocked.Exchange(ref _screensavers, 0);
        _ = EvaluateAsync();
    }

    public async Task GoingToSleepAsync()
    {
        _asleep = true;
        await SetAsync(MachinePresence.Asleep, "the machine is going to sleep", force: true);
    }

    public void SessionChanged(bool? locked, bool? loggedOn)
    {
        if (locked != null) _lockedByEvent = locked;
        if (loggedOn != null) _loggedOnByEvent = loggedOn;
        _ = EvaluateAsync();
    }

    public void Hold(string reason)
    {
        _hold = reason;
        _ = EvaluateAsync();
    }

    public void Release()
    {
        _hold = null;
        _ = EvaluateAsync();
    }

    public void Resumed()
    {
        _asleep = false;
        _freeSince = null;
        _ = EvaluateAsync();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EvaluateAsync();

                //NOTE: A new connection starts as available on the server, so say again what the machine is.
                var state = _connection.State;
                if (state == HubConnectionState.Connected && _lastConnection != HubConnectionState.Connected) _reported = null;
                _lastConnection = state;
                await ReportAsync();

                await Task.Delay(Tick, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "The presence check failed.");
                await Task.Delay(Tick, stoppingToken);
            }
        }
    }

    private async Task EvaluateAsync()
    {
        //NOTE: One at a time: the pipe, the session events and the timer all ask.
        await _evaluate.WaitAsync();
        try
        {
            await EvaluateOnceAsync();
        }
        finally
        {
            _evaluate.Release();
        }
    }

    private async Task EvaluateOnceAsync()
    {
        if (_asleep) return;

        var (free, reason) = Observe();
        if (!free)
        {
            _freeSince = null;
            await SetAsync(MachinePresence.InUse, reason);
            return;
        }

        _freeSince ??= DateTime.UtcNow;
        if (_current == MachinePresence.Available || DateTime.UtcNow - _freeSince >= TimeSpan.FromSeconds(_options.ResumeDelaySeconds))
        {
            await SetAsync(MachinePresence.Available, reason);
        }
    }

    /// <summary>Whether the machine is free of its user now, and why.</summary>
    internal (bool Free, string Reason) Observe()
    {
        if (_hold is { } hold) return (false, hold);
        if (_options.AlwaysWork) return (true, "set to work all the time");
        if (Volatile.Read(ref _screensavers) > 0) return (true, "the screensaver is on");

        //NOTE: What Windows says now, else what its session events said. Unknown counts as someone at the machine:
        //then only the screensaver frees it, which is never wrong.
        var console = ConsoleSession.Read();
        var loggedOn = console?.UserLoggedOn ?? _loggedOnByEvent ?? true;
        var locked = console?.Locked ?? _lockedByEvent ?? false;
        if (!loggedOn) return (true, "nobody is logged on");
        if (locked) return (true, "the machine is locked");

        return (false, "someone is using the machine");
    }

    private async Task SetAsync(MachinePresence presence, string reason, bool force = false)
    {
        var changed = presence != _current;
        _current = presence;
        _reason = reason;
        if (changed)
        {
            _logger.LogInformation("The machine is now {Presence}: {Reason}.", presence, reason);
            Changed?.Invoke(this, presence);
        }

        if (changed || force) await ReportAsync(force);
    }

    private async Task ReportAsync(bool force = false)
    {
        if (!force && _reported == _current) return;
        if (_connection.State != HubConnectionState.Connected) return;

        await _report.WaitAsync();
        try
        {
            if (!force && _reported == _current) return;
            await _communication.PostAsync(new MachinePresenceMessage { Presence = _current, Reason = _reason });
            _reported = _current;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not tell the server the machine is {Presence}.", _current);
        }
        finally
        {
            _report.Release();
        }
    }
}
