namespace Tharga.Neurolito.Agent.Features.Ollama;

public record OllamaOptions
{
    public string Address { get; set; }

    /// <summary>
    /// Models installed automatically at startup if not already present. Leave empty to install
    /// nothing and manage models by hand or from the web UI.
    /// </summary>
    public string[] Models { get; set; } = [];

    /// <summary>
    /// The context, in tokens, given to Anthropic requests (<c>/v1/messages</c>), capped at what each model was
    /// trained for. 0 forwards to the model as installed, with Ollama's own default.
    /// </summary>
    /// <remarks>
    /// Ollama's Anthropic endpoint takes no context option, and its default keeps only the end of a long prompt:
    /// on 2026-10-02 a ~15,000-token request reached the model as 2,049 tokens, so Claude Code's system prompt
    /// and tools were gone and every answer was a few words of text. 32768 holds Claude Code's requests
    /// with room for the conversation to grow. See <see cref="ContextVariantService"/>.
    /// </remarks>
    public int AnthropicContextLength { get; set; } = DefaultAnthropicContextLength;

    public const int DefaultAnthropicContextLength = 32768;
}