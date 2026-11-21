namespace Tharga.Neurolito.Agent.Features.Diagnostics;

/// <summary>The <c>Diagnostics</c> section: what the server may read from this agent.</summary>
public record DiagnosticsOptions
{
    public const string SectionName = "Diagnostics";

    /// <summary>The most log lines one probe returns, whatever the server asks for.</summary>
    public int MaxLogLines { get; set; } = 200;

    /// <summary>The most bytes of the log one probe reads.</summary>
    public int MaxLogBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// Where the log files are. Unset, the agent looks where its logging is configured to write and in the two folders the
    /// installers use, and reads the newest file it finds.
    /// </summary>
    public string LogDirectory { get; set; }

    /// <summary>How long a program the agent runs for a probe (<c>nvidia-smi</c>, <c>llama-server --list-devices</c>) may take.</summary>
    public int ProbeTimeoutSeconds { get; set; } = 20;
}
