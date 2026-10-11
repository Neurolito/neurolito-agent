using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using Tharga.Communication.Client;
using Tharga.Neurolito.Agent.Features.Engine;

namespace Tharga.Neurolito.Agent.Features.Model;

/// <summary>
/// Installs the models named in <c>Ollama:Models</c> (<c>LlamaServer:Models</c> for llama-server) when the agent starts, so a fresh agent — a new
/// container, a reimaged machine — can take work without someone installing a model by hand first.
/// </summary>
/// <remarks>
/// An agent with no installed model is not merely idle: <c>QueueEngine.DispatchIdleAgentsOnceAsync</c>
/// skips it entirely, so it connects, reports healthy, and never receives a prompt.
/// </remarks>
internal class ModelPreloadService : BackgroundService
{
    private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan _engineTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan _connectionTimeout = TimeSpan.FromMinutes(2);

    private readonly IServiceProvider _serviceProvider;
    private readonly ISignalRHostedService _signalR;
    private readonly ModelPreloadOptions _options;
    private readonly ILogger<ModelPreloadService> _logger;

    public ModelPreloadService(IServiceProvider serviceProvider, ISignalRHostedService signalR, IOptions<ModelPreloadOptions> options, ILogger<ModelPreloadService> logger)
    {
        _serviceProvider = serviceProvider;
        _signalR = signalR;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wanted = (_options.Models ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (wanted.Length == 0) return;

        _logger.LogInformation("Model preload configured for {count} model(s): {models}.", wanted.Length, string.Join(", ", wanted));

        using var scope = _serviceProvider.CreateScope();
        var modelService = scope.ServiceProvider.GetRequiredService<IEngineService>();

        if (!await WaitForEngineAsync(modelService, stoppingToken))
        {
            _logger.LogError("Model preload abandoned — the engine did not become reachable within {timeout}.", _engineTimeout);
            return;
        }

        // Installing reports the refreshed capability back to the server, which needs a live
        // connection. Without it the server keeps its empty model list and never dispatches here.
        await WaitForConnectionAsync(stoppingToken);

        foreach (var model in wanted)
        {
            if (stoppingToken.IsCancellationRequested) return;

            try
            {
                if (await IsInstalledAsync(modelService, model, stoppingToken))
                {
                    _logger.LogInformation("Model '{model}' is already installed.", model);
                    continue;
                }

                _logger.LogInformation("Installing model '{model}'...", model);
                await foreach (var _ in modelService.InstallModelAsync(model, null, true, stoppingToken)) { }
                _logger.LogInformation("Model '{model}' installed.", model);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // One bad model name must not stop the rest, and must not take the agent down.
                _logger.LogError(e, "Failed to preload model '{model}'.", model);
            }
        }
    }

    private async Task<bool> WaitForEngineAsync(IEngineService modelService, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _engineTimeout;
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                await modelService.GetModelsAsync(cancellationToken).ToArrayAsync(cancellationToken);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch
            {
                await Task.Delay(_pollInterval, cancellationToken);
            }
        }

        return false;
    }

    private async Task WaitForConnectionAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _connectionTimeout;
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (_signalR.State == HubConnectionState.Connected) return;
            await Task.Delay(_pollInterval, cancellationToken);
        }

        // Proceed regardless. The pull is still worth doing; the capability post may fail, and the
        // next reconnect reports the model anyway.
        _logger.LogWarning("Preloading without a live server connection — state is {state}.", _signalR.State);
    }

    private static async Task<bool> IsInstalledAsync(IEngineService modelService, string model, CancellationToken cancellationToken)
    {
        var installed = await modelService.GetModelsAsync(cancellationToken).ToArrayAsync(cancellationToken);
        return installed.Any(x => IsSameModel(x?.Name, model));
    }

    private static bool IsSameModel(string installed, string wanted)
    {
        if (string.IsNullOrWhiteSpace(installed)) return false;
        if (installed.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return true;

        // Ollama reports an untagged pull as ":latest"; treat "name" and "name:latest" as one model.
        return Bare(installed).Equals(Bare(wanted), StringComparison.OrdinalIgnoreCase);

        static string Bare(string value) => value.EndsWith(":latest", StringComparison.OrdinalIgnoreCase)
            ? value[..^":latest".Length]
            : value;
    }
}

/// <summary>The models <see cref="ModelPreloadService"/> installs at startup.</summary>
public record ModelPreloadOptions
{
    public string[] Models { get; init; } = [];
}
