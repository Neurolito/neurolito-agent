using Microsoft.Extensions.Options;
using Velopack;
using Velopack.Sources;

namespace Tharga.Neurolito.Agent.Features.Desktop;

/// <summary>The <c>Updates</c> section: an agent installed by the Windows installer keeps itself up to date.</summary>
public record UpdateOptions
{
    public const string SectionName = "Updates";

    /// <summary>Check for and install updates. Off by default; the Windows installer turns it on.</summary>
    public bool Enabled { get; set; }

    /// <summary>Where releases are published: a GitHub repository's address, or a folder (for testing).</summary>
    public string Source { get; set; }

    /// <summary>How often to look, in hours.</summary>
    public double CheckHours { get; set; } = 6;

    /// <summary>The longest to wait for a running job to finish before updating anyway, in minutes.</summary>
    public int DrainMinutes { get; set; } = 120;
}

/// <summary>
/// Keeps an agent installed by the Windows installer up to date, with Velopack.
/// </summary>
/// <remarks>
/// The service updates itself, without a privileged helper: its account may write its own install folder. When a new
/// version is out it downloads it, tells the server the machine is in use so no new job arrives, lets the running job
/// finish, hands the update to Velopack's Update.exe and exits with an error code. Windows' restart-on-failure then
/// starts the new version - Update.exe has swapped the files by then.
/// </remarks>
internal sealed class AgentUpdater : BackgroundService
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DrainPoll = TimeSpan.FromSeconds(5);

    private readonly UpdateOptions _options;
    private readonly IPresenceMonitor _presence;
    private readonly IAgentActivity _activity;
    private readonly ILogger<AgentUpdater> _logger;

    public AgentUpdater(IOptions<UpdateOptions> options, IPresenceMonitor presence, IAgentActivity activity, ILogger<AgentUpdater> logger)
    {
        _options = options.Value;
        _presence = presence;
        _activity = activity;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var source = SourceOf(_options.Source);
        if (source == null)
        {
            _logger.LogWarning("Updates are on, but there is no update source.");
            return;
        }

        var manager = new UpdateManager(source);
        if (!manager.IsInstalled)
        {
            _logger.LogInformation("Not installed by the Windows installer, so the agent does not update itself.");
            return;
        }

        _logger.LogInformation("Version {Version}; looking for updates at {Source} every {Hours} hours.", manager.CurrentVersion, _options.Source, _options.CheckHours);
        var wait = FirstCheck;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(wait, stoppingToken);
                wait = TimeSpan.FromHours(Math.Max(0.05, _options.CheckHours));

                var update = await manager.CheckForUpdatesAsync();
                if (update == null) continue;

                var version = update.TargetFullRelease.Version;
                _logger.LogInformation("Version {Version} is out; downloading.", version);
                await manager.DownloadUpdatesAsync(update, cancelToken: stoppingToken);

                await DrainAsync($"updating to {version}", stoppingToken);

                _logger.LogInformation("Installing version {Version}; the service starts again on it.", version);
                manager.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: false);

                //NOTE: An error code, so Windows' restart-on-failure starts the service again, on the new files.
                Environment.Exit(1);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _presence.Release();
                _logger.LogWarning(e, "The update failed; the agent goes on as it is and tries again later.");
            }
        }
    }

    /// <summary>No new job, and the running one finished - or the drain time is up.</summary>
    private async Task DrainAsync(string reason, CancellationToken cancellationToken)
    {
        _presence.Hold(reason);
        var deadline = DateTime.UtcNow.AddMinutes(_options.DrainMinutes);
        while (_activity.Running > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(DrainPoll, cancellationToken);
        }

        if (_activity.Running > 0) _logger.LogWarning("A job is still running after {Minutes} minutes; updating anyway.", _options.DrainMinutes);

        //NOTE: Time for the server to hear the machine is in use before the connection drops.
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
    }

    /// <summary>A GitHub repository for an https address on github.com; otherwise a folder.</summary>
    internal static IUpdateSource SourceOf(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            return uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                ? new GithubSource(source.TrimEnd('/'), null, false)
                : new SimpleWebSource(source);
        }

        return new SimpleFileSource(new DirectoryInfo(source));
    }
}
