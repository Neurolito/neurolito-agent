using Microsoft.Extensions.Options;

namespace Tharga.Neurolito.Agent.Features.Diagnostics;

public static class DiagnosticsRegistration
{
    /// <summary>The probes the server may ask for. Called after the engine, whose host and settings the probes read.</summary>
    public static void AddDiagnostics(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(DiagnosticsOptions.SectionName).Get<DiagnosticsOptions>() ?? new DiagnosticsOptions();
        builder.Services.AddSingleton(Options.Create(options));
        builder.Services.AddSingleton<IAgentDiagnostics, AgentDiagnostics>();
    }
}
