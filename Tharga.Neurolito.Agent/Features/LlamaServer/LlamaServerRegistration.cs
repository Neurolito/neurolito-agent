using Microsoft.Extensions.Options;
using Tharga.Neurolito.Agent.Features.Diagnostics;
using Tharga.Neurolito.Agent.Features.Engine;
using Tharga.Neurolito.Agent.Features.Model;

namespace Tharga.Neurolito.Agent.Features.LlamaServer;

public static class LlamaServerRegistration
{
    /// <summary>llama-server as the agent's engine. Called by <see cref="EngineRegistration.AddEngine"/>.</summary>
    public static void AddLlamaServer(this WebApplicationBuilder builder, Action<LlamaServerOptions> options = null)
    {
        var o = builder.Configuration.GetSection("LlamaServer").Get<LlamaServerOptions>() ?? new LlamaServerOptions();
        options?.Invoke(o);

        //NOTE: What the server set wins over the installer's settings, as with the desktop's.
        EngineSettingsStore.ApplySaved(o, EngineSettingsStore.DefaultPath);

        builder.Services.AddSingleton(Options.Create(o));
        builder.Services.AddSingleton<IEngineSettingsStore>(s => new EngineSettingsStore(o, s.GetRequiredService<LlamaServerHost>(), EngineSettingsStore.DefaultPath, s.GetRequiredService<ILogger<EngineSettingsStore>>()));
        builder.Services.AddSingleton(Options.Create(new ModelPreloadOptions { Models = o.Models ?? [] }));
        builder.Services.AddSingleton(new ModelStore(o.ResolvedModelsPath));
        builder.Services.AddSingleton<HuggingFaceClient>();
        builder.Services.AddSingleton<LlamaServerHost>();
        builder.Services.AddHostedService(s => s.GetRequiredService<LlamaServerHost>());
        builder.Services.AddSingleton<IEngine, LlamaServerEngine>();

        //NOTE: No timeouts: a generation and a download last as long as they last. The server's time limit, and the
        //cancel it sends, decide when a job stops.
        builder.Services.AddHttpClient(LlamaServerHost.ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
        builder.Services.AddHttpClient(HuggingFaceClient.ClientName, client =>
        {
            client.BaseAddress = new Uri(o.HuggingFaceAddress.TrimEnd('/') + "/");
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
    }
}
