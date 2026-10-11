using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tharga.Communication.Client;
using Tharga.Communication.Client.Communication;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Ollama;

/// <summary>
/// Forwards an Anthropic request to this agent's Ollama and relays the answer as it arrives.
/// </summary>
/// <remarks>
/// Ollama answers <c>/v1/messages</c> in Anthropic's own shape and streams Anthropic's own event
/// sequence, so the body is never parsed in either direction. The one thing written into the request
/// is the resolved model name, because the published id the caller used means nothing to the engine.
/// </remarks>
internal sealed class AnthropicPassthroughService : IAnthropicPassthroughService
{
    private const string MessagesPath = "/v1/messages";
    private const string ModelProperty = "model";
    private const int ChunkSize = 4096;

    private readonly HttpClient _httpClient;
    private readonly IClientCommunication _clientCommunication;
    private readonly IInstanceService _instanceService;
    private readonly IEventService _eventService;
    private readonly OllamaOptions _options;
    private readonly IContextVariantService _contextVariantService;
    private readonly ILogger<AnthropicPassthroughService> _logger;

    public AnthropicPassthroughService(HttpClient httpClient, IClientCommunication clientCommunication, IInstanceService instanceService, IEventService eventService, IOptions<OllamaOptions> options, IContextVariantService contextVariantService, ILogger<AnthropicPassthroughService> logger)
    {
        _httpClient = httpClient;
        _clientCommunication = clientCommunication;
        _instanceService = instanceService;
        _eventService = eventService;
        _options = options.Value;
        _contextVariantService = contextVariantService;
        _logger = logger;
    }

    public async Task<AnthropicPromptResponse> RunAsync(AnthropicPromptRequest request, CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        void OnCancel(object sender, EventArgs e) => cancellation.Cancel();
        _eventService.CancelEvent += OnCancel;

        var streamed = false;

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_options.Address), MessagesPath))
            {
                Content = new StringContent(Prepare(request, await _contextVariantService.ResolveAsync(request.Model, cancellation.Token)), Encoding.UTF8, "application/json")
            };

            using var response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);

            if (!response.IsSuccessStatusCode)
            {
                return new AnthropicPromptResponse
                {
                    StatusCode = (int)response.StatusCode,
                    ErrorJson = await response.Content.ReadAsStringAsync(cancellation.Token),
                    HasStreamed = false
                };
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellation.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var buffer = new char[ChunkSize];
            int read;
            while ((read = await reader.ReadAsync(buffer, cancellation.Token)) > 0)
            {
                streamed = true;

                await _clientCommunication.PostAsync(new AnthropicChunkResponse
                {
                    Instance = _instanceService.AgentInstanceKey,
                    RequestId = request.RequestId,
                    Chunk = new string(buffer, 0, read)
                });
            }

            return new AnthropicPromptResponse { StatusCode = (int)response.StatusCode, HasStreamed = streamed };
        }
        catch (OperationCanceledException)
        {
            // The caller left or a cancel arrived. Nobody is waiting, so this is an outcome, not a fault.
            return new AnthropicPromptResponse { StatusCode = 499, HasStreamed = streamed };
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Anthropic request {RequestId} failed against the local engine.", request.RequestId);
            return new AnthropicPromptResponse { StatusCode = 502, HasStreamed = streamed };
        }
        finally
        {
            _eventService.CancelEvent -= OnCancel;
        }
    }

    /// <summary>
    /// Writes the resolved model into the request, changing nothing else.
    /// </summary>
    /// <remarks>
    /// The caller's <c>stream</c> choice is left exactly as it arrived. Forcing streaming would be
    /// tempting - it is what makes a long generation visible - but the chunks are reassembled by the
    /// server, and a caller who asked for a whole response would then receive a concatenated event
    /// stream where it expects one JSON body.
    /// <para>
    /// The model written is <paramref name="engineModel"/>: the requested model, or its larger-context variant
    /// (<see cref="IContextVariantService"/>), because Ollama's own default context would cut the request.
    /// </para>
    /// </remarks>
    internal static string Prepare(AnthropicPromptRequest request, string engineModel)
    {
        if (JsonNode.Parse(request.RequestJson) is not JsonObject body) return request.RequestJson;

        if (string.IsNullOrWhiteSpace(engineModel)) return request.RequestJson;

        body[ModelProperty] = engineModel;

        return body.ToJsonString();
    }
}
