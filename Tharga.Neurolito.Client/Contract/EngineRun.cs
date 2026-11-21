using System.Text.Json.Serialization;

namespace Tharga.Neurolito.Client.Contract;

/// <summary>
/// What the engine was doing for one run: which engine, and how the model sat in memory when the answer was done.
/// Every field is optional, since an engine says only what it knows; an agent that predates it sends none of it.
/// </summary>
public record EngineRun
{
    /// <summary>The engine and its version, for example "Ollama 0.40.1".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Engine { get; init; }

    /// <summary>The memory the model takes while loaded, in bytes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SizeBytes { get; init; }

    /// <summary>How much of <see cref="SizeBytes"/> is in video memory. The rest runs on the CPU.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? VramBytes { get; init; }

    /// <summary>The context the model was loaded with, in tokens.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ContextLength { get; init; }

    /// <summary>When the engine will unload the model by itself if nothing uses it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ExpiresUtc { get; init; }

    /// <summary>How long the agent asked the engine to keep the model loaded after this answer.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? KeepAliveMinutes { get; init; }

    /// <summary>
    /// The share of the model that is in video memory, 0 to 1. Under 1 means it runs partly on the CPU, which is slow.
    /// Null when the engine did not say how large the model is or how much of it is in video memory.
    /// </summary>
    [JsonIgnore]
    public decimal? VramShare => SizeBytes is > 0 && VramBytes is { } vram ? Math.Min(1m, (decimal)vram / SizeBytes.Value) : null;
}
