using Microsoft.AspNetCore.SignalR.Client;
using Tharga.Communication.Client;
using Tharga.Communication.Client.Communication;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Engine;

/// <summary>Tells the server which models are loaded, whenever that changes.</summary>
public interface ILoadedModelsReporter
{
    /// <summary>Look now rather than at the next check: a job started or ended, so a model may have loaded.</summary>
    void Poke();
}

/// <summary>
/// Watches what the engine has loaded and sends <see cref="LoadedModelsMessage"/> when it changes, and again after every
/// reconnect. Polled, because an engine unloads by itself: Ollama when a model's keep-alive runs out, llama-server
/// when the agent finds it idle. An empty message is the unload.
/// </summary>
internal sealed class LoadedModelsReporter : BackgroundService, ILoadedModelsReporter
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly IEngine _engine;
    private readonly IClientCommunication _communication;
    private readonly ISignalRHostedService _connection;
    private readonly ILogger<LoadedModelsReporter> _logger;
    private readonly SemaphoreSlim _poke = new(0, 1);
    private string _reported;
    private HubConnectionState _lastConnection = HubConnectionState.Disconnected;

    public LoadedModelsReporter(IEngine engine, IClientCommunication communication, ISignalRHostedService connection, ILogger<LoadedModelsReporter> logger)
    {
        _engine = engine;
        _communication = communication;
        _connection = connection;
        _logger = logger;
    }

    public void Poke()
    {
        try { _poke.Release(); }
        catch (SemaphoreFullException) { /* already asked to look */ }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _poke.WaitAsync(Interval, stoppingToken);
                await CheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                //NOTE: An engine that cannot be reached has nothing loaded that the agent could use; say so once.
                _logger.LogDebug(e, "Could not read the loaded models.");
                await SendAsync([], stoppingToken);
            }
        }
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        await SendAsync(await _engine.GetLoadedModelsAsync(cancellationToken), cancellationToken);
    }

    private async Task SendAsync(LoadedModel[] loaded, CancellationToken cancellationToken)
    {
        var state = _connection.State;
        if (state == HubConnectionState.Connected && _lastConnection != HubConnectionState.Connected) _reported = null;
        _lastConnection = state;
        if (state != HubConnectionState.Connected) return;

        //NOTE: What changed is which models, and with what context; memory moves a little all the time and is not news.
        var signature = string.Join("|", loaded.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Name}@{x.ContextLength}"));
        if (signature == _reported) return;

        await _communication.PostAsync(new LoadedModelsMessage { Models = loaded });
        _logger.LogInformation("Loaded models: {Models}.", loaded.Length == 0 ? "none" : signature);
        _reported = signature;
    }
}
