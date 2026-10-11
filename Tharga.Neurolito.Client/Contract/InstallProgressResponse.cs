namespace Tharga.Neurolito.Client.Contract;

public record InstallProgressResponse
{
    public required Guid Instance { get; init; }

    /// <summary>How far the install is; 100 when it has ended, installed or not.</summary>
    public double Percent { get; init; }

    /// <summary>The model being installed. Null from older agents.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string Model { get; init; }

    /// <summary>The models waiting to be installed after it, in order. Null from older agents.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string[] Queued { get; init; }
}