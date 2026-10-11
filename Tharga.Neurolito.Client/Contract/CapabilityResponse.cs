using System.Text.Json.Serialization;

namespace Tharga.Neurolito.Client.Contract;

public record CapabilityResponse
{
    public required Engine Engine { get; init; }
    public required Performance Performance { get; init; }
}

public record Performance
{
    public required Memory Memory { get; init; }
    public required Processor Processor { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Gpu Gpu { get; init; }
    public required Machine Machine { get; init; }
}

public record Machine
{
    public required string EnvironmentType { get; init; }
    public string MachineName { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Manufacturer { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Model { get; init; }
    public required TimeSpan Uptime { get; init; }
    public required string CurrentUser { get; init; }
    public required string ProcessArchitecture { get; init; }
    public required string OsPlatform { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string OsDistribution { get; init; }
    public required string OsVersion { get; init; }
}

public record Memory
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double? TotalMemoryGb { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double? AvailableFreeMemoryGb { get; init; }
}

public record Processor
{
    public required int NumberOfCores { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double? ProcessorSpeedGHz { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? PhysicalCpuCores { get; init; }
}

public record Gpu
{
    public required string Name { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double? CoreClockGHz { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double? VideoMemoryGb { get; init; }
}

public record Engine
{
    public required string Name { get; init; }
    public required LLModel[] Models { get; set; }

    /// <summary>
    /// Whether the agent answers <see cref="EngineSelfTestRequest"/>. Older agents leave it false and
    /// are never sent one; the server tries a rested model on them with a real job instead.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SupportsSelfTest { get; init; }

    /// <summary>
    /// The context, in tokens, the agent gives Anthropic-compatible requests, capped per model at what it was
    /// trained for. Null from older agents and from agents that leave the engine's default in place, which for
    /// Ollama keeps only the end of a long prompt.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AnthropicContextLength { get; init; }
}