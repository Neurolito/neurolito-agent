using Tharga.Communication.MessageHandler;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Ollama;

public class UninstallModelRequestHandler : PostMessageHandlerBase<UninstallModelRequest>
{
    private readonly IOllamaService _ollamaService;
    private readonly IContextVariantService _contextVariantService;

    public UninstallModelRequestHandler(IOllamaService ollamaService, IContextVariantService contextVariantService)
    {
        _ollamaService = ollamaService;
        _contextVariantService = contextVariantService;
    }

    public override async Task Handle(UninstallModelRequest message)
    {
        //NOTE: First, because a variant shares the model's files and would keep them on disk after it is gone.
        await _contextVariantService.RemoveVariantsAsync(message.Model, CancellationToken.None);
        await _ollamaService.UninstallModelAsync(message.Model, true, CancellationToken.None);
    }
}