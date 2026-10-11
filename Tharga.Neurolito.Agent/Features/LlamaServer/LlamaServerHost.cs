using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Tharga.Neurolito.Agent.Features.LlamaServer;

/// <summary>
/// Runs <c>llama-server</c> as a child process, one model at a time, on a local port nobody else is told about.
/// </summary>
/// <remarks>
/// llama-server loads one model when it starts, so changing model, or giving it a larger context, means starting it
/// again - the same as Ollama swapping models on a small GPU. A request holds a <see cref="Lease"/> while it runs, and a
/// swap waits until no lease is held, so a model is never pulled out from under an answer. With nothing to do for
/// <see cref="LlamaServerOptions.IdleUnloadMinutes"/> the process is stopped and its memory given back.
/// </remarks>
internal sealed class LlamaServerHost : IHostedService, IAsyncDisposable
{
    public const string ClientName = "LlamaServer";
    private static readonly TimeSpan IdleCheck = TimeSpan.FromSeconds(15);
    private const int OutputLines = 40;

    private readonly LlamaServerOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LlamaServerHost> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<string> _output = new();
    private readonly CancellationTokenSource _stopping = new();

    private Process _process;
    private Uri _address;
    private string _model;
    private int _contextLength;
    private int _leases;
    private DateTime _lastUsed = DateTime.UtcNow;
    private string _version;
    private Task _idleLoop;

    public LlamaServerHost(IOptions<LlamaServerOptions> options, IHttpClientFactory httpClientFactory, ILogger<LlamaServerHost> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>The model loaded now, or null.</summary>
    public string LoadedModel => _process is { HasExited: false } ? _model : null;

    /// <summary>The context the loaded model was started with, or 0.</summary>
    public int LoadedContextLength => _process is { HasExited: false } ? _contextLength : 0;

    /// <summary>"llama.cpp b11541", from <c>llama-server --version</c>. Null when the program is missing or does not run.</summary>
    public async Task<string> GetVersionAsync(CancellationToken cancellationToken)
    {
        if (_version != null) return _version;

        var path = _options.ResolvedExecutablePath;
        if (!File.Exists(path)) return null;

        try
        {
            var start = new ProcessStartInfo(path, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(start)!;
            var text = await process.StandardError.ReadToEndAsync(cancellationToken) + await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            _version = VersionOf(text);
            return _version;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Could not run {Path}.", path);
            return null;
        }
    }

    /// <summary>
    /// "llama.cpp b11541" from <c>version: 0.6.0-dev (build 11541, commit f2918cabb)</c>; older builds print
    /// <c>version: 4589 (abc1234)</c>. The build number is what llama.cpp's releases are named by.
    /// </summary>
    internal static string VersionOf(string output)
    {
        var line = output?.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith("version:", StringComparison.OrdinalIgnoreCase));
        if (line == null) return "llama.cpp";

        var build = System.Text.RegularExpressions.Regex.Match(line, @"build (\d+)");
        if (build.Success) return $"llama.cpp b{build.Groups[1].Value}";

        var number = line["version:".Length..].Trim().Split(' ')[0];
        return string.IsNullOrEmpty(number) ? "llama.cpp" : $"llama.cpp {(char.IsDigit(number[0]) && !number.Contains('.') ? "b" : "")}{number}";
    }

    /// <summary>
    /// llama-server running <paramref name="name"/> from <paramref name="path"/> with at least
    /// <paramref name="contextLength"/>, started or restarted if needed. Dispose the lease when the request is done.
    /// </summary>
    public async Task<Lease> AcquireAsync(string name, string path, int contextLength, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var reuse = _process is { HasExited: false }
                        && string.Equals(_model, name, StringComparison.OrdinalIgnoreCase)
                        && _contextLength >= contextLength;

            if (!reuse)
            {
                await WaitForLeasesAsync(cancellationToken);
                await StopProcessAsync("loading another model or a larger context");
                await StartProcessAsync(name, path, contextLength, cancellationToken);
            }

            Interlocked.Increment(ref _leases);
            return new Lease(this, _address, reuse ? TimeSpan.Zero : started.Elapsed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops llama-server if it runs <paramref name="name"/>, or whatever it runs when <paramref name="name"/> is null.</summary>
    public async Task UnloadAsync(string name, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (name != null && !string.Equals(_model, name, StringComparison.OrdinalIgnoreCase)) return;
            await WaitForLeasesAsync(cancellationToken);
            await StopProcessAsync("unloaded");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Release()
    {
        _lastUsed = DateTime.UtcNow;
        Interlocked.Decrement(ref _leases);
    }

    private async Task WaitForLeasesAsync(CancellationToken cancellationToken)
    {
        while (Volatile.Read(ref _leases) > 0) await Task.Delay(200, cancellationToken);
    }

    private async Task StartProcessAsync(string name, string path, int contextLength, CancellationToken cancellationToken)
    {
        var executable = _options.ResolvedExecutablePath;
        if (!File.Exists(executable)) throw new FileNotFoundException($"llama-server is not at {executable}. Set LlamaServer:ExecutablePath.", executable);

        var port = FreePort();
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var argument in new[]
                 {
                     "-m", path, "--alias", name, "--jinja",
                     "-ngl", $"{_options.GpuLayers}", "-c", $"{contextLength}", "--parallel", $"{Math.Max(1, _options.Parallel)}",
                     "--host", "127.0.0.1", "--port", $"{port}"
                 }.Concat((_options.ExtraArguments ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)))
        {
            start.ArgumentList.Add(argument);
        }

        lock (_output) _output.Clear();
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Remember(e.Data);
        process.ErrorDataReceived += (_, e) => Remember(e.Data);
        process.Start();
        if (OperatingSystem.IsWindows() && !ChildProcessJob.Add(process)) _logger.LogWarning("llama-server (pid {Pid}) is not tied to the agent; if the agent is killed it keeps running until the agent starts again.", process.Id);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _process = process;
        _model = name;
        _contextLength = contextLength;
        _address = new Uri($"http://127.0.0.1:{port}/");
        _logger.LogInformation("Starting llama-server for {Model} with a context of {ContextLength} on port {Port} (pid {Pid}).", name, contextLength, port, process.Id);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, _options.LoadTimeoutMinutes)));
        var client = _httpClientFactory.CreateClient(ClientName);
        try
        {
            while (true)
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException($"llama-server exited with code {process.ExitCode} while loading {name}: {LastOutput()}");
                }

                try
                {
                    using var response = await client.GetAsync(new Uri(_address, "health"), timeout.Token);
                    if (response.StatusCode == HttpStatusCode.OK) break;
                }
                catch (HttpRequestException)
                {
                    // Not listening yet.
                }

                await Task.Delay(250, timeout.Token);
            }
        }
        catch (Exception e)
        {
            await StopProcessAsync("failed to load");
            if (e is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                throw new TimeoutException($"llama-server did not load {name} within {_options.LoadTimeoutMinutes} minutes: {LastOutput()}");
            throw;
        }

        _logger.LogInformation("llama-server is ready with {Model}.", name);
    }

    private async Task StopProcessAsync(string reason)
    {
        var process = _process;
        _process = null;
        _address = null;
        var model = _model;
        _model = null;
        _contextLength = 0;

        if (process == null) return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
            }

            _logger.LogInformation("Stopped llama-server with {Model}: {Reason}.", model, reason);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not stop llama-server (pid {Pid}).", process.Id);
        }
        finally
        {
            process.Dispose();
        }
    }

    private void Remember(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        _logger.LogDebug("llama-server: {Line}", line);
        lock (_output)
        {
            _output.Enqueue(line);
            while (_output.Count > OutputLines) _output.Dequeue();
        }
    }

    private string LastOutput()
    {
        lock (_output)
        {
            var errors = _output.Where(x => x.Contains("error", StringComparison.OrdinalIgnoreCase) || x.Contains("failed", StringComparison.OrdinalIgnoreCase)).ToArray();
            return string.Join(" | ", (errors.Length > 0 ? errors : _output.ToArray()).TakeLast(5));
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StopOrphans();
        if (_options.IdleUnloadMinutes > 0) _idleLoop = Task.Run(() => UnloadWhenIdleAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task UnloadWhenIdleAsync(CancellationToken cancellationToken)
    {
        var idle = TimeSpan.FromMinutes(_options.IdleUnloadMinutes);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(IdleCheck, cancellationToken);
                if (_process == null || Volatile.Read(ref _leases) > 0 || DateTime.UtcNow - _lastUsed < idle) continue;

                await _gate.WaitAsync(cancellationToken);
                try
                {
                    if (_process != null && Volatile.Read(ref _leases) == 0 && DateTime.UtcNow - _lastUsed >= idle)
                        await StopProcessAsync($"idle for {_options.IdleUnloadMinutes} minutes");
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "The idle check of llama-server failed.");
            }
        }
    }

    /// <summary>A llama-server left running by an agent that did not stop cleanly holds the GPU's memory. Ours are the ones started from our program.</summary>
    private void StopOrphans()
    {
        var executable = Path.GetFullPath(_options.ResolvedExecutablePath);
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            try
            {
                if (!string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) continue;
                process.Kill(true);
                _logger.LogWarning("Stopped a llama-server left running (pid {Pid}).", process.Id);
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "Could not inspect process {Pid}.", process.Id);
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        if (_idleLoop != null) await _idleLoop;
        await StopProcessAsync("the agent is stopping");
    }

    public async ValueTask DisposeAsync()
    {
        await StopProcessAsync("the agent is stopping");
        _stopping.Dispose();
        _gate.Dispose();
    }

    /// <summary>A running llama-server, held for one request. The model stays loaded until every lease is disposed.</summary>
    public sealed class Lease : IAsyncDisposable
    {
        private readonly LlamaServerHost _host;
        private int _disposed;

        internal Lease(LlamaServerHost host, Uri address, TimeSpan loadTime)
        {
            _host = host;
            Address = address;
            LoadTime = loadTime;
        }

        public Uri Address { get; }

        /// <summary>How long this request waited for the model to load. Zero when it was already loaded.</summary>
        public TimeSpan LoadTime { get; }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _host.Release();
            return ValueTask.CompletedTask;
        }
    }
}
