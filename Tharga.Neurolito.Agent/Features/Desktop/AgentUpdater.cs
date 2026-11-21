using Microsoft.Extensions.Options;
using Tharga.Neurolito.Agent.Features.Engine;
using Tharga.Neurolito.Client.Contract;
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

    /// <summary>The longest to wait for model installs to finish before updating anyway, in minutes: a large model is a long download.</summary>
    public int InstallWaitMinutes { get; set; } = 360;
}

/// <summary>Looks for a new version now, and updates when told to. The server asks through this, and so does the timer.</summary>
public interface IAgentUpdateService
{
    Task<CheckForUpdateResponse> CheckAsync(bool apply, CancellationToken cancellationToken);
}

/// <summary>The update machinery, behind an interface so the rule around it can be tested without installing anything.</summary>
internal interface IUpdateEngine
{
    bool IsInstalled { get; }
    string CurrentVersion { get; }

    /// <summary>The newest version available; null when this is it.</summary>
    Task<string> CheckAsync();

    Task DownloadAsync(CancellationToken cancellationToken);

    /// <summary>Hands the downloaded update to Velopack's Update.exe, which swaps the files once the process has exited.</summary>
    void ApplyAfterExit();
}

internal sealed class VelopackEngine : IUpdateEngine
{
    private readonly UpdateManager _manager;
    private UpdateInfo _update;

    public VelopackEngine(UpdateManager manager)
    {
        _manager = manager;
    }

    public bool IsInstalled => _manager.IsInstalled;
    public string CurrentVersion => _manager.CurrentVersion?.ToString();

    public async Task<string> CheckAsync()
    {
        _update = await _manager.CheckForUpdatesAsync();
        return _update?.TargetFullRelease.Version.ToString();
    }

    public Task DownloadAsync(CancellationToken cancellationToken) => _manager.DownloadUpdatesAsync(_update, cancelToken: cancellationToken);

    public void ApplyAfterExit() => _manager.WaitExitThenApplyUpdates(_update.TargetFullRelease, silent: true, restart: false);
}

/// <summary>
/// Keeps an agent installed by the Windows installer up to date, with Velopack.
/// </summary>
/// <remarks>
/// The service updates itself, without a privileged helper: its account may write its own install folder. When a new
/// version is out it downloads it, tells the server the machine is in use so no new job arrives, lets the running job and
/// any model install finish, hands the update to Velopack's Update.exe and exits with an error code. Windows' restart-on-failure then
/// starts the new version - Update.exe has swapped the files by then.
/// <para>
/// The timer looks every <see cref="UpdateOptions.CheckHours"/>, and the server can ask at any time through
/// <see cref="IAgentUpdateService"/>: the same rule either way. Applying answers at once and goes on in the background,
/// since the agent exits to start on the new version and cannot answer after that.
/// </para>
/// </remarks>
internal sealed class AgentUpdater : BackgroundService, IAgentUpdateService
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(2);

    private readonly UpdateOptions _options;
    private readonly IPresenceMonitor _presence;
    private readonly IAgentActivity _activity;
    private readonly IInstallQueue _installs;
    private readonly ILogger<AgentUpdater> _logger;
    private readonly Action _exit;
    private readonly TimeSpan _handover;
    private readonly TimeSpan _drainPoll;
    private readonly object _lock = new();
    private IUpdateEngine _engine;
    private bool _updating;
    private string _target;

    public AgentUpdater(IOptions<UpdateOptions> options, IPresenceMonitor presence, IAgentActivity activity, ILogger<AgentUpdater> logger, IInstallQueue installs = null,
        IUpdateEngine engine = null, Action exit = null, TimeSpan? handover = null, TimeSpan? drainPoll = null)
    {
        _options = options.Value;
        _presence = presence;
        _activity = activity;
        _installs = installs;
        _logger = logger;
        _engine = engine;

        //NOTE: An error code, so Windows' restart-on-failure starts the service again, on the new files.
        _exit = exit ?? (() => Environment.Exit(1));

        //NOTE: Time for the server to hear the machine is in use before the connection drops.
        _handover = handover ?? TimeSpan.FromSeconds(3);
        _drainPoll = drainPoll ?? TimeSpan.FromSeconds(5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var engine = Engine();
        if (engine == null)
        {
            _logger.LogWarning("Updates are on, but there is no update source.");
            return;
        }

        if (!engine.IsInstalled)
        {
            _logger.LogInformation("Not installed by the Windows installer, so the agent does not update itself.");
            return;
        }

        _logger.LogInformation("Version {Version}; looking for updates at {Source} every {Hours} hours.", engine.CurrentVersion, _options.Source, _options.CheckHours);
        var wait = FirstCheck;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(wait, stoppingToken);
                wait = TimeSpan.FromHours(Math.Max(0.05, _options.CheckHours));

                await CheckAsync(apply: true, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>Looks for a newer version; with <paramref name="apply"/>, goes on to install it in the background.</summary>
    public async Task<CheckForUpdateResponse> CheckAsync(bool apply, CancellationToken cancellationToken)
    {
        var engine = Engine();
        if (engine == null) return Reply(UpdateStatus.Disabled, null, null, "There is no update source.");
        if (!engine.IsInstalled) return Reply(UpdateStatus.NotInstalled, null, null, "Not installed by the Windows installer, so the agent does not update itself.");

        lock (_lock)
        {
            if (_updating) return Reply(UpdateStatus.Updating, engine.CurrentVersion, _target, $"Already updating to {_target}.");
        }

        string latest;
        try
        {
            latest = await engine.CheckAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Could not look for a new version; the agent goes on as it is and tries again later.");
            return Reply(UpdateStatus.Failed, engine.CurrentVersion, null, $"Could not look for a new version: {e.Message}");
        }

        if (latest == null)
        {
            _logger.LogInformation("Version {Version} is the newest.", engine.CurrentVersion);
            return Reply(UpdateStatus.UpToDate, engine.CurrentVersion, null, "This is the newest version.");
        }

        if (!apply) return Reply(UpdateStatus.Available, engine.CurrentVersion, latest, $"Version {latest} is out.");

        lock (_lock)
        {
            if (_updating) return Reply(UpdateStatus.Updating, engine.CurrentVersion, _target, $"Already updating to {_target}.");
            _updating = true;
            _target = latest;
        }

        _logger.LogInformation("Version {Version} is out; downloading.", latest);
        _ = Task.Run(() => ApplyAsync(engine, latest, cancellationToken), CancellationToken.None);
        return Reply(UpdateStatus.Updating, engine.CurrentVersion, latest, $"Downloading {latest}; the agent takes no new job, lets the running job finish and then restarts on it.");
    }

    private async Task ApplyAsync(IUpdateEngine engine, string version, CancellationToken cancellationToken)
    {
        try
        {
            await engine.DownloadAsync(cancellationToken);

            await DrainAsync($"updating to {version}", cancellationToken);

            _logger.LogInformation("Installing version {Version}; the service starts again on it.", version);
            engine.ApplyAfterExit();
            _exit();
        }
        catch (Exception e)
        {
            _presence.Release();
            lock (_lock)
            {
                _updating = false;
                _target = null;
            }

            if (e is OperationCanceledException && cancellationToken.IsCancellationRequested) return;
            _logger.LogWarning(e, "The update failed; the agent goes on as it is and tries again later.");
        }
    }

    private IUpdateEngine Engine()
    {
        if (_engine != null) return _engine;

        var source = SourceOf(_options.Source);
        return source == null ? null : _engine = new VelopackEngine(new UpdateManager(source));
    }

    private static CheckForUpdateResponse Reply(string status, string current, string latest, string message) => new() { Status = status, Current = current, Latest = latest, Message = message };

    /// <summary>
    /// No new job, the running one finished and no model installing - or the time for each is up. An install started
    /// while waiting is waited for too: the server may still ask for one.
    /// </summary>
    private async Task DrainAsync(string reason, CancellationToken cancellationToken)
    {
        _presence.Hold(reason);
        var started = DateTime.UtcNow;
        var jobsUntil = started.AddMinutes(_options.DrainMinutes);
        var installsUntil = started.AddMinutes(_options.InstallWaitMinutes);
        bool JobRunning() => _activity.Running > 0 && DateTime.UtcNow < jobsUntil;
        bool Installing() => _installs?.Busy == true && DateTime.UtcNow < installsUntil;

        while (JobRunning() || Installing())
        {
            await Task.Delay(_drainPoll, cancellationToken);
        }

        if (_activity.Running > 0) _logger.LogWarning("A job is still running after {Minutes} minutes; updating anyway.", _options.DrainMinutes);
        if (_installs?.Busy == true) _logger.LogWarning("Models are still installing after {Minutes} minutes ({Models}); updating anyway.", _options.InstallWaitMinutes, string.Join(", ", _installs.Pending));

        await Task.Delay(_handover, cancellationToken);
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
