using System.IO.Pipes;
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
/// <c>active</c>. It is told <c>state</c> (presence, working, machine, engine, model) on connecting and on every
/// change, and <c>job-start</c> (model, words in), <c>tokens</c> and <c>job-end</c> (words out, duration) as work
/// happens. Never prompt or answer text.</para>
/// <para>Local only: authenticated users on this machine may connect, the network may not. Each client has its own
/// queue, so a screensaver that stops reading cannot hold up the agent.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class StatusPipeServer : BackgroundService
{
    private const int QueueLength = 1000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly DesktopOptions _options;
    private readonly IPresenceMonitor _presence;
    private readonly IAgentActivity _activity;
    private readonly IEngine _engine;
    private readonly ILogger<StatusPipeServer> _logger;
    private readonly Lock _clientsLock = new();
    private readonly List<Channel<string>> _clients = [];

    private string _engineName;
    private string _model;

    public StatusPipeServer(IOptions<DesktopOptions> options, IPresenceMonitor presence, IAgentActivity activity, IEngine engine, ILogger<StatusPipeServer> logger)
    {
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
        lock (_clientsLock) _clients.Add(queue);
        queue.Writer.TryWrite(StateLine());

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
            }

            await done.CancelAsync();
            try { await send; } catch { /* the screensaver is gone */ }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            lock (_clientsLock) _clients.Remove(queue);
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

        Broadcast(e.Kind switch
        {
            "job-start" => JsonSerializer.Serialize(new { type = e.Kind, model = e.Model, words = e.Words }, Json),
            "tokens" => JsonSerializer.Serialize(new { type = e.Kind, count = e.Tokens }, Json),
            _ => JsonSerializer.Serialize(new { type = e.Kind, words = e.Words, durationMs = Math.Round(e.DurationMs) }, Json)
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
        model = _model
    }, Json);

    private void Broadcast(string line)
    {
        lock (_clientsLock)
        {
            foreach (var client in _clients) client.Writer.TryWrite(line);
        }
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
