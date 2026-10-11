using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Tharga.Neurolito.Agent.Features.Ollama;

namespace Tharga.Neurolito.Agent.Tests;

/// <summary>
/// The agent's generation call has no client-side time limit. The default 100-second HttpClient
/// timeout cut off long generations on CPU-only agents.
/// </summary>
public class AgentOllamaClientTests
{
    [Fact]
    public void GenerationUsesAClientWithNoTimeout()
    {
        var builder = WebApplication.CreateBuilder();
        OllamaRegistration.AddOllama(builder, o => o.Address = "http://ollama.example:11434");
        using var provider = builder.Services.BuildServiceProvider();

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(OllamaEngine.LongRunningClient);

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }
}
