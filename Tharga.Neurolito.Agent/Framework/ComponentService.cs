using Microsoft.AspNetCore.SignalR.Client;
using Quilt4Net.Toolkit;
using Quilt4Net.Toolkit.Features.Health;
using Tharga.Communication.Client;
using Tharga.Neurolito.Agent.Features.Engine;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Framework;

internal class ComponentService : IComponentService
{
    private readonly IEngineService _engineService;
    private readonly IEngine _engine;
    private readonly ISignalRHostedService _signalRConnectionState;

    public ComponentService(IEngineService engineService, IEngine engine, ISignalRHostedService signalRConnectionState)
    {
        _engineService = engineService;
        _engine = engine;
        _signalRConnectionState = signalRConnectionState;
    }

    public IEnumerable<Component> GetComponents()
    {
        yield return new Component
        {
            //NOTE: "Ollama" for an Ollama agent, as the component has always been called, so its health history continues.
            Name = _engine.Kind == EngineKind.Ollama ? "Ollama" : _engine.Kind,
            Essential = true,
            CheckAsync = async _ =>
            {
                try
                {
                    var response = await _engineService.GetVersionAsync();
                    return new CheckResult
                    {
                        Success = true,
                        Message = $"{response} available."
                    };
                }
                catch (Exception e)
                {
                    return new CheckResult
                    {
                        Success = false,
                        Message = e.Message
                    };
                }
            }
        };

        yield return new Component
        {
            Name = "Server",
            Essential = true,
            CheckAsync = _ => Task.FromResult(new CheckResult
            {
                Success = _signalRConnectionState.State == HubConnectionState.Connected,
                Message = $"Server status is {_signalRConnectionState.State}."
            })
        };

    }
}