using Tharga.Neurolito.Agent.Features.Engine;

namespace Tharga.Neurolito.Agent.Tests;

/// <summary>Model installs run one at a time, are never queued twice, and an uninstall cancels one.</summary>
public class InstallQueueTests
{
    [Fact]
    public void AModelAlreadyQueuedIsNotQueuedAgain()
    {
        var queue = new InstallQueue();
        using var first = queue.Enter("qwen3-coder:30b", CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(queue.Enter("QWEN3-CODER:30B", CancellationToken.None));
        Assert.Equal(["qwen3-coder:30b"], queue.Pending);
    }

    [Fact]
    public async Task InstallsTakeTurnsInTheOrderAsked()
    {
        var queue = new InstallQueue();
        using var first = queue.Enter("a", CancellationToken.None);
        using var second = queue.Enter("b", CancellationToken.None);
        await first.WaitForTurnAsync();

        var waiting = second.WaitForTurnAsync();
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(["a", "b"], queue.Pending);

        first.Dispose();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["b"], queue.Pending);
    }

    [Fact]
    public async Task CancellingAQueuedInstallTakesItOutAndLeavesTheOthers()
    {
        var queue = new InstallQueue();
        using var running = queue.Enter("a", CancellationToken.None);
        await running.WaitForTurnAsync();
        var queued = queue.Enter("b", CancellationToken.None);

        var waiting = queued.WaitForTurnAsync();
        Assert.True(queue.Cancel("b"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        queued.Dispose();

        Assert.Equal(["a"], queue.Pending);
        Assert.False(queue.Cancel("b"));
        Assert.True(queue.Busy);
    }

    [Fact]
    public void AnEndedInstallCanBeAskedForAgain()
    {
        var queue = new InstallQueue();
        queue.Enter("a", CancellationToken.None)!.Dispose();

        Assert.False(queue.Busy);
        using var again = queue.Enter("a", CancellationToken.None);
        Assert.NotNull(again);
    }
}
