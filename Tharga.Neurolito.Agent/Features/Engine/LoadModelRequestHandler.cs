using Tharga.Communication.MessageHandler;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Engine;

/// <summary>Loads a model when the server expects a request for it soon, so that request does not wait for the load.</summary>
internal class LoadModelRequestHandler : SendMessageHandlerBase<LoadModelRequest, LoadModelResponse>
{
    private readonly IEngineService _engineService;

    public LoadModelRequestHandler(IEngineService engineService)
    {
        _engineService = engineService;
    }

    public override Task<LoadModelResponse> Handle(LoadModelRequest message)
    {
        return _engineService.LoadModelAsync(message.Model, message.KeepAliveMinutes, CancellationToken.None);
    }
}
