using Microsoft.AspNetCore.Mvc;
using Tharga.Communication.Client;
using Tharga.Neurolito.Agent.Features.Engine;
using Tharga.Neurolito.Agent.Features.Ollama;

namespace Tharga.Neurolito.Agent.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StatusController : ControllerBase
{
    private readonly ISignalRHostedService _signalRConnectionState;
    private readonly IEngineService _engineService;

    public StatusController(ISignalRHostedService signalRConnectionState, IEngineService engineService)
    {
        _signalRConnectionState = signalRConnectionState;
        _engineService = engineService;
    }

    [HttpGet]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
    {
        var state = _signalRConnectionState.State;
        return Ok($"{state}");
    }

    [HttpGet("capability")]
    public async Task<IActionResult> GetMetrics(CancellationToken cancellationToken)
    {
        var response = await _engineService.BuildCapabilityAsync();
        return Ok(response);
    }

}