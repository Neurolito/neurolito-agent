using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Tharga.Communication.Client;
using Tharga.Neurolito.Agent.Framework;
using ClientOptions = Tharga.Communication.Client.CommunicationOptions;

namespace Tharga.Neurolito.Agent.Tests;

public class AgentContainerConfigurationTests
{
    private const string ApiKeyVariable = "Tharga__Communication__ApiKey";

    [Fact]
    public void AContainerCanNameItsTeamThroughTheEnvironment()
    {
        // A container has no install script to write appsettings.Agent.json, so the only way to give it
        // a team is `docker run -e`. If this binding broke, every container would connect unassigned.
        var previous = Environment.GetEnvironmentVariable(ApiKeyVariable);
        Environment.SetEnvironmentVariable(ApiKeyVariable, "team-key-from-env");
        try
        {
            Assert.Equal("team-key-from-env", ResolveAgentOptions(_ => { }).ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ApiKeyVariable, previous);
        }
    }

    [Fact]
    public void TheKeyTheChocolateyInstallWritesReachesTheHub()
    {
        // appsettings.Agent.json puts the key under Tharga:Communication:ApiKey. The library binds that
        // section but drops ApiKey, which is why AgentConnection carries it across.
        var options = ResolveAgentOptions(c => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            { AgentConnection.ApiKeySetting, "team-key-from-file" }
        }));

        Assert.Equal("team-key-from-file", options.ApiKey);
    }

    [Fact]
    public void TheLibraryAloneNowBindsAConfiguredKey()
    {
        // Tharga.Communication 1.1.5 and later bind ApiKey themselves; up to 1.1.2 it dropped it, which AgentConnection
        // exists for. The workaround stays until every agent in the field runs a version that binds it.
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            { AgentConnection.ApiKeySetting, "team-key-from-file" }
        });
        builder.AddThargaCommunicationClient(o => o.Pattern = AgentConnection.HubPattern);

        using var provider = builder.Services.BuildServiceProvider();

        Assert.Equal("team-key-from-file", provider.GetRequiredService<IOptions<ClientOptions>>().Value.ApiKey);
    }

    [Fact]
    public void AnAgentWithNoKeyConnectsWithoutOne()
    {
        var options = ResolveAgentOptions(_ => { });

        Assert.Null(options.ApiKey);
        Assert.Equal(AgentConnection.HubPattern, options.Pattern);
    }

    private static ClientOptions ResolveAgentOptions(Action<IConfigurationBuilder> configure)
    {
        var builder = Host.CreateApplicationBuilder();
        configure(builder.Configuration);
        builder.AddThargaCommunicationClient(o => AgentConnection.Configure(o, builder.Configuration));

        using var provider = builder.Services.BuildServiceProvider();

        return provider.GetRequiredService<IOptions<ClientOptions>>().Value;
    }
}
