using System.Text.Json.Serialization;

namespace Tharga.Neurolito.Client.Contract;

public record ModelDetails
{
    public required string ParameterSize { get; init; }
    public long? ParameterCount { get; init; }

    /// <summary>
    /// The parameters one token runs through: <see cref="ParameterCount"/> for a dense model, less for a
    /// mixture-of-experts model, which routes each token through a few of its experts. Null from older agents.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long? ActiveParameterCount { get; init; }

    /// <summary>
    /// The model's architecture as its GGUF names it, for example <c>qwen3</c> or <c>llama</c>. With
    /// <see cref="Quantization"/> and <see cref="ParameterCount"/> it tells whether two names are the same model on
    /// different engines. Null from older agents.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Architecture { get; init; }

    /// <summary>How the weights are quantised, for example <c>Q4_K_M</c> or <c>Q8_0</c>. Null from older agents.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Quantization { get; init; }

    /// <summary>The model's size on disk, in bytes. Null from older agents.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long? SizeBytes { get; init; }

    /// <summary>The memory the model takes while loaded, in bytes. Null when it is not loaded, or from older agents.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long? LoadedSizeBytes { get; init; }

    /// <summary>
    /// How much of <see cref="LoadedSizeBytes"/> sits in video memory, in bytes. The rest runs on the CPU. Null when
    /// the model is not loaded, or from older agents.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long? LoadedVramBytes { get; init; }
}
