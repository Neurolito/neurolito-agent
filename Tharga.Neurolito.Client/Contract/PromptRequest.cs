namespace Tharga.Neurolito.Client.Contract;

public record PromptRequest
{
    public required Guid RequestId { get; init; }
    public string Model { get; init; }
    public required Message[] Messages { get; init; }
    public Dictionary<string, string> Tags { get; init; }
    public ResponseInstruction ResponseInstruction { get; init; }
    public Options Options { get; init; }
    public int? TimeoutMinutes { get; init; }

    /// <summary>How long the server wants the engine to keep the model loaded after the run, from the agent's setting. Null leaves it to the timeout.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? KeepAliveMinutes { get; init; }
}