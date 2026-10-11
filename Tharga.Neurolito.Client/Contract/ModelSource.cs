using System.Text.Json.Serialization;

namespace Tharga.Neurolito.Client.Contract;

/// <summary>
/// Where an engine that runs GGUF files itself (llama-server) downloads a model, and the sampling defaults the model
/// comes with. Ollama ignores it: it pulls a model by its name.
/// </summary>
/// <remarks>
/// Pinned by <see cref="Revision"/> and <see cref="Sha256"/>, so a file re-uploaded under the same name fails the check
/// instead of silently changing the model a name stands for.
/// </remarks>
public record ModelSource
{
    /// <summary>The Hugging Face repository, for example <c>unsloth/Qwen3-1.7B-GGUF</c>.</summary>
    public required string Repository { get; init; }

    /// <summary>The GGUF file in the repository, for example <c>Qwen3-1.7B-Q4_K_M.gguf</c>.</summary>
    public required string File { get; init; }

    /// <summary>The commit to download from. Null downloads from <c>main</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Revision { get; init; }

    /// <summary>The file's SHA-256, lower-case hex. Null skips the check.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Sha256 { get; init; }

    /// <summary>The file's size in bytes, when known.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SizeBytes { get; init; }

    /// <summary>Applied when a prompt sets none of its own, as Ollama applies a model's own parameters.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SamplingDefaults Defaults { get; init; }
}

/// <summary>A model's own sampling settings, used for any option a prompt leaves out.</summary>
public record SamplingDefaults
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Temperature { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? TopP { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TopK { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? RepeatPenalty { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[] Stop { get; init; }
}
