using Tharga.Communication.Client.Communication;
using Tharga.Communication.MessageHandler;
using Tharga.Neurolito.Agent.Features.Engine;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Ollama;

internal class CapabilityRequestHandler : PostMessageHandlerBase<CapabilityRequest>
{
    private readonly IEngineService _engineService;
    private readonly IClientCommunication _clientCommunication;

    public CapabilityRequestHandler(IEngineService engineService, IClientCommunication clientCommunication)
    {
        _engineService = engineService;
        _clientCommunication = clientCommunication;
    }

    public override async Task Handle(CapabilityRequest message)
    {
        var capability = await _engineService.BuildCapabilityAsync();
        await _clientCommunication.PostAsync(capability);
    }
}