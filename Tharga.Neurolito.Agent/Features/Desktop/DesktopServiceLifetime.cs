using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace Tharga.Neurolito.Agent.Features.Desktop;

/// <summary>
/// The Windows service lifetime, asking also for power and session events: going to sleep is reported to the server
/// before the connection drops, and a lock or unlock is acted on at once rather than at the next check.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DesktopServiceLifetime : WindowsServiceLifetime
{
    //NOTE: Windows gives a service a few seconds to answer a suspend; the message is small and the connection is up.
    private static readonly TimeSpan SleepNotice = TimeSpan.FromSeconds(3);

    private readonly IServiceProvider _services;
    private readonly ILogger<DesktopServiceLifetime> _logger;

    public DesktopServiceLifetime(IHostEnvironment environment, IHostApplicationLifetime applicationLifetime, ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor, IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor, IServiceProvider services)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        _services = services;
        _logger = loggerFactory.CreateLogger<DesktopServiceLifetime>();
        CanHandlePowerEvent = true;
        CanHandleSessionChangeEvent = true;
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        try
        {
            switch (powerStatus)
            {
                case PowerBroadcastStatus.Suspend:
                    _logger.LogInformation("The machine is going to sleep.");
                    _services.GetService<EnergyKeeper>()?.Release();
                    _services.GetRequiredService<IPresenceMonitor>().GoingToSleepAsync().Wait(SleepNotice);
                    break;
                case PowerBroadcastStatus.ResumeAutomatic:
                case PowerBroadcastStatus.ResumeSuspend:
                    _logger.LogInformation("The machine woke up ({Status}).", powerStatus);
                    _services.GetRequiredService<IPresenceMonitor>().Resumed();
                    break;
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not handle the power event {Status}.", powerStatus);
        }

        return base.OnPowerEvent(powerStatus);
    }

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        _logger.LogDebug("Session {Session}: {Reason}.", changeDescription.SessionId, changeDescription.Reason);

        //NOTE: Windows is asked again at once; what the event says is kept for when it cannot be asked.
        var monitor = _services.GetRequiredService<IPresenceMonitor>();
        switch (changeDescription.Reason)
        {
            case SessionChangeReason.SessionLock:
                monitor.SessionChanged(locked: true, loggedOn: true);
                break;
            case SessionChangeReason.SessionUnlock:
            case SessionChangeReason.SessionLogon:
                monitor.SessionChanged(locked: false, loggedOn: true);
                break;
            case SessionChangeReason.SessionLogoff:
                monitor.SessionChanged(locked: null, loggedOn: false);
                break;
        }

        base.OnSessionChange(changeDescription);
    }
}
