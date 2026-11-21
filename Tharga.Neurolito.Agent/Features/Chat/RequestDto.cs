using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Chat;

public record RequestDto
{
    public Guid? RequestId { get; init; }
    public required string Model { get; init; }
    public required Message[] Messages { get; init; }
    public Dictionary<string, string> Tags { get; init; }
    public ResponseInstruction ResponseInstruction { get; init; }
    public Options Options { get; init; }
    public int? TimeoutMinutes { get; init; }

    /// <summary>The server's keep-alive for this agent; the engine keeps the model for the larger of it and the timeout.</summary>
    public int? KeepAliveMinutes { get; init; }
}