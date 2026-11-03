using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.Json;
using Tharga.Neurolito.Client.Contract;
using Options = Tharga.Neurolito.Client.Contract.Options;

namespace Tharga.Neurolito.Client;

internal class NeurolitoService : INeurolitoService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NeurolitoService> _logger;
    private readonly NeurolitoOptions _options;

    public NeurolitoService(IHttpClientFactory httpClientFactory, IOptions<NeurolitoOptions> options, ILogger<NeurolitoService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<EnqueueResponse> EnqueueAsync(Input input, Dictionary<string, string> tags = default, ResponseInstruction responseInstruction = default, CancellationToken cancellationToken = default)
    {
        //NOTE: Fallback models exist on V2 only.
        if (input?.FallbackModels is { Length: > 0 }) return await EnqueueAsync(input, null, tags, responseInstruction, cancellationToken);

        try
        {
            var model = input.Model; // ?? _options.DefaultModel;

            using var httpClient = _httpClientFactory.CreateClient(Constants.NeurolitoClient);
            httpClient.DefaultRequestHeaders.Add("X-API-KEY", _options.ApiKey);
            var queueRequest = new QueueRequest
            {
                Prompt = input,
                Model = model,
                Tags = tags,
                ResponseInstruction = responseInstruction
            };
            using var response = await httpClient.PostAsJsonAsync("Queue", queueRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new EnqueueResponse { Success = false, RequestId = null, InfoLinq = null, Message = response.ReasonPhrase };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var item = JsonSerializer.Deserialize<EnqueueResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return item;
        }
        catch (Exception e)
        {
            _logger.LogError(e, e.Message);
            return new EnqueueResponse
            {
                Success = false,
                Message = e.Message
            };
        }
    }

    public async Task<EnqueueResponse> EnqueueAsync(Input input, Options options, Dictionary<string, string> tags = default, ResponseInstruction responseInstruction = default, CancellationToken cancellationToken = default)
    {
        try
        {
            var model = input.Model; // ?? _options.DefaultModel;

            using var httpClient = _httpClientFactory.CreateClient(Constants.NeurolitoClient);
            httpClient.DefaultRequestHeaders.Add("X-API-KEY", _options.ApiKey);
            var queueRequest = new QueueRequestV2
            {
                Model = model,
                Messages = input,
                Options = options,
                Tags = tags,
                ResponseInstruction = responseInstruction,
                FallbackModels = input.FallbackModels,
                FallbackAfterSeconds = input.FallbackAfter.HasValue ? (int)input.FallbackAfter.Value.TotalSeconds : null
            };
            using var response = await httpClient.PostAsJsonAsync("Queue/V2", queueRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new EnqueueResponse { Success = false, RequestId = null, InfoLinq = null, Message = response.ReasonPhrase };
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var item = JsonSerializer.Deserialize<EnqueueResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return item;
        }
        catch (Exception e)
        {
            _logger.LogError(e, e.Message);
            return new EnqueueResponse
            {
                Success = false,
                Message = e.Message
            };
        }
    }

    public async Task<CanConnectResponse> CanConnectAsync()
    {
        using var httpClient = _httpClientFactory.CreateClient(Constants.NeurolitoClient);
        httpClient.DefaultRequestHeaders.Add("X-API-KEY", _options.ApiKey);

        try
        {
            var response = await httpClient.GetAsync("Health");
            return new CanConnectResponse
            {
                Success = response.IsSuccessStatusCode,
                Address = httpClient.BaseAddress,
                Message = response.ReasonPhrase
            };
        }
        catch (Exception e)
        {
            return new CanConnectResponse
            {
                Success = false,
                Address = httpClient.BaseAddress,
                Message = e.Message
            };
        }
    }

    public async Task<bool> CancelAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        using var httpClient = _httpClientFactory.CreateClient(Constants.NeurolitoClient);
        httpClient.DefaultRequestHeaders.Add("X-API-KEY", _options.ApiKey);

        using var response = await httpClient.DeleteAsync($"Queue/{requestId}", cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.OK) return true;
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return false;

        response.EnsureSuccessStatusCode();
        return false;
    }

    public async Task<GetReadyResponse> GetReadyAsync(string model, int? keepAliveMinutes = null, CancellationToken cancellationToken = default)
    {
        using var httpClient = _httpClientFactory.CreateClient(Constants.NeurolitoClient);
        httpClient.DefaultRequestHeaders.Add("X-API-KEY", _options.ApiKey);

        var query = $"model={Uri.EscapeDataString(model ?? string.Empty)}{(keepAliveMinutes.HasValue ? $"&keepAliveMinutes={keepAliveMinutes}" : "")}";
        using var response = await httpClient.PostAsync($"Prompt/ready?{query}", null, cancellationToken);

        //NOTE: A refusal comes with the same body as a success, saying why; only a failure without one throws.
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(content)) response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<GetReadyResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    public async Task<PromptStatusResponse> GetStatusAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        using var httpClient = _httpClientFactory.CreateClient(Constants.NeurolitoClient);
        httpClient.DefaultRequestHeaders.Add("X-API-KEY", _options.ApiKey);

        using var response = await httpClient.GetAsync($"Queue/{requestId}/status", cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<PromptStatusResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    public Task VoteAsync(Guid RequestId, int vote, CancellationToken cancellationToken)
    {
        return SendVoteAsync(RequestId, vote, null, cancellationToken);
    }

    public Task VoteAsync(Guid requestId, int vote, VoteSource source, CancellationToken cancellationToken)
    {
        return SendVoteAsync(requestId, vote, source, cancellationToken);
    }

    private async Task SendVoteAsync(Guid requestId, int vote, VoteSource? source, CancellationToken cancellationToken)
    {
        if (vote < 0 || vote > 100) throw new ArgumentOutOfRangeException("Vote can be a number between 0 and 100.");

        using var httpClient = _httpClientFactory.CreateClient(Constants.NeurolitoClient);
        httpClient.DefaultRequestHeaders.Add("X-API-KEY", _options.ApiKey);

        var request = new VoteRequest
        {
            RequestId = requestId,
            Vote = vote,
            Source = source
        };
        using var response = await httpClient.PostAsJsonAsync("Chat/Vote", request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}