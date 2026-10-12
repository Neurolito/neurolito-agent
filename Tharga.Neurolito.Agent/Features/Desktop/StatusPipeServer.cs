using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;
using Tharga.Neurolito.Agent.Features.Engine;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Desktop;

/// <summary>
/// The local pipe the screensaver reads: the machine's state and the shape of the work, one JSON object per line.
/// </summary>
/// <remarks>
/// <para>The screensaver says <c>idle</c> when it starts and <c>active</c> when it ends; a dropped connection counts as
/// <c>active</c>. It is told <c>state</c> (presence, working, machine, engine, model, version, loaded models) on
/// connecting and on every change, and <c>job-start</c> (model, words in), <c>tokens</c> and <c>job-end</c> (words out,
/// duration, work units) as work happens. <c>model</c> says a model is <c>loading</c> for a job, has <c>loaded</c> or
/// was <c>unloaded</c>; <c>work</c> is the machine's work units by hour and day; <c>queue</c> the server's count of the
/// jobs it may run, with and without a model for them. Never prompt or answer text.</para>
/// <para>A helper in each user's session says <c>screensaver</c> (whether the user's screensaver is the Neurolito one,
/// on, and after how long) and is told <c>set-screensaver</c> when the server changes it. A helper is told nothing
/// else. Its user is read from its session, not from what it says.</para>
/// <para>Local only: authenticated users on this machine may connect, the network may not. Each client has its own
/// queue, so a screensaver that stops reading cannot hold up the agent.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class StatusPipeServer : BackgroundService
{
    private const int QueueLength = 1000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The release this agent is, without the commit it was built from, so the screensaver shows which one runs.</summary>
    private static readonly string Version = typeof(StatusPipeServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];

    private readonly DesktopOptions _options;
    private readonly IPresenceMonitor _presence;
    private readonly IAgentActivity _activity;
    private readonly IEngine _engine;
    private readonly ILogger<StatusPipeServer> _logger;
    private readonly ILoadedModelsReporter _loaded;
    private readonly IQueueSummaryStore _queue;
    private readonly IWorkLog _work;
    private readonly Lock _clientsLock = new();
    private readonly List<PipeClient> _clients = [];
    private readonly IUserScreensavers _screensavers;

    private string _engineName;
    private string _model;

    public StatusPipeServer(IOptions<DesktopOptions> options, IPresenceMonitor presence, IAgentActivity activity, IEngine engine, ILogger<StatusPipeServer> logger,
        ILoadedModelsReporter loaded = null, IQueueSummaryStore queue = null, IWorkLog work = null, IUserScreensavers screensavers = null)
    {
        _screensavers = screensavers;
        _loaded = loaded;
        _queue = queue;
        _work = work;
        _options = options.Value;
        _presence = presence;
        _activity = activity;
        _engine = engine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _engineName = await _engine.GetNameAsync(stoppingToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogDebug(e, "No engine name for the screensaver yet.");
        }

        _presence.Changed += OnPresenceChanged;
        _activity.Changed += OnActivity;
        if (_loaded != null) _loaded.LoadedChanged += OnLoadedChanged;
        if (_queue != null) _queue.Changed += OnQueueChanged;
        if (_work != null) _work.Changed += OnWorkChanged;
        _logger.LogInformation("The screensaver pipe is \\\\.\\pipe\\{Pipe}.", _options.PipeName);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                NamedPipeServerStream pipe;
                try
                {
                    pipe = NamedPipeServerStreamAcl.Create(_options.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, Security());
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Could not open the screensaver pipe {Pipe}.", _options.PipeName);
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                    continue;
                }

                try
                {
                    await pipe.WaitForConnectionAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    await pipe.DisposeAsync();
                    break;
                }

                _ = Task.Run(() => ServeAsync(pipe, stoppingToken), CancellationToken.None);
            }
        }
        finally
        {
            _presence.Changed -= OnPresenceChanged;
            _activity.Changed -= OnActivity;
            if (_loaded != null) _loaded.LoadedChanged -= OnLoadedChanged;
            if (_queue != null) _queue.Changed -= OnQueueChanged;
            if (_work != null) _work.Changed -= OnWorkChanged;
        }
    }

    /// <summary>Users on this machine may read the agent's state; nobody over the network may.</summary>
    private static PipeSecurity Security()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        var who = Describe(pipe);
        var queue = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueLength) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var client = new PipeClient(Guid.NewGuid(), queue);
        lock (_clientsLock) _clients.Add(client);
        queue.Writer.TryWrite(StateLine());
        if (WorkLine() is { } work) queue.Writer.TryWrite(work);
        if (QueueLine(_queue?.Latest) is { } waiting) queue.Writer.TryWrite(waiting);

        var idle = false;
        using var done = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _logger.LogInformation("A screensaver connected: {Who}.", who);

        try
        {
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

            var send = Task.Run(async () =>
            {
                await foreach (var line in queue.Reader.ReadAllAsync(done.Token)) await writer.WriteLineAsync(line);
            }, CancellationToken.None);

            while (await reader.ReadLineAsync(done.Token) is { } line)
            {
                if (line == "idle" && !idle)
                {
                    idle = true;
                    _presence.ScreensaverRunning(true);
                }
                else if (line == "active" && idle)
                {
                    idle = false;
                    _presence.ScreensaverRunning(false);
                }
                else if (line.StartsWith('{') && _screensavers != null)
                {
                    client.User ??= UserOf(pipe);
                    if (UserScreensavers.Parse(line, client.User) is { } report)
                    {
                        if (!client.Helper) _logger.LogInformation("A screensaver helper reports for {User}: {Who}.", client.User, who);
                        client.Helper = true;
                        _screensavers.Report(client.Id, report, x => queue.Writer.TryWrite(x));
                        if (ErrorOf(line) is { } error) _logger.LogWarning("The screensaver helper for {User} could not make a change: {Error}", client.User, error);
                    }
                }
            }

            await done.CancelAsync();
            try { await send; } catch { /* the screensaver is gone */ }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            lock (_clientsLock) _clients.Remove(client);
            _screensavers?.Disconnected(client.Id);
            queue.Writer.TryComplete();

            //NOTE: A screensaver killed with the user's mouse move never says active; its disconnect does.
            if (idle) _presence.ScreensaverRunning(false);
            _logger.LogInformation("A screensaver disconnected: {Who}.", who);
            await pipe.DisposeAsync();
        }
    }

    private void OnPresenceChanged(object sender, MachinePresence presence) => Broadcast(StateLine());

    private void OnActivity(object sender, ActivityEvent e)
    {
        if (e.Kind == "job-start") _model = e.Model;

        //NOTE: A job for a model not loaded starts by loading it, which is the long wait before the first word.
        if (e.Kind == "job-start" && _loaded != null && !string.IsNullOrEmpty(e.Model) && !_loaded.Loaded.Contains(e.Model, StringComparer.OrdinalIgnoreCase))
        {
            Broadcast(ModelLine("loading", e.Model));
        }

        Broadcast(e.Kind switch
        {
            "job-start" => JsonSerializer.Serialize(new { type = e.Kind, model = e.Model, words = e.Words }, Json),
            "tokens" => JsonSerializer.Serialize(new { type = e.Kind, count = e.Tokens }, Json),
            _ => JsonSerializer.Serialize(new { type = e.Kind, words = e.Words, durationMs = Math.Round(e.DurationMs), workUnits = e.WorkUnits is { } wu ? Math.Round(wu, 2) : (double?)null }, Json)
        });

        if (e.Kind != "tokens") Broadcast(StateLine());
    }

    private string StateLine() => JsonSerializer.Serialize(new
    {
        type = "state",
        presence = $"{_presence.Current}",
        working = _activity.Running > 0,
        machine = Environment.MachineName,
        engine = _engineName,
        model = _model,
        version = Version,
        loaded = _loaded?.Loaded
    }, Json);

    private IReadOnlyList<string> _lastLoaded = [];

    private void OnLoadedChanged(object sender, IReadOnlyList<string> loaded)
    {
        foreach (var model in loaded.Except(_lastLoaded, StringComparer.OrdinalIgnoreCase)) Broadcast(ModelLine("loaded", model));
        foreach (var model in _lastLoaded.Except(loaded, StringComparer.OrdinalIgnoreCase)) Broadcast(ModelLine("unloaded", model));
        _lastLoaded = loaded;
        Broadcast(StateLine());
    }

    private void OnQueueChanged(object sender, QueueSummaryMessage summary)
    {
        if (QueueLine(summary) is { } line) Broadcast(line);
    }

    private void OnWorkChanged(object sender, EventArgs e)
    {
        if (WorkLine() is { } line) Broadcast(line);
    }

    private static string ModelLine(string change, string model) => JsonSerializer.Serialize(new { type = "model", change, model }, Json);

    private string WorkLine()
    {
        if (_work == null) return null;
        var summary = _work.Summary(DateTime.UtcNow);
        return JsonSerializer.Serialize(new
        {
            type = "work",
            workUnitsToday = Math.Round(summary.WorkUnitsToday, 2),
            jobsToday = summary.JobsToday,
            hours = summary.Hours.Select(x => Math.Round(x, 2)),
            days = summary.Days.Select(x => Math.Round(x, 2))
        }, Json);
    }

    private static string QueueLine(QueueSummaryMessage summary) => summary == null ? null : JsonSerializer.Serialize(new
    {
        type = "queue",
        pickable = summary.Pickable,
        unpickable = summary.Unpickable,
        missing = (summary.Missing ?? []).Select(x => new { model = x.Model, jobs = x.Jobs })
    }, Json);

    private void Broadcast(string line)
    {
        lock (_clientsLock)
        {
            //NOTE: A helper has no screen; it is told only what concerns its user's screensaver.
            foreach (var client in _clients.Where(x => !x.Helper)) client.Queue.Writer.TryWrite(line);
        }
    }

    private static string UserOf(NamedPipeServerStream pipe) =>
        GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var session) ? ConsoleSession.UserOf(session) : null;

    private static string ErrorOf(string line)
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            return json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A connection to the pipe: a screensaver, or a helper once it has reported its user's screensaver.</summary>
    private sealed class PipeClient(Guid id, Channel<string> queue)
    {
        public Guid Id { get; } = id;
        public Channel<string> Queue { get; } = queue;
        public volatile bool Helper;
        public string User;
    }

    private static string Describe(NamedPipeServerStream pipe)
    {
        var handle = pipe.SafePipeHandle;
        var pid = GetNamedPipeClientProcessId(handle, out var p) ? p.ToString() : "?";
        var session = GetNamedPipeClientSessionId(handle, out var s) ? s.ToString() : "?";
        return $"session {session}, process {pid}";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);
}
