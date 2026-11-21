using System.Collections.Concurrent;
using System.Globalization;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Engine;

/// <summary>The words an agent logs about one run, kept apart from the service so they can be read and tested.</summary>
internal static class RunReport
{
    /// <summary>One line: what the run was, how fast, and how the model sat in memory.</summary>
    public static string Describe(string model, int outputTokens, decimal outputMs, decimal loadMs, EngineRun run)
    {
        var speed = outputMs > 0 ? outputTokens / (outputMs / 1000m) : 0m;
        var parts = new List<string>
        {
            $"Run of '{model}': {outputTokens} tokens at {Format(speed)} tokens/s",
            $"load {Format(loadMs)} ms"
        };
        if (run?.VramShare is { } share) parts.Add($"{Math.Round(share * 100, 0)}% in video memory");
        if (run?.ContextLength is { } context) parts.Add($"context {context}");
        if (run?.Engine is { } engine) parts.Add(engine);
        if (run?.KeepAliveMinutes is { } keep) parts.Add($"keep-alive {keep} min");
        return string.Join(", ", parts) + ".";
    }

    /// <summary>A warning when part of the model runs on the CPU, which is the usual reason a good graphics card is slow. Null otherwise.</summary>
    public static string PartialCpuWarning(string model, EngineRun run) =>
        run?.VramShare is { } share and < 1m
            ? $"'{model}' runs partly on the CPU, only {Math.Round(share * 100, 0)}% is in video memory. Expect it to be slow; free video memory or use a smaller model."
            : null;

    internal static string Format(decimal value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}

/// <summary>
/// Remembers the best warm speed of each model since the agent started, and says so when a run falls far below it.
/// A run that had to load the model is neither compared nor remembered, since loading is slow by nature.
/// </summary>
internal sealed class PaceWatch
{
    private const decimal DropBelow = 0.5m;
    private const decimal ColdLoadMs = 1000m;
    private readonly ConcurrentDictionary<string, decimal> _best = new(StringComparer.OrdinalIgnoreCase);

    /// <returns>A warning when this run is under half the model's best speed so far, otherwise null.</returns>
    public string Observe(string model, decimal tokensPerSecond, decimal loadMs)
    {
        if (tokensPerSecond <= 0 || loadMs > ColdLoadMs) return null;

        var best = _best.GetOrAdd(model, tokensPerSecond);
        if (tokensPerSecond > best)
        {
            _best[model] = tokensPerSecond;
            return null;
        }

        return tokensPerSecond < best * DropBelow
            ? $"'{model}' ran at {RunReport.Format(tokensPerSecond)} tokens/s, far below the {RunReport.Format(best)} it has reached since this agent started. Something changed on this machine."
            : null;
    }
}
