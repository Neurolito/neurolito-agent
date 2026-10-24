namespace Tharga.Neurolito.Agent.Features.Anthropic;

/// <summary>The <c>Anthropic</c> section: Anthropic's hosted API as the engine, chosen with <c>Engine:Kind = Anthropic</c>.</summary>
/// <remarks>
/// Every call is billed to the owner of <see cref="ApiKey"/>. The agent never installs or downloads anything: the models are
/// the ones listed in <see cref="Models"/>, each with the size work units are counted at.
/// </remarks>
public record AnthropicOptions
{
    /// <summary>The API key every request is sent with. Required.</summary>
    public string ApiKey { get; set; }

    public string Address { get; set; } = "https://api.anthropic.com";

    /// <summary>
    /// The <c>anthropic-version</c> header. <c>2023-06-01</c> by default, because Anthropic requests are relayed with their
    /// body unchanged and a caller's body may still carry fields a later version refuses.
    /// </summary>
    public string Version { get; set; } = "2023-06-01";

    /// <summary>The most a prompt may answer, when it sets no <c>num_predict</c> of its own.</summary>
    public int MaxTokens { get; set; } = 32000;

    /// <summary>The models this agent offers. A model not listed here is never sent to Anthropic.</summary>
    public AnthropicModel[] Models { get; set; } = [];
}

/// <summary>A hosted model and the size its work units are counted at.</summary>
public record AnthropicModel
{
    /// <summary>Anthropic's model id, for example <c>claude-sonnet-5-5</c>.</summary>
    public string Name { get; set; }

    /// <summary>
    /// The effective parameter count: Anthropic publishes no sizes, so it comes from the price. Output dollars per token
    /// divided by the reference price per work unit; at 0.20 dollars per million work units, 10 dollars per million output
    /// tokens is 50 billion.
    /// </summary>
    public long ParameterCount { get; set; }

    /// <summary>Whether a prompt that asks for thinking gets it. On by default; every current model thinks.</summary>
    public bool SupportsThinking { get; set; } = true;
}
