using Microsoft.Extensions.Options;
using Tharga.Neurolito.Agent.Features.Engine;
using Tharga.Neurolito.Agent.Features.Model;

namespace Tharga.Neurolito.Agent.Features.Anthropic;

public static class AnthropicRegistration
{
    /// <summary>Anthropic's hosted API as the agent's engine. Called by <see cref="EngineRegistration.AddEngine"/>.</summary>
    public static void AddAnthropic(this WebApplicationBuilder builder, Action<AnthropicOptions> options = null)
    {
        var o = builder.Configuration.GetSection("Anthropic").Get<AnthropicOptions>() ?? new AnthropicOptions();
        options?.Invoke(o);

        if (string.IsNullOrWhiteSpace(o.ApiKey)) throw new InvalidOperationException("Engine:Kind is Anthropic, but Anthropic:ApiKey is not set.");

        builder.Services.AddSingleton(Options.Create(o));

        //NOTE: Nothing to preload: the models are the provider's, and a load would be a billed call.
        builder.Services.AddSingleton(Options.Create(new ModelPreloadOptions { Models = [] }));
        builder.Services.AddSingleton<IEngine, AnthropicEngine>();

        //NOTE: No timeout: a generation lasts as long as it lasts. The server's time limit, and the cancel it sends, decide when a job stops.
        builder.Services.AddHttpClient(AnthropicEngine.ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
    }
}
