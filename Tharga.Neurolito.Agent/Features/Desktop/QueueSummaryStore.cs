using Tharga.Communication.MessageHandler;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Desktop;

/// <summary>The latest queue counts the server sent, for the screensaver.</summary>
public interface IQueueSummaryStore
{
    event EventHandler<QueueSummaryMessage> Changed;

    /// <summary>The latest counts, or null before the server has sent any.</summary>
    QueueSummaryMessage Latest { get; }

    void Set(QueueSummaryMessage summary);
}

internal sealed class QueueSummaryStore : IQueueSummaryStore
{
    private QueueSummaryMessage _latest;

    public event EventHandler<QueueSummaryMessage> Changed;

    public QueueSummaryMessage Latest => Volatile.Read(ref _latest);

    public void Set(QueueSummaryMessage summary)
    {
        if (summary == null) return;
        Volatile.Write(ref _latest, summary);
        Changed?.Invoke(this, summary);
    }
}

/// <summary>Keeps the server's queue counts. An agent without the desktop has no store and never asks for them.</summary>
internal sealed class QueueSummaryMessageHandler : PostMessageHandlerBase<QueueSummaryMessage>
{
    private readonly IQueueSummaryStore _store;

    public QueueSummaryMessageHandler(IQueueSummaryStore store = null)
    {
        _store = store;
    }

    public override Task Handle(QueueSummaryMessage message)
    {
        _store?.Set(message);
        return Task.CompletedTask;
    }
}
