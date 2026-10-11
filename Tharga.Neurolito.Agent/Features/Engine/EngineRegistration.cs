using Tharga.Neurolito.Agent.Features.LlamaServer;
using Tharga.Neurolito.Agent.Features.Model;
using Tharga.Neurolito.Agent.Features.Ollama;

namespace Tharga.Neurolito.Agent.Features.Engine;

public static class EngineRegistration
{
    /// <summary>
    /// The engine named by <c>Engine:Kind</c> - <c>Ollama</c> (the default) or <c>LlamaServer</c> - and everything the
    /// agent does with it.
    /// </summary>
    public static void AddEngine(this WebApplicationBuilder builder)
    {
        if (IsLlamaServer(builder.Configuration["Engine:Kind"]))
        {
            builder.AddLlamaServer();
        }
        else
        {
            builder.AddOllama();
        }

        builder.Services.AddTransient<IEngineService, EngineService>();
        builder.Services.AddTransient<IEngineSelfTest, EngineSelfTest>();

        //NOTE: No timeout on the client. A generation is long and the answer is relayed as it
        //arrives, so the request ends when the caller cancels rather than on a clock.
        builder.Services.AddHttpClient<IAnthropicPassthroughService, AnthropicPassthroughService>(client => client.Timeout = Timeout.InfiniteTimeSpan);

        builder.Services.AddSingleton<IEventService, EventService>();
        builder.Services.AddSingleton<LoadedModelsReporter>();
        builder.Services.AddSingleton<ILoadedModelsReporter>(s => s.GetRequiredService<LoadedModelsReporter>());
        builder.Services.AddHostedService(s => s.GetRequiredService<LoadedModelsReporter>());
        builder.Services.AddHostedService<ModelPreloadService>();
    }

    internal static bool IsLlamaServer(string kind)
    {
        return string.Equals(kind, "LlamaServer", StringComparison.OrdinalIgnoreCase)
               || string.Equals(kind, "llama-server", StringComparison.OrdinalIgnoreCase);
    }
}
