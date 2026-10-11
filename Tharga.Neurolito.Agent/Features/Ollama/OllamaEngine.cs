using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;
using OllamaSharp.Models.Chat;
using Tharga.Neurolito.Agent.Features.Engine;
using Tharga.Neurolito.Agent.Features.Model;
using Tharga.Neurolito.Client.Contract;
using Message = OllamaSharp.Models.Chat.Message;

namespace Tharga.Neurolito.Agent.Features.Ollama;

/// <summary>The models run by Ollama, reached at <see cref="OllamaOptions.Address"/>.</summary>
internal class OllamaEngine : IEngine
{
    /// <summary>The named HttpClient with no timeout, for calls that last as long as a generation or a download.</summary>
    public const string LongRunningClient = "OllamaLongRunning";

    private readonly OllamaOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IContextVariantService _contextVariantService;

    public OllamaEngine(IOptions<OllamaOptions> options, IHttpClientFactory httpClientFactory, IContextVariantService contextVariantService)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _contextVariantService = contextVariantService;
    }

    public string Kind => EngineKind.Ollama;

    public int? AnthropicContextLength => _options.AnthropicContextLength > 0 ? _options.AnthropicContextLength : null;

    public async Task<string> GetNameAsync(CancellationToken cancellationToken)
    {
        using var client = new OllamaApiClient(_options.Address);
        var version = await client.GetVersionAsync(cancellationToken);
        return version == null ? null : $"Ollama {version}";
    }

    public async Task<string[]> GetModelNamesAsync(CancellationToken cancellationToken)
    {
        using var client = new OllamaApiClient(_options.Address);

        //NOTE: The larger-context copies made for Anthropic requests are an implementation detail, never a model
        //to offer or to resolve a name to.
        return (await client.ListLocalModelsAsync(cancellationToken))
            .Where(m => !ContextVariant.IsVariant(m.Name))
            .Select(m => m.Name)
            .ToArray();
    }

    public async Task<LLModel[]> GetModelsAsync(CancellationToken cancellationToken)
    {
        using var client = new OllamaApiClient(_options.Address);

        var modelsInstalled = (await client.ListLocalModelsAsync(cancellationToken)).Where(x => !ContextVariant.IsVariant(x.Name));
        var modelsRunning = (await client.ListRunningModelsAsync(cancellationToken)).ToArray();

        var result = new List<LLModel>();
        foreach (var modelInstalled in modelsInstalled)
        {
            var modelRunning = modelsRunning.FirstOrDefault(x => x.Name == modelInstalled.Name);
            var modelInfo = await ShowAsync(modelInstalled.Name, cancellationToken);
            result.Add(ModelReport.Build(modelInstalled, modelRunning, modelInfo));
        }

        return result.ToArray();
    }

    public async Task<LoadedModel[]> GetLoadedModelsAsync(CancellationToken cancellationToken)
    {
        using var client = new OllamaApiClient(_options.Address);

        //NOTE: A larger-context variant loaded for an Anthropic request is its model, loaded with a larger context.
        return (await client.ListRunningModelsAsync(cancellationToken))
            .Select(x => new LoadedModel
            {
                Name = ContextVariant.IsVariant(x.Name) ? ContextVariant.BaseOf(x.Name) : x.Name,
                SizeBytes = x.Size > 0 ? x.Size : null,
                VramBytes = x.SizeVram,
                ContextLength = x.ContextLength > 0 ? x.ContextLength : null,
                ExpiresUtc = x.ExpiresAt == default ? null : x.ExpiresAt.ToUniversalTime()
            })
            .ToArray();
    }

    public async Task<EngineModelInfo> GetModelInfoAsync(string model, CancellationToken cancellationToken)
    {
        var m = await ShowAsync(model, cancellationToken);
        var supportsThinking = m.Capabilities?.Any(c => string.Equals(c, "thinking", StringComparison.OrdinalIgnoreCase)) == true;
        return new EngineModelInfo(m.Info.ParameterCount, supportsThinking);
    }

    public async IAsyncEnumerable<double> InstallAsync(string model, ModelSource source, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        //NOTE: Ollama pulls by name from its own library; the source is for engines that download GGUF files.
        using var client = LongRunning();
        await foreach (var progress in client.PullModelAsync(new PullModelRequest { Model = model }, cancellationToken))
        {
            if (progress != null) yield return progress.Percent;
        }
    }

    public async Task UninstallAsync(string model, CancellationToken cancellationToken)
    {
        //NOTE: First, because a variant shares the model's files and would keep them on disk after it is gone.
        await _contextVariantService.RemoveVariantsAsync(model, cancellationToken);

        using var client = LongRunning();
        await client.DeleteModelAsync(new DeleteModelRequest { Model = model }, cancellationToken);
    }

    public async IAsyncEnumerable<EngineChunk> ChatAsync(EngineChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var chatRequest = new ChatRequest
        {
            Model = request.Model,
            Messages = request.Messages.Select(x => new Message
            {
                Role = new ChatRole($"{x.Role}".ToLower()),
                Content = x.Content
            }).ToArray(),
            Options = new RequestOptions
            {
                Temperature = request.Options?.Temperature,
                TopP = request.Options?.TopP,
                TopK = request.Options?.TopK,
                RepeatPenalty = request.Options?.RepeatPenalty,
                NumPredict = request.Options?.NumPredict,
                NumCtx = request.Options?.NumCtx,
                Stop = request.Options?.Stop,
            },
            Stream = true,
            Think = request.Think ? true : null,
            KeepAlive = request.KeepAliveMinutes.HasValue ? $"{request.KeepAliveMinutes}m" : null
        };

        using var client = LongRunning();
        await foreach (var chunk in client.ChatAsync(chatRequest, cancellationToken))
        {
            if (chunk is null) continue;

            if (chunk.Done)
            {
                if (chunk is not ChatDoneResponseStream final) break;

                yield return new EngineChunk
                {
                    Content = chunk.Message?.Content,
                    Thinking = chunk.Message?.Thinking,
                    Final = new EngineUsage
                    {
                        InputTokens = final.PromptEvalCount,
                        OutputTokens = final.EvalCount,
                        LoadDurationMs = final.LoadDuration / 1_000_000m,
                        InputDurationMs = final.PromptEvalDuration / 1_000_000m,
                        OutputDurationMs = final.EvalDuration / 1_000_000m,
                        TotalDurationMs = final.TotalDuration / 1_000_000m
                    }
                };
                yield break;
            }

            yield return new EngineChunk { Content = chunk.Message?.Content, Thinking = chunk.Message?.Thinking };
        }

        throw new InvalidOperationException("Stream ended without a final 'done' message from Ollama.");
    }

    public async Task<AnthropicTarget> ResolveAnthropicAsync(string model, CancellationToken cancellationToken)
    {
        //NOTE: Ollama answers /v1/messages itself. The model is the larger-context variant, because Ollama's own
        //default context would cut the request; see IContextVariantService.
        return new AnthropicTarget(new Uri(_options.Address), await _contextVariantService.ResolveAsync(model, cancellationToken));
    }

    private async Task<ShowModelResponse> ShowAsync(string model, CancellationToken cancellationToken)
    {
        using var client = new OllamaApiClient(_options.Address);
        return await client.ShowModelAsync(new ShowModelRequest { Model = model }, cancellationToken);
    }

    private OllamaApiClient LongRunning() => new(_httpClientFactory.CreateClient(LongRunningClient));
}
