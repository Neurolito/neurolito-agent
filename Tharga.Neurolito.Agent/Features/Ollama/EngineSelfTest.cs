using System.Diagnostics;
using Tharga.Communication.Client.Communication;
using Tharga.Neurolito.Agent.Features.Engine;
using Tharga.Communication.MessageHandler;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Ollama;

/// <summary>
/// Runs a one-token prompt on a model to find out whether the engine can generate at all.
/// </summary>
/// <remarks>
/// A listed model says the engine is up; it does not say the engine can run it. A machine short of memory can
/// list every model and fail every generate with a 500. The server rests a model like that and, rather
/// than risk a real job as the trial, asks this agent to test it.
/// </remarks>
public interface IEngineSelfTest
{
    Task<EngineSelfTestResponse> RunAsync(string model, CancellationToken cancellationToken);
}

internal class EngineSelfTest : IEngineSelfTest
{
    //NOTE: Long enough for a large model to load from disk, which is part of what is being tested.
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);

    private readonly IEngine _engine;
    private readonly ILogger<EngineSelfTest> _logger;

    public EngineSelfTest(IEngine engine, ILogger<EngineSelfTest> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    public async Task<EngineSelfTestResponse> RunAsync(string model, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Limit);

            var request = new EngineChatRequest
            {
                Model = model,
                Messages = [new Message { Role = Role.User, Content = "Reply with OK." }],
                Options = new Options { NumPredict = 1 }
            };

            var answered = false;
            await foreach (var chunk in _engine.ChatAsync(request, cts.Token))
            {
                answered = true;
                if (chunk.Final != null) break;
            }

            _logger.LogInformation("Self-test of {model}: {result} in {ms} ms.", model, answered ? "ok" : "no answer", sw.ElapsedMilliseconds);
            return new EngineSelfTestResponse { Model = model, Ok = answered, Error = answered ? null : "The engine returned no answer.", DurationMs = sw.Elapsed.TotalMilliseconds };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Self-test of {model}: no answer within {limit}.", model, Limit);
            return new EngineSelfTestResponse { Model = model, Ok = false, Error = $"No answer within {Limit.TotalMinutes:0} minutes.", DurationMs = sw.Elapsed.TotalMilliseconds };
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Self-test of {model} failed.", model);
            return new EngineSelfTestResponse { Model = model, Ok = false, Error = e.Message, DurationMs = sw.Elapsed.TotalMilliseconds };
        }
    }
}

internal class EngineSelfTestRequestHandler : PostMessageHandlerBase<EngineSelfTestRequest>
{
    private readonly IEngineSelfTest _selfTest;
    private readonly IClientCommunication _clientCommunication;

    public EngineSelfTestRequestHandler(IEngineSelfTest selfTest, IClientCommunication clientCommunication)
    {
        _selfTest = selfTest;
        _clientCommunication = clientCommunication;
    }

    public override async Task Handle(EngineSelfTestRequest message)
    {
        var result = await _selfTest.RunAsync(message.Model, CancellationToken.None);
        await _clientCommunication.PostAsync(result);
    }
}
