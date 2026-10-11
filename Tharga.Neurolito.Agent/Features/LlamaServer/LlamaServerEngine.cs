using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Tharga.Neurolito.Agent.Features.Engine;
using Tharga.Neurolito.Agent.Features.Model;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.LlamaServer;

/// <summary>
/// The models run by llama.cpp's own <c>llama-server</c>, which this agent starts itself: no Ollama, no Docker.
/// </summary>
/// <remarks>
/// Models keep the names the server gives them (<c>qwen3:1.7b</c>), and their files come from Hugging Face: the
/// server says where (<see cref="ModelSource"/>), or the name says it (<c>hf.co/{owner}/{repo}:{quantisation}</c>).
/// The chat template is the one in the GGUF file, rendered by llama-server with <c>--jinja</c>.
/// </remarks>
internal sealed class LlamaServerEngine : IEngine
{
    private readonly LlamaServerHost _host;
    private readonly ModelStore _store;
    private readonly HuggingFaceClient _huggingFace;
    private readonly LlamaServerOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LlamaServerEngine> _logger;
    private readonly SemaphoreSlim _install = new(1, 1);

    public LlamaServerEngine(LlamaServerHost host, ModelStore store, HuggingFaceClient huggingFace, IOptions<LlamaServerOptions> options, IHttpClientFactory httpClientFactory, ILogger<LlamaServerEngine> logger)
    {
        _host = host;
        _store = store;
        _huggingFace = huggingFace;
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string Kind => EngineKind.LlamaServer;

    public int? AnthropicContextLength => _options.AnthropicContextLength > 0 ? _options.AnthropicContextLength : null;

    public Task<string> GetNameAsync(CancellationToken cancellationToken) => _host.GetVersionAsync(cancellationToken);

    public Task<string[]> GetModelNamesAsync(CancellationToken cancellationToken) => Task.FromResult(_store.List().Select(x => x.Name).ToArray());

    public Task<LLModel[]> GetModelsAsync(CancellationToken cancellationToken)
    {
        var loaded = _host.LoadedModel;
        return Task.FromResult(_store.List().Select(x =>
        {
            var isLoaded = string.Equals(x.Name, loaded, StringComparison.OrdinalIgnoreCase);
            return new LLModel
            {
                Name = x.Name,
                Loaded = isLoaded,
                Details = new ModelDetails
                {
                    ParameterSize = x.ParameterSize,
                    ParameterCount = x.ParameterCount,
                    ActiveParameterCount = x.ActiveParameterCount,
                    SizeBytes = x.SizeBytes,
                    LoadedSizeBytes = isLoaded ? x.SizeBytes : null
                }
            };
        }).ToArray());
    }

    public Task<LoadedModel[]> GetLoadedModelsAsync(CancellationToken cancellationToken)
    {
        var loaded = _host.LoadedModel;
        var model = loaded == null ? null : _store.Find(loaded);
        if (model == null) return Task.FromResult(Array.Empty<LoadedModel>());

        //NOTE: All layers go to the GPU when there is one (-ngl 999); how much fitted, llama-server does not say.
        return Task.FromResult(new[] { new LoadedModel { Name = model.Name, SizeBytes = model.SizeBytes, ContextLength = _host.LoadedContextLength } });
    }

    public Task<EngineModelInfo> GetModelInfoAsync(string model, CancellationToken cancellationToken)
    {
        var installed = Installed(model);
        return Task.FromResult(new EngineModelInfo(installed.ParameterCount, installed.SupportsThinking));
    }

    public async IAsyncEnumerable<double> InstallAsync(string model, ModelSource source, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await _install.WaitAsync(cancellationToken);
        try
        {
            var existing = _store.Find(model);
            if (existing != null && (source?.Sha256 == null || string.Equals(source.Sha256, existing.Source.Sha256, StringComparison.OrdinalIgnoreCase)))
            {
                yield return 100;
                yield break;
            }

            if (source == null)
            {
                if (!HuggingFaceClient.TryParse(model, out var repository, out var quantisation))
                    throw new InvalidOperationException($"'{model}' has no download source. Install it from the server, which knows where its file is, or name it hf.co/<owner>/<repo>:<quantisation>.");

                source = await _huggingFace.ResolveAsync(repository, quantisation, cancellationToken);
            }

            //NOTE: A newer file for the same name replaces the old one; the old model must not stay loaded from a file being overwritten.
            if (existing != null) await _host.UnloadAsync(existing.Name, cancellationToken);

            var path = _store.FilePathFor(model);
            _logger.LogInformation("Downloading {Model} from {Repository}/{File} at {Revision}.", model, source.Repository, source.File, source.Revision ?? "main");
            await foreach (var percent in _huggingFace.DownloadAsync(source, path, cancellationToken))
            {
                yield return percent;
            }

            var gguf = GgufReader.Read(path);
            _store.Save(new InstalledModel
            {
                Name = model,
                Source = source,
                FileName = Path.GetFileName(path),
                InstalledUtc = DateTime.UtcNow,
                SizeBytes = new FileInfo(path).Length,
                ParameterCount = gguf.ParameterCount,
                ActiveParameterCount = ModelReport.ActiveParameters(gguf.ParameterCount, gguf.Read),
                ParameterSize = gguf.SizeLabel ?? SizeLabel(gguf.ParameterCount),
                ContextLength = gguf.ContextLength,
                SupportsThinking = gguf.SupportsThinking
            });
            _logger.LogInformation("Installed {Model}: {Architecture}, {Parameters} parameters, trained for a context of {ContextLength}.", model, gguf.Architecture, gguf.ParameterCount, gguf.ContextLength);
        }
        finally
        {
            _install.Release();
        }
    }

    public async Task UninstallAsync(string model, CancellationToken cancellationToken)
    {
        var installed = _store.Find(model);
        if (installed == null) return;

        await _host.UnloadAsync(installed.Name, cancellationToken);
        _store.Remove(installed);
    }

    public async IAsyncEnumerable<EngineChunk> ChatAsync(EngineChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        var installed = Installed(request.Model);
        var contextLength = ContextFor(installed, request.Options?.NumCtx is > 0 ? Math.Max(request.Options.NumCtx.Value, _options.ContextLength) : _options.ContextLength);

        await using var lease = await _host.AcquireAsync(installed.Name, _store.PathOf(installed), contextLength, cancellationToken);

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(lease.Address, "v1/chat/completions"))
        {
            Content = new StringContent(Body(request, installed).ToJsonString(), Encoding.UTF8, "application/json")
        };

        using var response = await _httpClientFactory.CreateClient(LlamaServerHost.ClientName).SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw await FailureAsync(response, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;

            using var json = JsonDocument.Parse(data);
            var root = json.RootElement;

            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("delta", out var delta))
            {
                var content = Text(delta, "content");
                var thinking = Text(delta, "reasoning_content");
                if (content != null || thinking != null) yield return new EngineChunk { Content = content, Thinking = thinking };
            }

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                var timings = root.TryGetProperty("timings", out var t) && t.ValueKind == JsonValueKind.Object ? t : default;
                yield return new EngineChunk
                {
                    Final = new EngineUsage
                    {
                        InputTokens = usage.GetProperty("prompt_tokens").GetInt32(),
                        OutputTokens = usage.GetProperty("completion_tokens").GetInt32(),
                        LoadDurationMs = (decimal)lease.LoadTime.TotalMilliseconds,
                        InputDurationMs = Number(timings, "prompt_ms"),
                        OutputDurationMs = Number(timings, "predicted_ms"),
                        TotalDurationMs = (decimal)total.Elapsed.TotalMilliseconds
                    }
                };
                yield break;
            }
        }

        throw new InvalidOperationException("Stream ended without usage from llama-server.");
    }

    public async Task<AnthropicTarget> ResolveAnthropicAsync(string model, CancellationToken cancellationToken)
    {
        //NOTE: llama-server answers /v1/messages itself. The context is set when it starts, so the model is (re)loaded
        //with the Anthropic context and held for the length of the request.
        var installed = Installed(model);
        var lease = await _host.AcquireAsync(installed.Name, _store.PathOf(installed), ContextFor(installed, _options.AnthropicContextLength > 0 ? _options.AnthropicContextLength : _options.ContextLength), cancellationToken);
        return new AnthropicTarget(lease.Address, installed.Name, lease);
    }

    private InstalledModel Installed(string model)
    {
        return _store.Find(model)
               ?? _store.List().FirstOrDefault(x => x.Name.StartsWith(model + ":", StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"No model matching '{model}' is installed.");
    }

    private static int ContextFor(InstalledModel model, int wanted)
    {
        return model.ContextLength is > 0 ? (int)Math.Min(wanted, model.ContextLength.Value) : wanted;
    }

    /// <summary>The OpenAI-shaped request: the prompt's own options, then the model's defaults for any it leaves out.</summary>
    internal static JsonObject Body(EngineChatRequest request, InstalledModel model)
    {
        var options = request.Options;
        var defaults = model.Source?.Defaults;

        var body = new JsonObject
        {
            ["messages"] = new JsonArray(request.Messages.Select(x => (JsonNode)new JsonObject
            {
                ["role"] = $"{x.Role}".ToLowerInvariant(),
                ["content"] = x.Content
            }).ToArray()),
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true }
        };

        Set(body, "temperature", options?.Temperature ?? defaults?.Temperature);
        Set(body, "top_p", options?.TopP ?? defaults?.TopP);
        Set(body, "top_k", options?.TopK ?? defaults?.TopK);
        Set(body, "repeat_penalty", options?.RepeatPenalty ?? defaults?.RepeatPenalty);
        if (options?.NumPredict is > 0) body["max_tokens"] = options.NumPredict.Value;

        var stop = options?.Stop ?? defaults?.Stop;
        if (stop is { Length: > 0 }) body["stop"] = new JsonArray(stop.Select(x => (JsonNode)x).ToArray());

        //NOTE: Only a model whose template has the switch; another would render it as text or ignore it.
        if (model.SupportsThinking) body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = request.Think };

        return body;
    }

    private static void Set<T>(JsonObject body, string name, T? value) where T : struct
    {
        if (value.HasValue) body[name] = JsonValue.Create(value.Value);
    }

    private static string Text(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
    }

    private static decimal Number(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : 0;
    }

    /// <summary>The status line as the server knows it (it recognises a machine fault by it), with llama-server's reason.</summary>
    private static async Task<HttpRequestException> FailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string reason = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);
            reason = json.RootElement.TryGetProperty("error", out var error)
                ? error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) ? m.GetString() : error.ToString()
                : body;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
        }

        var status = (int)response.StatusCode;
        var phrase = response.ReasonPhrase ?? response.StatusCode.ToString();
        var text = $"Response status code does not indicate success: {status} ({phrase}).";
        if (!string.IsNullOrWhiteSpace(reason)) text += $" llama-server: {(reason.Length > 500 ? reason[..500] + "…" : reason)}";
        return new HttpRequestException(text, null, (HttpStatusCode)status);
    }

    /// <summary>"1.7B" or "350M", as Ollama labels a model's size.</summary>
    internal static string SizeLabel(long parameters)
    {
        return parameters >= 1_000_000_000
            ? $"{parameters / 1e9:0.#}B".Replace(',', '.')
            : $"{parameters / 1e6:0}M";
    }
}
