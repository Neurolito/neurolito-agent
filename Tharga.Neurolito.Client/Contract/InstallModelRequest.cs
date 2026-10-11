using System.Text.Json.Serialization;

namespace Tharga.Neurolito.Client.Contract;

public record InstallModelRequest
{
    public required string Model { get; init; }

    /// <summary>
    /// Where to download the model from, for an agent whose engine runs GGUF files itself. Null for Ollama, which pulls
    /// by name, and from older servers.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModelSource Source { get; init; }
}