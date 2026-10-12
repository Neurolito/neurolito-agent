using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace Tharga.Neurolito.Agent.Features.Desktop;

public static class DesktopRegistration
{
    /// <summary>
    /// The activity feed, always (the prompt pipeline reports to it); and, with <c>Desktop:Enabled</c> on Windows, the
    /// presence monitor, the screensaver pipe, the energy mode and the settings the server may change.
    /// </summary>
    public static void AddDesktop(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(DesktopOptions.SectionName).Get<DesktopOptions>() ?? new DesktopOptions();

        //NOTE: What the server set wins over the installer's settings. One options instance, changed in place, so every
        //part of the desktop sees a change at once.
        if (options.Enabled) DesktopSettingsStore.ApplySaved(options);
        builder.Services.AddSingleton(Options.Create(options));
        builder.Services.AddSingleton<IAgentActivity, AgentActivity>();

        if (!options.Enabled || !OperatingSystem.IsWindows()) return;

        //NOTE: Only with the desktop: the store being there is what makes the agent ask the server for queue counts.
        builder.Services.AddSingleton<IQueueSummaryStore, QueueSummaryStore>();
        builder.Services.AddSingleton<IWorkLog, WorkLog>();

        builder.Services.AddSingleton<PresenceMonitor>();
        builder.Services.AddSingleton<IPresenceMonitor>(s => s.GetRequiredService<PresenceMonitor>());
        builder.Services.AddHostedService(s => s.GetRequiredService<PresenceMonitor>());
        builder.Services.AddHostedService<StatusPipeServer>();
        builder.Services.AddSingleton<IDesktopSettingsStore>(new DesktopSettingsStore(options));
        builder.Services.AddSingleton<IUserScreensavers, UserScreensavers>();
        builder.Services.AddSingleton<IPowerPlan, WindowsPowerPlan>();
        builder.Services.AddSingleton<DesktopSettingsReporter>();
        builder.Services.AddSingleton<IDesktopSettingsReporter>(s => s.GetRequiredService<DesktopSettingsReporter>());
        builder.Services.AddHostedService(s => s.GetRequiredService<DesktopSettingsReporter>());
        builder.Services.AddSingleton<EnergyKeeper>();
        builder.Services.AddHostedService(s => s.GetRequiredService<EnergyKeeper>());

        var updates = builder.Configuration.GetSection(UpdateOptions.SectionName).Get<UpdateOptions>() ?? new UpdateOptions();
        builder.Services.AddSingleton(Options.Create(updates));
        if (updates.Enabled) builder.Services.AddHostedService<AgentUpdater>();
    }

    /// <summary>
    /// Replaces the Windows service lifetime with one that hears sleep, wake, lock and unlock. Called after
    /// <c>UseWindowsService</c>, whose registration it replaces; does nothing outside a service or with the desktop off.
    /// </summary>
    public static void UseDesktopLifetime(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(DesktopOptions.SectionName).Get<DesktopOptions>();
        if (options?.Enabled != true || !OperatingSystem.IsWindows() || !WindowsServiceHelpers.IsWindowsService()) return;

        builder.Services.Replace(ServiceDescriptor.Singleton<IHostLifetime, DesktopServiceLifetime>());
    }
}
