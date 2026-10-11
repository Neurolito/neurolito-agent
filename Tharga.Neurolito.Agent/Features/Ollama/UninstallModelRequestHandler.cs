using Tharga.Communication.MessageHandler;
using Tharga.Neurolito.Agent.Features.Engine;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Ollama;

public class UninstallModelRequestHandler : PostMessageHandlerBase<UninstallModelRequest>
{
    private readonly IEngineService _engineService;

    public UninstallModelRequestHandler(IEngineService engineService)
    {
        _engineService = engineService;
    }

    public override async Task Handle(UninstallModelRequest message)
    {
        await _engineService.UninstallModelAsync(message.Model, true, CancellationToken.None);
    }
}