using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Client;

public interface INeurolitoService
{
    Task<EnqueueResponse> EnqueueAsync(Input input, Dictionary<string, string> tags = default, ResponseInstruction responseInstruction = default, CancellationToken cancellationToken = default);
    Task<EnqueueResponse> EnqueueAsync(Input input, Options options, Dictionary<string, string> tags = default, ResponseInstruction responseInstruction = default, CancellationToken cancellationToken = default);
    Task<CanConnectResponse> CanConnectAsync();
    Task VoteAsync(Guid RequestId, int vote, CancellationToken cancellationToken);

    /// <summary>Rates a completed request 0-100, stating whether a person or a pipeline cast the vote.</summary>
    Task VoteAsync(Guid requestId, int vote, VoteSource source, CancellationToken cancellationToken);
    Task<bool> CancelAsync(Guid requestId, CancellationToken cancellationToken = default);
    Task<PromptStatusResponse> GetStatusAsync(Guid requestId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Says a request for <paramref name="model"/> is coming soon, so the machine it would most likely land on loads it
    /// now. Returns at once; nothing is generated or charged. At most once a minute per model.
    /// </summary>
    Task<GetReadyResponse> GetReadyAsync(string model, int? keepAliveMinutes = null, CancellationToken cancellationToken = default);
}