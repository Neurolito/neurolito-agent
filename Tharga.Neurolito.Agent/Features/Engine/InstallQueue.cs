using System.Collections.Concurrent;

namespace Tharga.Neurolito.Agent.Features.Engine;

/// <summary>
/// The models being installed, one at a time in the order asked, whatever the engine. A model asked for while it is
/// already queued or installing is not queued twice, and an uninstall of one cancels its install.
/// </summary>
/// <remarks>
/// The server asks for every missing model each time the agent reports its models, and the agent reports after each
/// install - so without this a long download was asked for again after every shorter one finished.
/// </remarks>
public interface IInstallQueue
{
    /// <summary>The models queued or installing, the one installing first.</summary>
    IReadOnlyList<string> Pending { get; }

    /// <summary>Whether anything is queued or installing; an update waits for it.</summary>
    bool Busy { get; }

    /// <summary>
    /// A place in the queue for <paramref name="model"/>, or null when it already has one. Dispose it when the install
    /// has ended, however it ended.
    /// </summary>
    InstallTicket Enter(string model, CancellationToken cancellationToken);

    /// <summary>Cancels the install of <paramref name="model"/>, queued or running. False when there is none.</summary>
    bool Cancel(string model);
}

public sealed class InstallTicket : IDisposable
{
    private readonly InstallQueue _queue;
    private readonly CancellationTokenSource _cancellation;
    private bool _holdsTurn;

    internal InstallTicket(InstallQueue queue, string model, CancellationTokenSource cancellation)
    {
        _queue = queue;
        Model = model;
        _cancellation = cancellation;
    }

    public string Model { get; }

    /// <summary>Cancelled when the install is cancelled, by an uninstall of the model or by the caller.</summary>
    public CancellationToken Token => _cancellation.Token;

    /// <summary>Waits until the installs ahead have ended.</summary>
    public async Task WaitForTurnAsync()
    {
        await _queue.Gate.WaitAsync(Token);
        _holdsTurn = true;
    }

    internal void Cancel() => _cancellation.Cancel();

    public void Dispose()
    {
        if (_holdsTurn) _queue.Gate.Release();
        _holdsTurn = false;
        _queue.Leave(this);
        _cancellation.Dispose();
    }
}

internal sealed class InstallQueue : IInstallQueue
{
    private readonly ConcurrentDictionary<string, InstallTicket> _tickets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _orderLock = new();
    private readonly List<string> _order = [];

    internal SemaphoreSlim Gate { get; } = new(1, 1);

    public IReadOnlyList<string> Pending
    {
        get
        {
            lock (_orderLock) return _order.ToArray();
        }
    }

    public bool Busy => !_tickets.IsEmpty;

    public InstallTicket Enter(string model, CancellationToken cancellationToken)
    {
        var ticket = new InstallTicket(this, model, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
        if (!_tickets.TryAdd(model, ticket))
        {
            ticket.Dispose();
            return null;
        }

        lock (_orderLock) _order.Add(model);
        return ticket;
    }

    public bool Cancel(string model)
    {
        if (!_tickets.TryGetValue(model, out var ticket)) return false;
        ticket.Cancel();
        return true;
    }

    internal void Leave(InstallTicket ticket)
    {
        if (!_tickets.TryRemove(new KeyValuePair<string, InstallTicket>(ticket.Model, ticket))) return;
        lock (_orderLock) _order.Remove(ticket.Model);
    }
}
