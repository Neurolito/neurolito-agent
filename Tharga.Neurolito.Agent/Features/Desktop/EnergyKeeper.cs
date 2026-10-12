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
    private readonly IDesktopSettingsStore _settings;
    private readonly Lock _update = new();
    private EnergyMode _mode;

    public EnergyKeeper(IOptions<DesktopOptions> options, IPresenceMonitor presence, IAgentActivity activity, ILogger<EnergyKeeper> logger, IDesktopSettingsStore settings = null)
    {
        _options = options.Value;
        _settings = settings;
        _mode = _options.EnergyMode;
        _presence = presence;
        _activity = activity;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        //NOTE: Listening whatever the mode: the server may change it while the agent runs.
        _presence.Changed += OnPresenceChanged;
        _activity.Changed += OnChanged;
        if (_settings != null) _settings.Changed += OnSettingsChanged;
        Update();
        return Task.CompletedTask;
    }

    /// <summary>Whether the machine is being kept awake now.</summary>
    internal bool Holding => _blocker.Held;

    /// <summary>Lets go before the machine sleeps, so the request is not what Windows finds holding it up.</summary>
    public void Release() => Apply(false);

    private void OnPresenceChanged(object sender, MachinePresence e) => Update();

    private void OnChanged(object sender, ActivityEvent e) => Update();

    private void OnSettingsChanged(object sender, EventArgs e) => Update();

    private void Update()
    {
        lock (_update)
        {
            //NOTE: A new mode lets go first, so a request held under the old one does not keep its old reason.
            var mode = _options.EnergyMode;
            if (mode != _mode)
            {
                _logger.LogInformation("The energy mode is now {Mode}.", mode);
                Apply(false);
                _mode = mode;
            }

            Apply(ShouldHold(mode, _activity.Running > 0, _presence.Current));
        }
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
        var reason = _mode == EnergyMode.WhileWorking ? "Neurolito agent: finishing a job" : "Neurolito agent: working while nobody uses this machine";
        if (_blocker.Set(hold, reason)) _logger.LogInformation(hold ? "Keeping the machine awake ({Mode})." : "Letting the machine sleep again ({Mode}).", _mode);
        else _logger.LogWarning("Windows refused to {Action} the power request.", hold ? "set" : "clear");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _presence.Changed -= OnPresenceChanged;
        _activity.Changed -= OnChanged;
        if (_settings != null) _settings.Changed -= OnSettingsChanged;
        Release();
        return Task.CompletedTask;
    }

    public void Dispose() => _blocker.Dispose();
}
