using Microsoft.AspNetCore.Mvc;
using Tharga.Communication.Contract;
using Tharga.Neurolito.Agent.Features.Chat;
using Tharga.Neurolito.Agent.Features.Engine;
using Tharga.Neurolito.Agent.Features.Ollama;
using Tharga.Neurolito.Agent.Framework;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PromptController : ControllerBase
{
    private readonly IEngineService _engineService;

    public PromptController(IEngineService engineService)
    {
        _engineService = engineService;
    }

    [HttpGet]
    [LocalAction]
    public async Task<IActionResult> Get(string prompt, string model = Constants.DefaultModel, CancellationToken cancellationToken = default)
    {
        var request = new RequestDto
        {
            RequestId = Guid.NewGuid(),
            Model = model,
            Messages = [new Message { Role = Role.User, Content = prompt }],
            Options = null,
            TimeoutMinutes = 5,
            ResponseInstruction = null,
            Tags = null,
        };
        var response = await _engineService.GetResponseAsync(request, false, cancellationToken);
        return Ok(response);
    }
}