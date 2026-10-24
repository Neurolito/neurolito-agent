using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Tharga.Neurolito.Agent.Features.Engine;
using Tharga.Neurolito.Agent.Features.LlamaServer;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Anthropic;

/// <summary>
/// The models run by Anthropic's hosted API: nothing runs on this machine, and every answer is billed to the owner of the key.
/// </summary>
/// <remarks>
/// The models are the configured ones (<see cref="AnthropicOptions.Models"/>), never the provider's whole list, so a model
/// is offered only once someone has given it a size to count work units at. There is nothing to install, load or unload.
/// </remarks>
internal sealed class AnthropicEngine : IEngine
{
    internal const string ClientName = "Anthropic";
    private static readonly TimeSpan NameLifetime = TimeSpan.FromMinutes(10);

    private readonly AnthropicOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AnthropicEngine> _logger;
    private string _name;
    private DateTime _nameUtc;

    public AnthropicEngine(IOptions<AnthropicOptions> options, IHttpClientFactory httpClientFactory, ILogger<AnthropicEngine> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string Kind => EngineKind.Anthropic;

    //NOTE: The context is the model's own; there is no engine default to override.
    public int? AnthropicContextLength => null;

    //NOTE: A self-test would be a billed call; a rested model comes back on its timer instead.
    public bool SupportsSelfTest => false;

    /// <summary>"Anthropic API", once the key is accepted. Listing the models is free, so it is the check.</summary>
    public async Task<string> GetNameAsync(CancellationToken cancellationToken)
    {
        if (_name != null && DateTime.UtcNow - _nameUtc < NameLifetime) return _name;

        try
        {
            using var message = Request(HttpMethod.Get, "v1/models?limit=1");
            using var response = await Client().SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Anthropic refused the model list: {Status}. Check Anthropic:ApiKey.", (int)response.StatusCode);
                _name = null;
                return null;
            }

            _name = "Anthropic API";
            _nameUtc = DateTime.UtcNow;
            return _name;
        }
        catch (HttpRequestException e)
        {
            _logger.LogWarning(e, "Anthropic cannot be reached at {Address}.", _options.Address);
            _name = null;
            return null;
        }
    }

    public Task<string[]> GetModelNamesAsync(CancellationToken cancellationToken) => Task.FromResult(Models().Select(x => x.Name).ToArray());

    public Task<LLModel[]> GetModelsAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(Models().Select(x => new LLModel
        {
            Name = x.Name,
            Loaded = false,
            Details = new ModelDetails
            {
                ParameterCount = x.ParameterCount,
                ParameterSize = x.ParameterCount > 0 ? LlamaServerEngine.SizeLabel(x.ParameterCount) : null
            }
        }).ToArray());
    }

    public Task<LoadedModel[]> GetLoadedModelsAsync(CancellationToken cancellationToken) => Task.FromResult(Array.Empty<LoadedModel>());

    public Task<EngineModelInfo> GetModelInfoAsync(string model, CancellationToken cancellationToken)
    {
        var configured = Configured(model);
        return Task.FromResult(new EngineModelInfo(configured.ParameterCount > 0 ? configured.ParameterCount : null, configured.SupportsThinking));
    }

    public IAsyncEnumerable<double> InstallAsync(string model, ModelSource source, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException($"'{model}' cannot be installed: this agent runs Anthropic's hosted models, which are listed in its configuration (Anthropic:Models).");
    }

    public Task UninstallAsync(string model, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException($"'{model}' cannot be uninstalled: this agent runs Anthropic's hosted models, which are listed in its configuration (Anthropic:Models).");
    }

    public async IAsyncEnumerable<EngineChunk> ChatAsync(EngineChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        var configured = Configured(request.Model);

        using var message = Request(HttpMethod.Post, "v1/messages");
        message.Content = new StringContent(Body(request, configured, _options.MaxTokens).ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await Client().SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw Failure((int)response.StatusCode, response.ReasonPhrase ?? response.StatusCode.ToString(), await response.Content.ReadAsStringAsync(cancellationToken));

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var state = new StreamState();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var chunk = state.Read(line[5..].Trim(), total.Elapsed);
            if (chunk != null) yield return chunk;
            if (state.Stopped) break;
        }

        if (!state.Stopped) throw new InvalidOperationException("Stream ended without message_stop from Anthropic.");

        yield return new EngineChunk { Final = state.Usage(total.Elapsed) };
    }

    public Task<AnthropicTarget> ResolveAnthropicAsync(string model, CancellationToken cancellationToken)
    {
        var configured = Configured(model);
        return Task.FromResult(new AnthropicTarget(new Uri(BaseAddress()), configured.Name, headers: Headers()));
    }

    private AnthropicModel[] Models() => (_options.Models ?? []).Where(x => !string.IsNullOrWhiteSpace(x?.Name)).ToArray();

    //NOTE: Exact names only. A hosted call costs money, so a near match must not reach one.
    private AnthropicModel Configured(string model)
    {
        return Models().FirstOrDefault(x => string.Equals(x.Name, model, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"'{model}' is not one of this agent's hosted models (Anthropic:Models).");
    }

    private HttpClient Client() => _httpClientFactory.CreateClient(ClientName);

    private string BaseAddress() => _options.Address.TrimEnd('/') + "/";

    private Dictionary<string, string> Headers() => new()
    {
        ["x-api-key"] = _options.ApiKey,
        ["anthropic-version"] = _options.Version
    };

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var message = new HttpRequestMessage(method, new Uri(new Uri(BaseAddress()), path));
        foreach (var (name, value) in Headers()) message.Headers.TryAddWithoutValidation(name, value);
        return message;
    }

    /// <summary>
    /// The Messages request: system messages joined into <c>system</c>, the rest in order. No sampling options are sent,
    /// since current models accept only their defaults.
    /// </summary>
    internal static JsonObject Body(EngineChatRequest request, AnthropicModel model, int maxTokens)
    {
        var system = string.Join("\n\n", request.Messages.Where(x => x.Role == Role.System && !string.IsNullOrEmpty(x.Content)).Select(x => x.Content));

        var body = new JsonObject
        {
            ["model"] = model.Name,
            ["max_tokens"] = request.Options?.NumPredict is > 0 ? request.Options.NumPredict.Value : maxTokens,
            ["messages"] = new JsonArray(request.Messages.Where(x => x.Role != Role.System).Select(x => (JsonNode)new JsonObject
            {
                ["role"] = x.Role == Role.Assistant ? "assistant" : "user",
                ["content"] = x.Content ?? string.Empty
            }).ToArray()),
            ["stream"] = true
        };

        if (system.Length > 0) body["system"] = system;

        var stop = request.Options?.Stop;
        if (stop is { Length: > 0 }) body["stop_sequences"] = new JsonArray(stop.Select(x => (JsonNode)x).ToArray());

        //NOTE: Summarized, because the default leaves the thinking text empty and the screen shows the thinking as it comes.
        if (request.Think && model.SupportsThinking) body["thinking"] = new JsonObject { ["type"] = "adaptive", ["display"] = "summarized" };

        return body;
    }

    /// <summary>The status line as the server knows it (it recognises a machine fault by it), with Anthropic's reason.</summary>
    internal static HttpRequestException Failure(int status, string phrase, string body)
    {
        string reason = null;
        try
        {
            using var json = JsonDocument.Parse(body);
            reason = json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m)
                ? m.GetString()
                : body;
        }
        catch (JsonException)
        {
            reason = body;
        }

        var text = $"Response status code does not indicate success: {status} ({phrase}).";
        if (!string.IsNullOrWhiteSpace(reason)) text += $" Anthropic: {(reason.Length > 500 ? reason[..500] + "…" : reason)}";
        return new HttpRequestException(text, null, (HttpStatusCode)status);
    }

    /// <summary>
    /// Reads Anthropic's event stream one <c>data:</c> line at a time: text and thinking as they come, and the usage, which
    /// arrives in <c>message_start</c> (input) and <c>message_delta</c> (output, as a running total).
    /// </summary>
    internal sealed class StreamState
    {
        private int _inputTokens;
        private int _outputTokens;
        private TimeSpan? _firstToken;

        public bool Stopped { get; private set; }

        public EngineChunk Read(string data, TimeSpan elapsed)
        {
            using var json = JsonDocument.Parse(data);
            var root = json.RootElement;

            switch (Text(root, "type"))
            {
                case "message_start":
                    if (root.TryGetProperty("message", out var message) && message.TryGetProperty("usage", out var startUsage)) Count(startUsage);
                    return null;

                case "content_block_delta":
                    if (!root.TryGetProperty("delta", out var delta)) return null;
                    var content = Text(delta, "type") == "text_delta" ? Text(delta, "text") : null;
                    var thinking = Text(delta, "type") == "thinking_delta" ? Text(delta, "thinking") : null;
                    if (string.IsNullOrEmpty(content) && string.IsNullOrEmpty(thinking)) return null;

                    _firstToken ??= elapsed;
                    return new EngineChunk { Content = content, Thinking = thinking };

                case "message_delta":
                    if (root.TryGetProperty("usage", out var deltaUsage)) Count(deltaUsage);
                    return null;

                case "message_stop":
                    Stopped = true;
                    return null;

                case "error":
                    //NOTE: An error after the stream has started, such as overloaded_error, comes as an event with no status.
                    var error = root.TryGetProperty("error", out var e) ? e : default;
                    var type = error.ValueKind == JsonValueKind.Object ? Text(error, "type") : null;
                    throw Failure(StatusOf(type), type ?? "error", data);

                default:
                    return null;
            }
        }

        public EngineUsage Usage(TimeSpan total)
        {
            var first = _firstToken ?? total;
            return new EngineUsage
            {
                InputTokens = _inputTokens,
                OutputTokens = _outputTokens,
                InputDurationMs = (decimal)first.TotalMilliseconds,
                OutputDurationMs = (decimal)(total - first).TotalMilliseconds,
                TotalDurationMs = (decimal)total.TotalMilliseconds
            };
        }

        //NOTE: Cached input is input too; leaving it out would make a cached prompt look smaller than it is.
        private void Count(JsonElement usage)
        {
            var input = Number(usage, "input_tokens") + Number(usage, "cache_creation_input_tokens") + Number(usage, "cache_read_input_tokens");
            if (input > 0) _inputTokens = input;
            if (Number(usage, "output_tokens") is > 0 and var output) _outputTokens = output;
        }

        private static int StatusOf(string type) => type switch
        {
            "overloaded_error" => 529,
            "rate_limit_error" => 429,
            "api_error" => 500,
            "invalid_request_error" => 400,
            "authentication_error" => 401,
            "permission_error" => 403,
            "billing_error" => 402,
            "not_found_error" => 404,
            "request_too_large" => 413,
            _ => 502
        };
    }

    private static string Text(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static int Number(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
    }
}
