using Microsoft.Extensions.Options;
using Tharga.Neurolito.Agent.Features.Model;

namespace Tharga.Neurolito.Agent.Features.Ollama;

public static class OllamaRegistration
{
    public static void AddOllama(this WebApplicationBuilder builder, Action<OllamaOptions> options = null)
    {
        var section = builder.Services.BuildServiceProvider().GetService<IConfiguration>().GetSection("Ollama");
        var address = section.GetSection("Address").Value;

        var o = new OllamaOptions
        {
            Address = address ?? "http://localhost:11434",
            Models = section.GetSection("Models").Get<string[]>() ?? [],
            AnthropicContextLength = section.GetValue("AnthropicContextLength", OllamaOptions.DefaultAnthropicContextLength)
        };
        options?.Invoke(o);
        builder.Services.AddSingleton(Options.Create(o));
        builder.Services.AddTransient<IOllamaService, OllamaService>();

        //NOTE: No timeout for generation, pull and delete. The default HttpClient timeout is 100 seconds,
        //and on a CPU-only machine an 8B prompt routinely takes longer. The server's time limit, and the
        //cancel it sends when that passes, decide when a job stops.
        builder.Services.AddTransient<OllamaErrorBodyHandler>();
        builder.Services.AddHttpClient(OllamaService.LongRunningClient, client =>
        {
            client.BaseAddress = new Uri(o.Address);
            client.Timeout = Timeout.InfiniteTimeSpan;
        }).AddHttpMessageHandler<OllamaErrorBodyHandler>();
        builder.Services.AddTransient<IEngineSelfTest, EngineSelfTest>();
        builder.Services.AddTransient<IOllamaModelService, OllamaModelService>();

        //NOTE: No timeout on the client. A generation is long and the answer is relayed as it
        //arrives, so the request ends when the caller cancels rather than on a clock.
        builder.Services.AddHttpClient<IAnthropicPassthroughService, AnthropicPassthroughService>(client => client.Timeout = Timeout.InfiniteTimeSpan);

        //NOTE: A singleton, because it remembers which variants exist and creates one at a time. Creating a
        //variant copies no model files and takes seconds, so this client keeps a timeout.
        builder.Services.AddHttpClient(ContextVariantService.ClientName, client =>
        {
            client.BaseAddress = new Uri(o.Address);
            client.Timeout = TimeSpan.FromMinutes(2);
        });
        builder.Services.AddSingleton<IContextVariantService, ContextVariantService>();
        builder.Services.AddSingleton<IEventService, EventService>();
        builder.Services.AddHostedService<ModelPreloadService>();
    }
}