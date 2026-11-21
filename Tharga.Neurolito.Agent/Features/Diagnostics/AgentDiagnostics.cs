using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Options;
using Tharga.Neurolito.Agent.Features.LlamaServer;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Diagnostics;

/// <summary>Runs one probe from the closed list in <see cref="DiagnosticsProbes"/>. There is no way to run anything else.</summary>
public interface IAgentDiagnostics
{
    Task<AgentDiagnosticsResponse> RunAsync(AgentDiagnosticsRequest request, CancellationToken cancellationToken);
}

/// <inheritdoc />
internal sealed class AgentDiagnostics : IAgentDiagnostics
{
    private const string NoLlamaServer = "This agent does not use the llama-server engine.";

    private readonly DiagnosticsOptions _options;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AgentDiagnostics> _logger;
    private readonly LlamaServerHost _host;
    private readonly LlamaServerOptions _llama;

    public AgentDiagnostics(IOptions<DiagnosticsOptions> options, IConfiguration configuration, ILogger<AgentDiagnostics> logger, LlamaServerHost host = null, IOptions<LlamaServerOptions> llama = null)
    {
        _options = options.Value;
        _configuration = configuration;
        _logger = logger;
        _host = host;
        _llama = llama?.Value;
    }

    public async Task<AgentDiagnosticsResponse> RunAsync(AgentDiagnosticsRequest request, CancellationToken cancellationToken)
    {
        var probe = DiagnosticsProbes.All.FirstOrDefault(x => string.Equals(x, request?.Probe?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (probe == null)
        {
            return new AgentDiagnosticsResponse { Status = DiagnosticsStatus.UnknownProbe, Message = $"There is no probe '{request?.Probe}'. The probes are: {string.Join(", ", DiagnosticsProbes.All)}." };
        }

        _logger.LogInformation("The server asked for the {Probe} probe.", probe);
        try
        {
            var reply = probe switch
            {
                DiagnosticsProbes.Log => Log(request),
                DiagnosticsProbes.Devices => await DevicesAsync(cancellationToken),
                DiagnosticsProbes.Gpu => await GpuAsync(cancellationToken),
                DiagnosticsProbes.Engine => await EngineAsync(cancellationToken),
                _ => await VersionAsync(cancellationToken)
            };

            return reply with { Probe = probe, Text = SecretMask.Apply(reply.Text, _llama?.HuggingFaceToken) };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "The {Probe} probe failed.", probe);
            return new AgentDiagnosticsResponse { Status = DiagnosticsStatus.Failed, Probe = probe, Message = e.Message };
        }
    }

    private AgentDiagnosticsResponse Log(AgentDiagnosticsRequest request)
    {
        var path = LogTail.Newest(LogDirectories());
        if (path == null) return Unavailable("No log file was found.");

        var lines = Math.Clamp(request.Lines ?? _options.MaxLogLines, 1, _options.MaxLogLines);
        var text = string.Join('\n', LogTail.Read(path, lines, _options.MaxLogBytes));
        return new AgentDiagnosticsResponse { Status = DiagnosticsStatus.Ok, Text = text, Message = $"The last {lines} lines at most of {Path.GetFileName(path)}." };
    }

    private IEnumerable<string> LogDirectories()
    {
        if (!string.IsNullOrWhiteSpace(_options.LogDirectory))
        {
            yield return _options.LogDirectory;
            yield break;
        }

        foreach (var section in _configuration.GetSection("Serilog:WriteTo").GetChildren())
        {
            if (section["Args:path"] is { Length: > 0 } configured) yield return Path.GetDirectoryName(configured);
        }

        var data = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        yield return Path.Combine(data, "Neurolito", "logs");
        yield return Path.Combine(data, "Thargelion AB", "Tharga.Neurolito.Agent", "logs");
    }

    private async Task<AgentDiagnosticsResponse> DevicesAsync(CancellationToken cancellationToken)
    {
        if (_host == null) return Unavailable(NoLlamaServer);

        var output = await _host.ListDevicesAsync(cancellationToken);
        if (output == null) return Unavailable($"llama-server could not be run: {_llama?.ResolvedExecutablePath}.");

        var listed = DeviceList.Parse(output);
        var text = new StringBuilder();
        text.AppendLine($"Device setting: {_llama?.Device ?? "(none: llama-server chooses)"}");
        text.AppendLine(listed.Count == 0 ? "No devices parsed from llama-server's output; raw output follows." : "Devices llama-server lists:");
        foreach (var d in listed) text.AppendLine($"  {d.Name}: {d.Description} ({d.TotalMiB} MiB, {d.FreeMiB} MiB free)");
        if (listed.Count == 0) text.AppendLine(output);
        return Ok(text);
    }

    private async Task<AgentDiagnosticsResponse> GpuAsync(CancellationToken cancellationToken)
    {
        const string query = "index,name,driver_version,memory.total,memory.used,utilization.gpu,temperature.gpu";
        var run = await ProbeProcess.RunAsync("nvidia-smi", ["--query-gpu=" + query, "--format=csv"], TimeSpan.FromSeconds(_options.ProbeTimeoutSeconds), cancellationToken);
        if (run.NotFound) return Unavailable("nvidia-smi is not installed on this machine, so there is no NVIDIA card or driver to report on.");
        if (run.ExitCode != 0) return new AgentDiagnosticsResponse { Status = DiagnosticsStatus.Failed, Message = $"nvidia-smi exited with {run.ExitCode}: {run.Output.Trim()}" };

        return Ok(new StringBuilder(run.Output.Trim()));
    }

    private async Task<AgentDiagnosticsResponse> EngineAsync(CancellationToken cancellationToken)
    {
        if (_host == null || _llama == null) return Unavailable(NoLlamaServer);

        var text = new StringBuilder();
        text.AppendLine($"Engine: {await _host.GetVersionAsync(cancellationToken) ?? "llama-server (version unknown)"}");
        text.AppendLine($"Program: {_llama.ResolvedExecutablePath} ({(File.Exists(_llama.ResolvedExecutablePath) ? "found" : "NOT FOUND")})");
        text.AppendLine($"Device: {_llama.Device ?? "(none: llama-server chooses)"}");
        text.AppendLine($"GPU layers: {(_llama.GpuLayers is { } layers ? layers.ToString() : "(none: llama-server fits)")}");
        text.AppendLine($"Context: {_llama.ContextLength}, Anthropic context: {_llama.AnthropicContextLength}, parallel: {_llama.Parallel}, idle unload: {_llama.IdleUnloadMinutes} min");
        text.AppendLine($"Extra arguments: {(string.IsNullOrWhiteSpace(_llama.ExtraArguments) ? "(none)" : _llama.ExtraArguments)}");
        text.AppendLine($"Loaded now: {(_host.LoadedModel == null ? "nothing" : $"{_host.LoadedModel}, context {_host.LoadedContextLength}")}");
        text.AppendLine($"Last start: {_host.LastStart ?? "(llama-server has not been started since the agent did)"}");

        var output = _host.RecentOutput();
        text.AppendLine(output.Length == 0 ? "Last output: (none)" : "Last output:");
        foreach (var line in output) text.AppendLine($"  {line}");
        return Ok(text);
    }

    private async Task<AgentDiagnosticsResponse> VersionAsync(CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        text.AppendLine($"Agent: {Assembly.GetEntryAssembly()?.GetName().Version ?? typeof(AgentDiagnostics).Assembly.GetName().Version}");
        if (_host != null) text.AppendLine($"Engine: {await _host.GetVersionAsync(cancellationToken) ?? "llama-server (version unknown)"}");
        text.AppendLine($"System: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), {Environment.ProcessorCount} cores");
        text.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        return Ok(text);
    }

    private static AgentDiagnosticsResponse Ok(StringBuilder text) => new() { Status = DiagnosticsStatus.Ok, Text = text.ToString().TrimEnd() };

    private static AgentDiagnosticsResponse Unavailable(string message) => new() { Status = DiagnosticsStatus.Unavailable, Message = message };
}

/// <summary>Runs one fixed program with fixed arguments and returns what it printed. Never a shell, never a caller's text.</summary>
internal static class ProbeProcess
{
    public sealed record Result(int ExitCode, string Output, bool NotFound);

    public static async Task<Result> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(start)!;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);

            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var error = process.StandardError.ReadToEndAsync(limit.Token);
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(true);
                return new Result(-1, $"{program} did not finish within {timeout.TotalSeconds:0} seconds.", false);
            }

            return new Result(process.ExitCode, await output + await error, false);
        }
        catch (Win32Exception)
        {
            return new Result(-1, string.Empty, true);
        }
    }
}
