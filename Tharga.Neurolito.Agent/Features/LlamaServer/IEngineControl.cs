namespace Tharga.Neurolito.Agent.Features.LlamaServer;

/// <summary>What changing the engine's settings needs of llama-server's host: which devices exist, and a way to unload the model.</summary>
internal interface IEngineControl
{
    /// <summary>The raw output of <c>llama-server --list-devices</c>, or null when the program is missing or does not run.</summary>
    Task<string> ListDevicesAsync(CancellationToken cancellationToken);

    /// <summary>Stops llama-server if it runs <paramref name="name"/>, or whatever it runs when <paramref name="name"/> is null.</summary>
    Task UnloadAsync(string name, CancellationToken cancellationToken);
}
