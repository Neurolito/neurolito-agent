using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Tharga.Communication.Contract;
using Tharga.Neurolito.Agent.Features.Engine;
using Tharga.Neurolito.Agent.Features.Ollama;
using Tharga.Neurolito.Agent.Framework;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EngineController : ControllerBase
{
    private readonly IEngineService _engineService;
    private readonly ILogger<EngineController> _logger;

    public EngineController(IEngineService engineService, ILogger<EngineController> logger)
    {
        _engineService = engineService;
        _logger = logger;
    }

    [HttpGet("model")]
    public async Task<IActionResult> GetModels(CancellationToken cancellationToken)
    {
        var models = await _engineService.GetModelsAsync(cancellationToken).ToArrayAsync(cancellationToken);
        return Ok(new ModelResponse { Models = models });
    }

    [HttpGet("model/install/{model}")]
    [LocalAction]
    public async Task InstallModel(string model = Constants.DefaultModel, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(model)) model = Uri.UnescapeDataString(model);

        Response.Headers.Add("Content-Type", "text/event-stream");
        Response.Headers.Add("Cache-Control", "no-cache");
        Response.Headers.Add("Connection", "keep-alive");

        await foreach (var progress in _engineService.InstallModelAsync(model, null, false, cancellationToken))
        {
            var json = JsonSerializer.Serialize(progress);

            await Response.WriteAsync($"data: {json}\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
            _logger.LogDebug($"data: {json}");
        }
    }

    [HttpGet("model/uninstall/{model}")]
    [LocalAction]
    public async Task<IActionResult> UninstallModel(string model = Constants.DefaultModel, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(model)) model = Uri.UnescapeDataString(model);

        await _engineService.UninstallModelAsync(model, false, cancellationToken);
        return Ok();
    }

    [HttpGet("version")]
    public async Task<IActionResult> GetVersion(CancellationToken cancellationToken)
    {
        var response = await _engineService.GetVersionAsync(cancellationToken);
        return Ok(response);
    }
}