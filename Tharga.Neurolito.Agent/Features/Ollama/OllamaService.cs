using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;
using OllamaSharp.Models.Chat;
using Quilt4Net.Toolkit.Features.Health.Metrics;
using Quilt4Net.Toolkit.Features.Measure;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Tharga.Communication.Client;
using Tharga.Communication.Client.Communication;
using Tharga.Neurolito.Agent.Features.Chat;
using Tharga.Neurolito.Client.Contract;
using Message = OllamaSharp.Models.Chat.Message;

namespace Tharga.Neurolito.Agent.Features.Ollama;

internal class OllamaService : IOllamaService, IDisposable
{
    private readonly IClientCommunication _clientCommunication;
    private readonly IMetricsService _metricsService;
    private readonly IInstanceService _instanceService;
    private readonly IEventService _eventService;
    private readonly OllamaOptions _options;
    private readonly ILogger<OllamaService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>The named HttpClient with no timeout, for calls that last as long as a generation or a download.</summary>
    public const string LongRunningClient = "OllamaLongRunning";

    private readonly CancellationTokenSource _cancellationTokenSource;

    public OllamaService(IClientCommunication clientCommunication, IMetricsService metricsService, IInstanceService instanceService, IEventService eventService, IOptions<OllamaOptions> options, ILogger<OllamaService> logger, IHttpClientFactory httpClientFactory)
    {
        _clientCommunication = clientCommunication;
        _metricsService = metricsService;
        _instanceService = instanceService;
        _eventService = eventService;
        _options = options.Value;
        _logger = logger;
        _httpClientFactory = httpClientFactory;

        _cancellationTokenSource = new CancellationTokenSource();

        _eventService.CancelEvent += OnCancelEvent;
    }

    private void OnCancelEvent(object sender, EventArgs e)
    {
        _cancellationTokenSource.Cancel();
    }

    public async Task<string> GetVersionAsync(CancellationToken cancellationToken)
    {
        using var client = new OllamaApiClient(_options.Address);
        var version = await client.GetVersionAsync(cancellationToken);
        return version;
    }

    public async IAsyncEnumerable<LLModel> GetModelsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var client = new OllamaApiClient(_options.Address);

        var modelsInstalled = await client.ListLocalModelsAsync(cancellationToken);
        var modelsRunning = (await client.ListRunningModelsAsync(cancellationToken)).ToArray();

        foreach (var modelInstalled in modelsInstalled)
        {
            var modelRunning = modelsRunning.FirstOrDefault(x => x.Name == modelInstalled.Name);
            var modelInfo = await GetModelAsync(modelInstalled.Name);

            var result = Features.Model.ModelReport.Build(modelInstalled, modelRunning, modelInfo);

            yield return result;
        }
    }

    private async Task<string> ResolveModelNameAsync(string model, CancellationToken cancellationToken = default)
    {
        using var client = new OllamaApiClient(_options.Address);
        var installed = (await client.ListLocalModelsAsync(cancellationToken))
            .Select(m => m.Name)
            .ToArray();

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

    private async Task<ShowModelResponse> GetModelAsync(string model)
    {
        using var client = new OllamaApiClient(_options.Address);
        var response = await client.ShowModelAsync(new ShowModelRequest { Model = model });
        return response;
    }

    public async IAsyncEnumerable<PullModelResponse> InstallModelAsync(string model, bool  respondToServer, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _logger.LogInformation("Start to install model '{model}'.", model);

        var request = new PullModelRequest
        {
            Model = model
        };

        using var client = LongRunning();
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

        using var client = LongRunning();
        await client.DeleteModelAsync(request, cancellationToken);

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

        try
        {
            _logger.LogInformation("OllamaService instance id: {id}", GetHashCode());

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

            var m = await GetModelAsync(model);
            var modelParameterCount = m.Info.ParameterCount;
            var supportsThinking = m.Capabilities?.Any(c => string.Equals(c, "thinking", StringComparison.OrdinalIgnoreCase)) == true;

            var messages = request.Messages.Select(x => new Message
            {
                Role = new ChatRole($"{x.Role}".ToLower()),
                Content = x.Content
            }).ToArray();

            var options = new RequestOptions
            {
                Temperature = request.Options?.Temperature, // ?? 0.1f,
                TopP = request.Options?.TopP, // ?? 0.9f,
                TopK = request.Options?.TopK, // ?? 40,
                RepeatPenalty = request.Options?.RepeatPenalty, // ?? 1.05f,
                NumPredict = request.Options?.NumPredict, // ?? 24,
                NumCtx = request.Options?.NumCtx, // ?? 4096,
                Stop = request.Options?.Stop,
            };

            var chatRequest = new ChatRequest
            {
                Model = model,
                Messages = messages,
                Options = options,
                Stream = true,
                Think = supportsThinking ? true : null,
                KeepAlive = request.TimeoutMinutes.HasValue ? $"{request.TimeoutMinutes}m" : null
            };

            using var ollamaClient = LongRunning();
            var output = new StringBuilder();
            ChatDoneResponseStream final = null;

            var sw = new Stopwatch();
            sw.Start();

            await foreach (var chunk in ollamaClient.ChatAsync(chatRequest, ct))
            {
                if (chunk is null) continue;

                if (chunk.Message.Thinking is { Length: > 0 } thought)
                {
                    _logger.LogDebug("Thinking: {thought}", thought);
                }

                if (chunk.Message.Content is { Length: > 0 } part)
                {
                    output.Append(part);
                    Console.Write(part);

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

                if (chunk.Done)
                {
                    final = chunk as ChatDoneResponseStream;
                    break;
                }
            }

            if (final is null) throw new InvalidOperationException("Stream ended without a final 'done' message from Ollama.");

            var tokensPerSecond = 0m;
            if (final.EvalDuration > 0 && final.EvalCount > 0)
            {
                tokensPerSecond = final.EvalCount / (final.EvalDuration / 1_000_000_000m);
            }

            var loadDurationMs = final.LoadDuration / 1_000_000m;
            var inputDurationMs = final.PromptEvalDuration / 1_000_000m;
            var outputDurationMs = final.EvalDuration / 1_000_000m;
            var workDurationMs = inputDurationMs + outputDurationMs;
            var totalDurationMs = final.TotalDuration / 1_000_000m;

            var workUnits = CalculateWorkUnits(final.PromptEvalCount + final.EvalCount, modelParameterCount);
            var throughput = workUnits / workDurationMs * 1000;

            _logger.LogInformation("Time: Completed in {elapsed}ms. Load time {loadTime}ms, input time {inputTime}ms, {outputTime}ms", totalDurationMs, loadDurationMs, inputDurationMs, outputDurationMs);
            _logger.LogInformation("WU: Used {workUnits} work units with a throughput of {throughput} work units per second. TokensPerSecond: {tokensPerSecond}.", workUnits, throughput, tokensPerSecond);
            _logger.LogInformation("Token: Used {tokens}, with {inputTokens} for input and {outputTokens} for output.", final.PromptEvalCount + final.EvalCount, final.PromptEvalCount, final.EvalCount);

            var logData = new LogData(new Dictionary<string, object> { { "model", model }, { "machine", Environment.MachineName } });

            _logger.Count($"{nameof(OllamaService)}.{nameof(GetResponseAsync)}.Tokens", final.PromptEvalCount + final.EvalCount, logData: logData);
            _logger.Count($"{nameof(OllamaService)}.{nameof(GetResponseAsync)}.TokensPerSecond", (int)tokensPerSecond, logData: logData);
            if (throughput.HasValue) _logger.Count($"{nameof(OllamaService)}.{nameof(GetResponseAsync)}.Throughput", (int)throughput.Value, logData: logData);
            if (workUnits.HasValue) _logger.Count($"{nameof(OllamaService)}.{nameof(GetResponseAsync)}.WorkUnits", (int)workUnits.Value, logData: logData);
            _logger.Elapsed($"{nameof(OllamaService)}.{nameof(GetResponseAsync)}.LoadTime", TimeSpan.FromMilliseconds((double)loadDurationMs), logData: logData);
            _logger.Elapsed($"{nameof(OllamaService)}.{nameof(GetResponseAsync)}.TotalDuration", TimeSpan.FromMilliseconds((double)totalDurationMs), logData: logData);

            if (respondToServer)
            {
                await _clientCommunication.PostAsync(new PromptFragmentResponse
                {
                    Instance = _instanceService.AgentInstanceKey,
                    RequestId = request.RequestId ?? Guid.Empty,
                    Output = null,
                    Tokens = final.PromptEvalCount + final.EvalCount,
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
                    InputTokens = final.PromptEvalCount,
                    InputDurationMs = inputDurationMs,
                    OutputTokens = final.EvalCount,
                    OutputDurationMs = outputDurationMs,
                    LoadDurationMs = loadDurationMs,
                    TotalDurationMs = totalDurationMs,
                    WorkUnits = workUnits,
                    Throughput = workUnits / workDurationMs * 1000,
                },
                ResponseInstruction = request.ResponseInstruction
            };
        }
        catch (TaskCanceledException)
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
    }

    private OllamaApiClient LongRunning() => new(_httpClientFactory.CreateClient(LongRunningClient));

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

    public void Dispose()
    {
        _eventService.CancelEvent -= OnCancelEvent;
        _cancellationTokenSource?.Dispose();
    }
}