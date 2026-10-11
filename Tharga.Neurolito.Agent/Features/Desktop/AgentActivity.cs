namespace Tharga.Neurolito.Agent.Features.Desktop;

/// <summary>
/// What the agent is doing, as a shape: a job started with so many words, tokens arrived, the job ended. Never the
/// words themselves - the agent serves other teams' jobs, and this feeds a screen anyone in the room can see.
/// </summary>
public interface IAgentActivity
{
    event EventHandler<ActivityEvent> Changed;

    /// <summary>Jobs running now.</summary>
    int Running { get; }

    void JobStarted(string model, int inputWords);
    void Tokens(int count);
    void JobEnded(int outputWords, double durationMs, double? workUnits = null);
}

public record ActivityEvent(string Kind, string Model = null, int Words = 0, int Tokens = 0, double DurationMs = 0, double? WorkUnits = null);

internal sealed class AgentActivity : IAgentActivity
{
    private int _running;

    public event EventHandler<ActivityEvent> Changed;

    public int Running => Volatile.Read(ref _running);

    public void JobStarted(string model, int inputWords)
    {
        Interlocked.Increment(ref _running);
        Changed?.Invoke(this, new ActivityEvent("job-start", model, inputWords));
    }

    public void Tokens(int count)
    {
        Changed?.Invoke(this, new ActivityEvent("tokens", Tokens: count));
    }

    public void JobEnded(int outputWords, double durationMs, double? workUnits = null)
    {
        if (Interlocked.Decrement(ref _running) < 0) Interlocked.Exchange(ref _running, 0);
        Changed?.Invoke(this, new ActivityEvent("job-end", Words: outputWords, DurationMs: durationMs, WorkUnits: workUnits));
    }

    /// <summary>The number of words in a text, which is all of it the screen is told.</summary>
    public static int WordsIn(string text) => string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length;
}
