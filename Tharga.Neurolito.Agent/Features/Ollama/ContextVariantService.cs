using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Tharga.Neurolito.Agent.Features.Ollama;

/// <summary>Which model an Anthropic request should run on, so that it is not cut to Ollama's default context.</summary>
public interface IContextVariantService
{
    /// <summary>
    /// The model to forward to: a variant of <paramref name="model"/> with <see cref="OllamaOptions.AnthropicContextLength"/>,
    /// created if missing, or <paramref name="model"/> itself when no variant is wanted or one cannot be made.
    /// </summary>
    Task<string> ResolveAsync(string model, CancellationToken cancellationToken);

    /// <summary>
    /// Removes every variant of <paramref name="model"/>. Called when the model is uninstalled: a variant shares the
    /// model's files, so one left behind would keep them on disk.
    /// </summary>
    Task RemoveVariantsAsync(string model, CancellationToken cancellationToken);
}

/// <summary>
/// Creates and remembers the larger-context variants of installed models.
/// </summary>
/// <remarks>
/// Ollama's Anthropic endpoint has no way to ask for a context, so the setting has to live on the model. A variant
/// is made with <c>POST /api/create</c> from the installed model plus <c>num_ctx</c>: it shares the model's files,
/// adds a few bytes, and needs no restart and no change to the machine's environment.
/// <para>
/// Every failure falls back to the installed model, which is exactly what the agent did before this existed.
/// </para>
/// </remarks>
internal sealed partial class ContextVariantService : IContextVariantService
{
    public const string ClientName = "OllamaContextVariants";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OllamaOptions _options;
    private readonly ILogger<ContextVariantService> _logger;
    private readonly ConcurrentDictionary<string, string> _resolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ContextVariantService(IHttpClientFactory httpClientFactory, IOptions<OllamaOptions> options, ILogger<ContextVariantService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> ResolveAsync(string model, CancellationToken cancellationToken)
    {
        var wanted = _options.AnthropicContextLength;
        if (wanted <= 0 || string.IsNullOrWhiteSpace(model) || ContextVariant.IsVariant(model)) return model;

        if (_resolved.TryGetValue(model, out var known)) return known;

        //NOTE: One at a time. Claude Code sends several requests at once, and two creations of the same variant
        //would race on Ollama's manifest for nothing.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_resolved.TryGetValue(model, out known)) return known;

            var resolved = await CreateAsync(model, wanted, cancellationToken);
            if (resolved != null) _resolved[model] = resolved;
            return resolved ?? model;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The variant, or the model itself when it already has the context. Null when neither can be confirmed.</summary>
    private async Task<string> CreateAsync(string model, int wanted, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(ClientName);

        try
        {
            var shown = await ShowAsync(client, model, cancellationToken);
            if (shown == null)
            {
                _logger.LogWarning("Model {Model} is not installed; Anthropic requests for it go to Ollama unchanged.", model);
                return null;
            }

            var (trained, configured) = shown.Value;
            var contextLength = trained is > 0 ? Math.Min(wanted, trained.Value) : wanted;

            //NOTE: Someone gave the installed model a context of its own, at least as large. Use it as it is.
            if (configured >= contextLength) return model;

            var variant = ContextVariant.NameOf(model, contextLength);
            if (await ShowAsync(client, variant, cancellationToken) == null)
            {
                using var response = await client.PostAsJsonAsync("api/create", new
                {
                    model = variant,
                    from = model,
                    parameters = new { num_ctx = contextLength },
                    stream = false
                }, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    //NOTE: Ollama before 0.5 had no "from" on api/create. Such an engine keeps working as before,
                    //with its default context, and the server records the truncation it causes.
                    _logger.LogWarning("Could not create {Variant} from {Model}: {Status} {Body}. Anthropic requests use the model as installed.",
                        variant, model, (int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
                    return null;
                }

                _logger.LogInformation("Created {Variant} with a context of {ContextLength} tokens for Anthropic requests.", variant, contextLength);
                await RemoveAsync(client, model, variant, cancellationToken);
            }

            return variant;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Could not prepare a larger-context variant of {Model}. Anthropic requests use the model as installed.", model);
            return null;
        }
    }

    public async Task RemoveVariantsAsync(string model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model) || ContextVariant.IsVariant(model)) return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _resolved.TryRemove(model, out _);
            await RemoveAsync(_httpClientFactory.CreateClient(ClientName), model, null, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Could not remove the larger-context variants of {Model}.", model);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The variants of <paramref name="model"/>, except <paramref name="keep"/>: those an earlier setting left behind, or all of them.</summary>
    private async Task RemoveAsync(HttpClient client, string model, string keep, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(await client.GetStreamAsync("api/tags", cancellationToken), cancellationToken: cancellationToken);
        var stale = document.RootElement.GetProperty("models").EnumerateArray()
            .Select(x => x.GetProperty("name").GetString())
            .Where(x => ContextVariant.IsVariant(x)
                        && !string.Equals(x, keep, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(ContextVariant.Normalize(ContextVariant.BaseOf(x)), ContextVariant.Normalize(model), StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var name in stale)
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, "api/delete") { Content = JsonContent.Create(new { model = name }) };
            using var response = await client.SendAsync(request, cancellationToken);
            _logger.LogInformation("Removed {Variant} ({Reason}): {Status}.", name, keep == null ? "its model is uninstalled" : $"replaced by {keep}", (int)response.StatusCode);
        }
    }

    /// <summary>
    /// The context the model was trained for and the one it is set to run with, or null when Ollama does not have it.
    /// </summary>
    private static async Task<(int? Trained, int Configured)?> ShowAsync(HttpClient client, string model, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync("api/show", new { model }, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;

        int? trained = null;
        if (root.TryGetProperty("model_info", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            //NOTE: The key carries the architecture - "qwen3moe.context_length", "llama.context_length".
            var entry = info.EnumerateObject().FirstOrDefault(x => x.Name.EndsWith(".context_length", StringComparison.Ordinal));
            if (entry.Value.ValueKind == JsonValueKind.Number) trained = entry.Value.GetInt32();
        }

        var configured = 0;
        if (root.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.String)
        {
            var match = NumCtxPattern().Match(parameters.GetString() ?? string.Empty);
            if (match.Success) configured = int.Parse(match.Groups[1].Value);
        }

        return (trained, configured);
    }

    [GeneratedRegex(@"^num_ctx\s+(\d+)", RegexOptions.Multiline)]
    private static partial Regex NumCtxPattern();
}
