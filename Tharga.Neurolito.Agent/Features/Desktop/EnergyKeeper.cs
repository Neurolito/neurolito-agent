using Microsoft.Extensions.Options;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Desktop;

/// <summary>Holds off the machine's sleep as <see cref="DesktopOptions.EnergyMode"/> says.</summary>
internal sealed class EnergyKeeper : IHostedService, IDisposable
{
    private readonly DesktopOptions _options;
    private readonly IPresenceMonitor _presence;
    private readonly IAgentActivity _activity;
    private readonly ILogger<EnergyKeeper> _logger;
    private readonly SleepBlocker _blocker = new();

    public EnergyKeeper(IOptions<DesktopOptions> options, IPresenceMonitor presence, IAgentActivity activity, ILogger<EnergyKeeper> logger)
    {
        _options = options.Value;
        _presence = presence;
        _activity = activity;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.EnergyMode == EnergyMode.Never) return Task.CompletedTask;

        _presence.Changed += OnPresenceChanged;
        _activity.Changed += OnChanged;
        Update();
        return Task.CompletedTask;
    }

    /// <summary>Lets go before the machine sleeps, so the request is not what Windows finds holding it up.</summary>
    public void Release() => Apply(false);

    private void OnPresenceChanged(object sender, MachinePresence e) => Update();

    private void OnChanged(object sender, ActivityEvent e) => Update();

    private void Update()
    {
        var working = _activity.Running > 0;
        Apply(ShouldHold(_options.EnergyMode, working, _presence.Current));
    }

    /// <summary>Whether to hold off sleep: a running job always finishes under While working and While ready.</summary>
    internal static bool ShouldHold(EnergyMode mode, bool working, MachinePresence presence)
    {
        if (presence == MachinePresence.Asleep) return false;
        return mode switch
        {
            EnergyMode.WhileWorking => working,
            EnergyMode.WhileReady => working || presence == MachinePresence.Available,
            _ => false
        };
    }

    private void Apply(bool hold)
    {
        if (_blocker.Held == hold) return;

        //NOTE: One reason for the request's life: Windows keeps the one it was created with, as powercfg /requests shows.
        var reason = _options.EnergyMode == EnergyMode.WhileWorking ? "Neurolito agent: finishing a job" : "Neurolito agent: working while nobody uses this machine";
        if (_blocker.Set(hold, reason)) _logger.LogInformation(hold ? "Keeping the machine awake ({Mode})." : "Letting the machine sleep again ({Mode}).", _options.EnergyMode);
        else _logger.LogWarning("Windows refused to {Action} the power request.", hold ? "set" : "clear");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _presence.Changed -= OnPresenceChanged;
        _activity.Changed -= OnChanged;
        Release();
        return Task.CompletedTask;
    }

    public void Dispose() => _blocker.Dispose();
}
