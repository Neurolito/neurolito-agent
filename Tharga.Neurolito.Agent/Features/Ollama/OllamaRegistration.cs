using Microsoft.Extensions.Options;
using Tharga.Neurolito.Agent.Features.Engine;
using Tharga.Neurolito.Agent.Features.Model;

namespace Tharga.Neurolito.Agent.Features.Ollama;

public static class OllamaRegistration
{
    /// <summary>Ollama as the agent's engine. Called by <see cref="EngineRegistration.AddEngine"/>.</summary>
    public static void AddOllama(this WebApplicationBuilder builder, Action<OllamaOptions> options = null)
    {
        var section = builder.Configuration.GetSection("Ollama");
        var address = section.GetSection("Address").Value;

        var o = new OllamaOptions
        {
            Address = address ?? "http://localhost:11434",
            Models = section.GetSection("Models").Get<string[]>() ?? [],
            AnthropicContextLength = section.GetValue("AnthropicContextLength", OllamaOptions.DefaultAnthropicContextLength)
        };
        options?.Invoke(o);
        builder.Services.AddSingleton(Options.Create(o));
        builder.Services.AddSingleton(Options.Create(new ModelPreloadOptions { Models = o.Models }));
        builder.Services.AddSingleton<IEngine, OllamaEngine>();

        //NOTE: No timeout for generation, pull and delete. The default HttpClient timeout is 100 seconds,
        //and on a CPU-only machine an 8B prompt routinely takes longer. The server's time limit, and the
        //cancel it sends when that passes, decide when a job stops.
        builder.Services.AddTransient<OllamaErrorBodyHandler>();
        builder.Services.AddHttpClient(OllamaEngine.LongRunningClient, client =>
        {
            client.BaseAddress = new Uri(o.Address);
            client.Timeout = Timeout.InfiniteTimeSpan;
        }).AddHttpMessageHandler<OllamaErrorBodyHandler>();

        //NOTE: A singleton, because it remembers which variants exist and creates one at a time. Creating a
        //variant copies no model files and takes seconds, so this client keeps a timeout.
        builder.Services.AddHttpClient(ContextVariantService.ClientName, client =>
        {
            client.BaseAddress = new Uri(o.Address);
            client.Timeout = TimeSpan.FromMinutes(2);
        });
        builder.Services.AddSingleton<IContextVariantService, ContextVariantService>();
    }
}
