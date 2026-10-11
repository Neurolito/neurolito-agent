using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;
using Quilt4Net.Toolkit.Features.Health.Metrics;
using System.Runtime.CompilerServices;
using Tharga.Communication.Client;
using Tharga.Communication.Client.Communication;
using Tharga.Neurolito.Agent.Features.Ollama;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Model;

internal class OllamaModelService : IOllamaModelService
{
    private readonly IClientCommunication _clientCommunication;
    private readonly IInstanceService _instanceService;
    private readonly IMetricsService _metricsService;
    private readonly OllamaOptions _options;
    private readonly ILogger<OllamaService> _logger;

    public OllamaModelService(IClientCommunication clientCommunication, IInstanceService instanceService, IMetricsService metricsService, IOptions<OllamaOptions> options, ILogger<OllamaService> logger)
    {
        _clientCommunication = clientCommunication;
        _instanceService = instanceService;
        _metricsService = metricsService;
        _options = options.Value;
        _logger = logger;
    }

    public async IAsyncEnumerable<LLModel> GetModelsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var client = new OllamaApiClient(_options.Address);

        //NOTE: The larger-context copies made for Anthropic requests are an implementation detail, never a model
        //to offer or to resolve a name to.
        var modelsInstalled = (await client.ListLocalModelsAsync(cancellationToken)).Where(x => !ContextVariant.IsVariant(x.Name));
        var modelsRunning = (await client.ListRunningModelsAsync(cancellationToken)).ToArray();

        foreach (var modelInstalled in modelsInstalled)
        {
            var modelRunning = modelsRunning.FirstOrDefault(x => x.Name == modelInstalled.Name);
            var modelInfo = await GetModelAsync(modelInstalled.Name);

            var result = Features.Model.ModelReport.Build(modelInstalled, modelRunning, modelInfo);

            yield return result;
        }
    }

    private async Task<ShowModelResponse> GetModelAsync(string model)
    {
        using var client = new OllamaApiClient(_options.Address);
        var response = await client.ShowModelAsync(new ShowModelRequest { Model = model });
        return response;
    }

    public async IAsyncEnumerable<PullModelResponse> InstallModelAsync(string model, bool respondToServer, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _logger.LogInformation("Start to install model '{model}'.", model);

        var request = new PullModelRequest
        {
            Model = model
        };

        using var client = new OllamaApiClient(_options.Address);
        await foreach (var progress in client.PullModelAsync(request, cancellationToken))
        {
            yield return progress;
            if (progress != null)
            {
                if (respondToServer)
                {
                    await _clientCommunication.PostAsync(new InstallProgressResponse
                    {
                        Instance = _instanceService.AgentInstanceKey,
                        Percent = progress.Percent
                    });
                }
            }
        }

        if (respondToServer)
        {
            await _clientCommunication.PostAsync(await BuildCapabilityAsync());
        }

        _logger.LogInformation("Completed installation of model '{model}'.", model);
    }

    public async Task UninstallModelAsync(string model, bool respondToServer, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Start to uninstall model '{model}'.", model);

        var request = new DeleteModelRequest
        {
            Model = model
        };

        using var client = new OllamaApiClient(_options.Address);
        await client.DeleteModelAsync(request, cancellationToken);

        if (respondToServer)
        {
            await _clientCommunication.PostAsync(await BuildCapabilityAsync());
        }

        _logger.LogInformation("Completed uninstallation of model '{model}'.", model);
    }

    public async Task<CapabilityResponse> BuildCapabilityAsync()
    {
        var capability = await BuildEngine();

        var metrics = await _metricsService.GetMetricsAsync();

        var machine = new Machine
        {
            EnvironmentType = $"{metrics.Machine.Identity.EnvironmentType}",
            MachineName = metrics.Machine.Identity.MachineName,
            Manufacturer = metrics.Machine.Identity.Manufacturer,
            Model = metrics.Machine.Identity.Model,
            Uptime = metrics.Machine.Lifecycle.Uptime,
            CurrentUser = metrics.Machine.Runtime.CurrentUser,
            ProcessArchitecture = metrics.Machine.Runtime.ProcessArchitecture,
            OsPlatform = metrics.Machine.OperatingSystem.Platform,
            OsDistribution = metrics.Machine.OperatingSystem.Distribution,
            OsVersion = metrics.Machine.OperatingSystem.Version,
        };

        var memory = new Memory
        {
            TotalMemoryGb = metrics.Memory.TotalMemoryGb,
            AvailableFreeMemoryGb = metrics.Memory.AvailableFreeMemoryGb,
        };

        var gpu = metrics.Gpu == null
            ? null
            : new Gpu
            {
                Name = metrics.Gpu.Name,
                CoreClockGHz = metrics.Gpu.CoreClockGHz,
                VideoMemoryGb = metrics.Gpu.VideoMemoryGb,
            };

        var processor = new Processor
        {
            NumberOfCores = metrics.Processor.NumberOfCores,
            PhysicalCpuCores = metrics.Processor.PhysicalCpuCores,
            ProcessorSpeedGHz = metrics.Processor.ProcessorSpeedGHz,
        };

        var performance = new Performance
        {
            Machine = machine,
            Memory = memory,
            Processor = processor,
            Gpu = gpu
        };

        var response = new CapabilityResponse
        {
            Engine = capability,
            Performance = performance
        };

        return response;
    }

    private async Task<Engine> BuildEngine()
    {
        Engine engine;
        try
        {
            var models = await GetModelsAsync(CancellationToken.None).ToArrayAsync();
            var version = await GetVersionAsync(CancellationToken.None);

            engine = new Engine
            {
                Name = version == null ? null : $"Ollama {version}",
                Models = models.ToArray(),
                SupportsSelfTest = true,
                AnthropicContextLength = _options.AnthropicContextLength > 0 ? _options.AnthropicContextLength : null,
            };
        }
        catch (Exception e)
        {
            _logger?.LogError(e, e.Message);
            engine = new Engine
            {
                Models = [],
                Name = null,
            };
        }

        return engine;
    }

    public async Task<string> GetVersionAsync(CancellationToken cancellationToken)
    {
        using var client = new OllamaApiClient(_options.Address);
        var version = await client.GetVersionAsync(cancellationToken);
        return version;
    }
}