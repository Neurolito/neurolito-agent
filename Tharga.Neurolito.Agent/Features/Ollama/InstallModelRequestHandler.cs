using Tharga.Communication.MessageHandler;
using Tharga.Neurolito.Agent.Features.Engine;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Ollama;

public class InstallModelRequestHandler : PostMessageHandlerBase<InstallModelRequest>
{
    private readonly IEngineService _engineService;

    public InstallModelRequestHandler(IEngineService engineService)
    {
        _engineService = engineService;
    }

    public override async Task Handle(InstallModelRequest message)
    {
        await _engineService.InstallModelAsync(message.Model, message.Source, true, CancellationToken.None).ToArrayAsync();
    }
}