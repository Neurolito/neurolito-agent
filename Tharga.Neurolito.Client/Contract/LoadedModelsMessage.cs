using System.Text.Json.Serialization;

namespace Tharga.Neurolito.Client.Contract;

/// <summary>
/// The models an agent has loaded - in memory, ready to answer without a load. Sent whenever that changes, and once
/// after connecting; an empty list means everything was unloaded. Older agents never send it, and their models'
/// <see cref="LLModel.Loaded"/> in the capability response is all the server knows.
/// </summary>
public record LoadedModelsMessage
{
    public required LoadedModel[] Models { get; init; }
}

/// <summary>One loaded model.</summary>
public record LoadedModel
{
    public required string Name { get; init; }

    /// <summary>The memory it takes while loaded, in bytes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SizeBytes { get; init; }

    /// <summary>How much of <see cref="SizeBytes"/> is in video memory. Null when the engine does not say.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? VramBytes { get; init; }

    /// <summary>The context it was loaded with, when the engine says.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ContextLength { get; init; }

    /// <summary>When the engine will unload it by itself if nothing uses it (Ollama's keep-alive). Null when it does not.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ExpiresUtc { get; init; }
}
