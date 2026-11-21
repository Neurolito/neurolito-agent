using Tharga.Communication.MessageHandler;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Diagnostics;

/// <summary>Answers the server's request for a probe. An agent with no diagnostics registered says so instead of staying silent.</summary>
internal sealed class AgentDiagnosticsRequestHandler : SendMessageHandlerBase<AgentDiagnosticsRequest, AgentDiagnosticsResponse>
{
    private readonly IAgentDiagnostics _diagnostics;

    public AgentDiagnosticsRequestHandler(IAgentDiagnostics diagnostics = null)
    {
        _diagnostics = diagnostics;
    }

    public override Task<AgentDiagnosticsResponse> Handle(AgentDiagnosticsRequest message)
    {
        if (_diagnostics == null) return Task.FromResult(new AgentDiagnosticsResponse { Status = DiagnosticsStatus.Unavailable, Message = "This agent has no diagnostics." });

        return _diagnostics.RunAsync(message, CancellationToken.None);
    }
}

/// <summary>Makes the engine settings change the server asks for and answers with the settings in effect.</summary>
internal sealed class ChangeEngineSettingsRequestHandler : SendMessageHandlerBase<ChangeEngineSettingsRequest, ChangeEngineSettingsResponse>
{
    private readonly IEngineSettingsStore _store;

    public ChangeEngineSettingsRequestHandler(IEngineSettingsStore store = null)
    {
        _store = store;
    }

    public override Task<ChangeEngineSettingsResponse> Handle(ChangeEngineSettingsRequest message)
    {
        if (_store == null) return Task.FromResult(new ChangeEngineSettingsResponse { Status = EngineSettingsStatus.Unavailable, Message = "This agent does not use the llama-server engine, so it has no device or layers to change." });

        return _store.ChangeAsync(message, CancellationToken.None);
    }
}
