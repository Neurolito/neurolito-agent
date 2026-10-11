using Tharga.Communication.MessageHandler;
using Tharga.Neurolito.Agent.Features.Chat;
using Tharga.Neurolito.Agent.Features.Engine;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Ollama;

internal class PromptRequestHandler : SendMessageHandlerBase<PromptRequest, AgentPromptResponse>
{
    private readonly IEngineService _engineService;

    public PromptRequestHandler(IEngineService engineService)
    {
        _engineService = engineService;
    }

    public override async Task<AgentPromptResponse> Handle(PromptRequest message)
    {
        var request = new RequestDto
        {
            RequestId = message.RequestId,
            Model = message.Model,
            Messages = message.Messages,
            Tags = message.Tags,
            ResponseInstruction = message.ResponseInstruction,
            Options = message.Options,
            TimeoutMinutes = message.TimeoutMinutes
        };

        return await _engineService.GetResponseAsync(request, true, CancellationToken.None);
    }
}