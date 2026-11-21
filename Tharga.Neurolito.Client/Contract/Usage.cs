using System.Text.Json.Serialization;

namespace Tharga.Neurolito.Client.Contract;

public record Usage
{
    public required int InputTokens { get; init; }
    public required decimal InputDurationMs { get; init; }
    public required int OutputTokens { get; init; }
    public required decimal OutputDurationMs { get; init; }
    public required decimal LoadDurationMs { get; init; }
    public required decimal TotalDurationMs { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public decimal? WorkUnits { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public decimal? Throughput { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Cancelled { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Exception { get; init; }

    /// <summary>What the engine was doing for this run. Null from an agent that does not report it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public EngineRun EngineRun { get; init; }
}