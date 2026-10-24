using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Engine;

/// <summary>
/// The program that runs the models: Ollama, or llama.cpp's own <c>llama-server</c>. Everything that depends on which
/// one it is lives behind this; the rest of the agent - prompts, work units, the server connection - is the same for both.
/// </summary>
internal interface IEngine
{
    /// <summary>One of <see cref="EngineKind"/>, reported to the server in <see cref="Client.Contract.Engine.Kind"/>.</summary>
    string Kind { get; }

    /// <summary>The engine and its version, for example "Ollama 0.40.1". Null when the engine cannot be reached.</summary>
    Task<string> GetNameAsync(CancellationToken cancellationToken);

    /// <summary>The names of the installed models. Cheaper than <see cref="GetModelsAsync"/>, for resolving a requested name.</summary>
    Task<string[]> GetModelNamesAsync(CancellationToken cancellationToken);

    /// <summary>The installed models, as reported to the server.</summary>
    Task<LLModel[]> GetModelsAsync(CancellationToken cancellationToken);

    /// <summary>The models loaded now - in memory, ready to answer without a load.</summary>
    Task<LoadedModel[]> GetLoadedModelsAsync(CancellationToken cancellationToken);

    /// <summary>What a prompt needs to know about an installed model.</summary>
    Task<EngineModelInfo> GetModelInfoAsync(string model, CancellationToken cancellationToken);

    /// <summary>Installs a model, yielding the percent done. <paramref name="source"/> is where to download it from, for an engine that needs one.</summary>
    IAsyncEnumerable<double> InstallAsync(string model, ModelSource source, CancellationToken cancellationToken);

    Task UninstallAsync(string model, CancellationToken cancellationToken);

    /// <summary>Generates an answer as it arrives. The last chunk carries <see cref="EngineChunk.Final"/>.</summary>
    IAsyncEnumerable<EngineChunk> ChatAsync(EngineChatRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Where an Anthropic request for <paramref name="model"/> is sent, ready to take it with
    /// <see cref="AnthropicContextLength"/>. Dispose the result when the request is done.
    /// </summary>
    Task<AnthropicTarget> ResolveAnthropicAsync(string model, CancellationToken cancellationToken);

    /// <summary>The context Anthropic requests get, or null when the engine's own default is left in place.</summary>
    int? AnthropicContextLength { get; }

    /// <summary>Whether the server may send a self-test. False for a hosted engine, where every trial is a billed call.</summary>
    bool SupportsSelfTest { get; }
}

/// <summary>What a prompt needs to know about a model: its size, for work units, and whether it can think.</summary>
internal record EngineModelInfo(long? ParameterCount, bool SupportsThinking);

internal record EngineChatRequest
{
    public required string Model { get; init; }
    public required Message[] Messages { get; init; }
    public Options Options { get; init; }

    /// <summary>Let a model that can think do so. Its thinking is kept apart from the answer.</summary>
    public bool Think { get; init; }

    /// <summary>How long the engine keeps the model loaded after the answer, when it decides that per request.</summary>
    public int? KeepAliveMinutes { get; init; }
}

internal record EngineChunk
{
    public string Content { get; init; }
    public string Thinking { get; init; }

    /// <summary>Set on the last chunk only.</summary>
    public EngineUsage Final { get; init; }
}

/// <summary>The engine's own account of one answer.</summary>
internal record EngineUsage
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public decimal LoadDurationMs { get; init; }
    public decimal InputDurationMs { get; init; }
    public decimal OutputDurationMs { get; init; }
    public decimal TotalDurationMs { get; init; }
}

/// <summary>Where an Anthropic request goes. Holds the engine's model in place until disposed.</summary>
internal sealed class AnthropicTarget(Uri address, string model, IAsyncDisposable hold = null, IReadOnlyDictionary<string, string> headers = null) : IAsyncDisposable
{
    /// <summary>The engine's base address; the request is posted to <c>/v1/messages</c> on it.</summary>
    public Uri Address { get; } = address;

    /// <summary>The model name to write into the request.</summary>
    public string Model { get; } = model;

    /// <summary>Headers the request is sent with, such as a hosted provider's key. Empty for a local engine.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; } = headers ?? new Dictionary<string, string>();

    public ValueTask DisposeAsync() => hold?.DisposeAsync() ?? ValueTask.CompletedTask;
}
