using Tharga.Communication.MessageHandler;
using Tharga.Neurolito.Agent.Features.Engine;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Ollama;

public class InstallModelRequestHandler : PostMessageHandlerBase<InstallModelRequest>
{
    private readonly IEngineService _engineService;
    private readonly ILogger<InstallModelRequestHandler> _logger;

    public InstallModelRequestHandler(IEngineService engineService, ILogger<InstallModelRequestHandler> logger = null)
    {
        _engineService = engineService;
        _logger = logger;
    }

    public override async Task Handle(InstallModelRequest message)
    {
        try
        {
            await _engineService.InstallModelAsync(message.Model, message.Source, true, CancellationToken.None).ToArrayAsync();
        }
        catch (OperationCanceledException)
        {
            //NOTE: Cancelled by an uninstall of the same model; the server has been told the install ended.
            _logger?.LogInformation("The install of model '{model}' was cancelled.", message.Model);
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Could not install model '{model}': {message}", message.Model, e.Message);
        }
    }
}