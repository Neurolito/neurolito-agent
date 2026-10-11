namespace Tharga.Neurolito.Agent.Features.LlamaServer;

/// <summary>The <c>LlamaServer</c> section: llama.cpp's own server as the engine, chosen with <c>Engine:Kind = LlamaServer</c>.</summary>
public record LlamaServerOptions
{
    /// <summary>The <c>llama-server</c> program. Default: <c>llama/llama-server(.exe)</c> next to the agent.</summary>
    public string ExecutablePath { get; set; }

    /// <summary>Where models are kept. Default: <c>Neurolito/models</c> under the machine's application data folder.</summary>
    public string ModelsPath { get; set; }

    /// <summary>The context a prompt gets when it asks for none, capped at what the model was trained for.</summary>
    public int ContextLength { get; set; } = 8192;

    /// <summary>The context Anthropic requests get, capped the same way. 0 gives them <see cref="ContextLength"/>.</summary>
    public int AnthropicContextLength { get; set; } = 32768;

    /// <summary>Layers on the GPU (<c>-ngl</c>). The default puts the whole model there; llama-server fits what it can.</summary>
    public int GpuLayers { get; set; } = 999;

    /// <summary>
    /// Requests llama-server runs at once (<c>--parallel</c>). One gives each request the whole context; more split it.
    /// </summary>
    public int Parallel { get; set; } = 1;

    /// <summary>Unload the model after this long without a request, to give the memory back. 0 keeps it loaded.</summary>
    public int IdleUnloadMinutes { get; set; } = 10;

    /// <summary>How long a model may take to load before the request fails.</summary>
    public int LoadTimeoutMinutes { get; set; } = 10;

    /// <summary>More arguments for llama-server, separated by spaces, for example <c>--flash-attn on</c>.</summary>
    public string ExtraArguments { get; set; }

    /// <summary>A Hugging Face access token, for gated models. Optional.</summary>
    public string HuggingFaceToken { get; set; }

    public string HuggingFaceAddress { get; set; } = "https://huggingface.co";

    /// <summary>Models installed at startup, as <c>hf.co/{owner}/{repo}:{quantisation}</c>.</summary>
    public string[] Models { get; set; } = [];

    internal string ResolvedExecutablePath => !string.IsNullOrWhiteSpace(ExecutablePath)
        ? ExecutablePath
        : Path.Combine(AppContext.BaseDirectory, "llama", OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server");

    internal string ResolvedModelsPath => !string.IsNullOrWhiteSpace(ModelsPath)
        ? ModelsPath
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Neurolito", "models");
}
