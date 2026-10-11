using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Quilt4Net.Toolkit.Features.Health.Metrics;
using Quilt4Net.Toolkit.Features.Measure;
using Tharga.Communication.Client;
using Tharga.Communication.Client.Communication;
using Tharga.Neurolito.Agent.Features.Chat;
using Tharga.Neurolito.Agent.Features.Desktop;
using Tharga.Neurolito.Agent.Features.Ollama;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Engine;

/// <summary>
/// What the agent does with its engine: answers prompts, installs and removes models, and tells the server what it has.
/// The same for every engine; what differs between them is behind <see cref="IEngine"/>.
/// </summary>
public interface IEngineService
{
    /// <summary>The engine and its version, for example "Ollama 0.40.1". Throws when the engine cannot be reached.</summary>
    Task<string> GetVersionAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<LLModel> GetModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>Installs a model, yielding the percent done. <paramref name="source"/> is where to download it from, for an engine that needs one.</summary>
    IAsyncEnumerable<double> InstallModelAsync(string model, ModelSource source, bool respondToServer, CancellationToken cancellationToken);

    Task UninstallModelAsync(string model, bool respondToServer, CancellationToken cancellationToken);

    Task<AgentPromptResponse> GetResponseAsync(RequestDto request, bool respondToServer, CancellationToken cancellationToken);

    Task<CapabilityResponse> BuildCapabilityAsync();
}

internal class EngineService : IEngineService, IDisposable
{
    private readonly IEngine _engine;
    private readonly IClientCommunication _clientCommunication;
    private readonly IMetricsService _metricsService;
    private readonly IInstanceService _instanceService;
    private readonly IEventService _eventService;
    private readonly ILogger<EngineService> _logger;
    private readonly IAgentActivity _activity;
    private readonly ILoadedModelsReporter _loadedModels;
    private readonly Desktop.IQueueSummaryStore _queueSummary;

    private readonly CancellationTokenSource _cancellationTokenSource;

    public EngineService(IEngine engine, IClientCommunication clientCommunication, IMetricsService metricsService, IInstanceService instanceService, IEventService eventService, ILogger<EngineService> logger, IAgentActivity activity = null, ILoadedModelsReporter loadedModels = null, Desktop.IQueueSummaryStore queueSummary = null)
    {
        _activity = activity;
        _queueSummary = queueSummary;
        _loadedModels = loadedModels;
        _engine = engine;
        _clientCommunication = clientCommunication;
        _metricsService = metricsService;
        _instanceService = instanceService;
        _eventService = eventService;
        _logger = logger;

        _cancellationTokenSource = new CancellationTokenSource();

        _eventService.CancelEvent += OnCancelEvent;
    }

    private void OnCancelEvent(object sender, EventArgs e)
    {
        _cancellationTokenSource.Cancel();
    }

    public async Task<string> GetVersionAsync(CancellationToken cancellationToken)
    {
        return await _engine.GetNameAsync(cancellationToken) ?? throw new InvalidOperationException($"The {_engine.Kind} engine cannot be reached.");
    }

    public async IAsyncEnumerable<LLModel> GetModelsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var model in await _engine.GetModelsAsync(cancellationToken))
        {
            yield return model;
        }
    }

    private async Task<string> ResolveModelNameAsync(string model, CancellationToken cancellationToken = default)
    {
        var installed = await _engine.GetModelNamesAsync(cancellationToken);

        if (installed.Length == 0)
            throw new InvalidOperationException("No models are installed.");

        if (string.IsNullOrWhiteSpace(model))
            return installed[Random.Shared.Next(installed.Length)];

        if (installed.Any(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase)))
            return model;

        var matches = installed
            .Where(m => m.StartsWith(model + ":", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length == 0)
            throw new InvalidOperationException($"No model matching '{model}' is installed.");

        return matches[Random.Shared.Next(matches.Length)];
    }

    public async IAsyncEnumerable<double> InstallModelAsync(string model, ModelSource source, bool respondToServer, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _logger.LogInformation("Start to install model '{model}'.", model);

        await foreach (var percent in _engine.InstallAsync(model, source, cancellationToken))
        {
            yield return percent;
            if (respondToServer)
            {
                await _clientCommunication.PostAsync(new InstallProgressResponse
                {
                    Instance = _instanceService.AgentInstanceKey,
                    Percent = percent
                });
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

        await _engine.UninstallAsync(model, cancellationToken);

        if (respondToServer)
        {
            await _clientCommunication.PostAsync(await BuildCapabilityAsync());
        }

        _logger.LogInformation("Completed uninstallation of model '{model}'.", model);
    }

    public async Task<AgentPromptResponse> GetResponseAsync(RequestDto request, bool respondToServer, CancellationToken cancellationToken)
    {
        decimal? wu = null;
        var tokens = 0;
        TimeSpan elapsedTotal = default;
        var model = request.Model;
        var tags = request.Tags;
        var started = false;
        var output = new StringBuilder();
        var sw = new Stopwatch();

        try
        {
            _logger.LogInformation("EngineService instance id: {id}", GetHashCode());

            if (respondToServer)
            {
                await _clientCommunication.PostAsync(new PromptFragmentResponse
                {
                    Instance = _instanceService.AgentInstanceKey,
                    RequestId = request.RequestId ?? Guid.Empty,
                    Output = string.Empty,
                    Tokens = 0,
                    WorkUnits = 0,
                    Throughput = 0,
                    TotalDurationMs = 0
                });
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellationTokenSource.Token);
            var ct = linkedCts.Token;

            model = await ResolveModelNameAsync(model, ct);

            _logger.LogInformation("Request for model '{model}'.", model);

            var info = await _engine.GetModelInfoAsync(model, ct);
            var modelParameterCount = info.ParameterCount;

            var chatRequest = new EngineChatRequest
            {
                Model = model,
                Messages = request.Messages,
                Options = request.Options,
                Think = info.SupportsThinking,
                KeepAliveMinutes = request.TimeoutMinutes
            };

            EngineUsage final = null;

            sw.Start();

            //NOTE: The shape of the job for the screensaver: its model and how many words, never the words.
            _activity?.JobStarted(model, request.Messages.Sum(x => AgentActivity.WordsIn(x.Content)));
            started = true;

            var first = true;
            await foreach (var chunk in _engine.ChatAsync(chatRequest, ct))
            {
                //NOTE: The first chunk means the model is loaded; the server hears it now rather than at the next check.
                if (first)
                {
                    first = false;
                    _loadedModels?.Poke();
                }

                if (chunk.Thinking is { Length: > 0 } thought)
                {
                    _logger.LogDebug("Thinking: {thought}", thought);

                    //NOTE: Thinking is work too, and the screen shows the work; it counts, as answer tokens do.
                    _activity?.Tokens(1);
                }

                if (chunk.Content is { Length: > 0 } part)
                {
                    output.Append(part);
                    Console.Write(part);
                    _activity?.Tokens(1);

                    tokens++;
                    wu = CalculateWorkUnits(tokens, modelParameterCount);
                    elapsedTotal = sw.Elapsed;

                    if (respondToServer)
                    {
                        await _clientCommunication.PostAsync(new PromptFragmentResponse
                        {
                            Instance = _instanceService.AgentInstanceKey,
                            RequestId = request.RequestId ?? Guid.Empty,
                            Output = part,
                            Tokens = tokens,
                            WorkUnits = wu,
                            Throughput = wu.HasValue ? wu.Value / (decimal)elapsedTotal.TotalSeconds : null,
                            TotalDurationMs = (decimal)elapsedTotal.TotalMilliseconds
                        });
                    }
                }

                if (chunk.Final != null)
                {
                    final = chunk.Final;
                    break;
                }
            }

            if (final is null) throw new InvalidOperationException($"Stream ended without a final message from the {_engine.Kind} engine.");

            var tokensPerSecond = 0m;
            if (final.OutputDurationMs > 0 && final.OutputTokens > 0)
            {
                tokensPerSecond = final.OutputTokens / (final.OutputDurationMs / 1000m);
            }

            var loadDurationMs = final.LoadDurationMs;
            var inputDurationMs = final.InputDurationMs;
            var outputDurationMs = final.OutputDurationMs;
            var workDurationMs = inputDurationMs + outputDurationMs;
            var totalDurationMs = final.TotalDurationMs;

            var workUnits = CalculateWorkUnits(final.InputTokens + final.OutputTokens, modelParameterCount);
            var throughput = workDurationMs > 0 ? workUnits / workDurationMs * 1000 : null;
            wu = workUnits;

            _logger.LogInformation("Time: Completed in {elapsed}ms. Load time {loadTime}ms, input time {inputTime}ms, {outputTime}ms", totalDurationMs, loadDurationMs, inputDurationMs, outputDurationMs);
            _logger.LogInformation("WU: Used {workUnits} work units with a throughput of {throughput} work units per second. TokensPerSecond: {tokensPerSecond}.", workUnits, throughput, tokensPerSecond);
            _logger.LogInformation("Token: Used {tokens}, with {inputTokens} for input and {outputTokens} for output.", final.InputTokens + final.OutputTokens, final.InputTokens, final.OutputTokens);

            var logData = new LogData(new Dictionary<string, object> { { "model", model }, { "machine", Environment.MachineName }, { "engine", _engine.Kind } });

            //NOTE: The metric names keep the class the agent had before the engine seam, so the dashboards built on them continue.
            const string metric = "OllamaService.GetResponseAsync";
            _logger.Count($"{metric}.Tokens", final.InputTokens + final.OutputTokens, logData: logData);
            _logger.Count($"{metric}.TokensPerSecond", (int)tokensPerSecond, logData: logData);
            if (throughput.HasValue) _logger.Count($"{metric}.Throughput", (int)throughput.Value, logData: logData);
            if (workUnits.HasValue) _logger.Count($"{metric}.WorkUnits", (int)workUnits.Value, logData: logData);
            _logger.Elapsed($"{metric}.LoadTime", TimeSpan.FromMilliseconds((double)loadDurationMs), logData: logData);
            _logger.Elapsed($"{metric}.TotalDuration", TimeSpan.FromMilliseconds((double)totalDurationMs), logData: logData);

            if (respondToServer)
            {
                await _clientCommunication.PostAsync(new PromptFragmentResponse
                {
                    Instance = _instanceService.AgentInstanceKey,
                    RequestId = request.RequestId ?? Guid.Empty,
                    Output = null,
                    Tokens = final.InputTokens + final.OutputTokens,
                    WorkUnits = workUnits,
                    Throughput = throughput,
                    TotalDurationMs = (decimal)elapsedTotal.TotalMilliseconds
                });
            }

            return new AgentPromptResponse
            {
                Model = model,
                Output = output.ToString(),
                Tags = tags,
                Usage = new Usage
                {
                    InputTokens = final.InputTokens,
                    InputDurationMs = inputDurationMs,
                    OutputTokens = final.OutputTokens,
                    OutputDurationMs = outputDurationMs,
                    LoadDurationMs = loadDurationMs,
                    TotalDurationMs = totalDurationMs,
                    WorkUnits = workUnits,
                    Throughput = throughput,
                },
                ResponseInstruction = request.ResponseInstruction
            };
        }
        catch (OperationCanceledException)
        {
            return new AgentPromptResponse
            {
                Model = model,
                Output = null,
                Tags = tags,
                Usage = new Usage
                {
                    InputTokens = 0,
                    InputDurationMs = 0,
                    OutputTokens = tokens,
                    OutputDurationMs = 0,
                    LoadDurationMs = 0,
                    TotalDurationMs = 0,
                    WorkUnits = wu,
                    Cancelled = true
                }
            };
        }
        catch (Exception e)
        {
            _logger.LogError(e, e.Message);
            return new AgentPromptResponse
            {
                Model = model,
                Output = null,
                Tags = tags,
                Usage = new Usage
                {
                    InputTokens = 0,
                    InputDurationMs = 0,
                    OutputTokens = tokens,
                    OutputDurationMs = 0,
                    LoadDurationMs = 0,
                    TotalDurationMs = 0,
                    WorkUnits = wu,
                    Exception = e.Message
                }
            };
        }
        finally
        {
            if (started) _activity?.JobEnded(AgentActivity.WordsIn(output.ToString()), sw.Elapsed.TotalMilliseconds, (double?)wu);
        }
    }

    private static decimal? CalculateWorkUnits(int tokens, long? modelParameterCount)
    {
        return (decimal?)(tokens * (modelParameterCount / 1e9));
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

        return new CapabilityResponse
        {
            Engine = capability,
            Performance = performance
        };
    }

    private async Task<Client.Contract.Engine> BuildEngine()
    {
        try
        {
            var models = await _engine.GetModelsAsync(CancellationToken.None);
            var name = await _engine.GetNameAsync(CancellationToken.None);

            return new Client.Contract.Engine
            {
                Name = name,
                Kind = _engine.Kind,
                Models = models,
                SupportsSelfTest = true,
                SupportsQueueSummary = _queueSummary != null,
                AnthropicContextLength = _engine.AnthropicContextLength,
            };
        }
        catch (Exception e)
        {
            _logger?.LogError(e, e.Message);
            return new Client.Contract.Engine
            {
                Models = [],
                Name = null,
                Kind = _engine.Kind,
            };
        }
    }

    public void Dispose()
    {
        _eventService.CancelEvent -= OnCancelEvent;
        _cancellationTokenSource?.Dispose();
    }
}
