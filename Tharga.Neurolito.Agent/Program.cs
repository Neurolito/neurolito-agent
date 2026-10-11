using System.Reflection;
using Microsoft.ApplicationInsights.AspNetCore.Extensions;
using Microsoft.Extensions.Hosting.WindowsServices;
using Quilt4Net.Toolkit;
using Quilt4Net.Toolkit.Api;
using Quilt4Net.Toolkit.Features.Api;
using Quilt4Net.Toolkit.Health;
using Serilog;
using Tharga.Communication.Client;
using Tharga.Neurolito.Agent.Features.ConsoleLog;
using Tharga.Neurolito.Agent.Features.Desktop;
using Tharga.Neurolito.Agent.Features.Greeting;
using Tharga.Neurolito.Agent.Features.Engine;
using Tharga.Neurolito.Agent.Framework;
using Velopack;

//NOTE: First, before anything else: when Velopack's Update.exe starts the agent to finish an install or an update, it
//does what it must and exits here. Otherwise it returns at once.
VelopackApp.Build().Run();

var builder = WebApplication.CreateBuilder(args);

//NOTE: The team credential, written by chocolateyInstall.ps1 and read as Tharga:Communication:ApiKey.
//Its own file rather than appsettings.json for two reasons: the package overwrites appsettings.json
//on every upgrade, which would silently unassign the agent; and a secret in the file people open to
//change a log level is a secret that ends up in a paste. Optional, because a developer running the
//agent from the repository has no such file, and an agent installed by an older package has none either.
builder.Configuration.AddJsonFile("appsettings.Agent.json", optional: true, reloadOnChange: true);

//NOTE: The Windows installer's settings: everything but the team key in the registry, and the team key in a file in a
//folder only the service and administrators can read (Windows Installer applies a key's permissions only when it
//creates the key, and a registry key with values cannot be created that way). Last, so they win over appsettings.json.
if (OperatingSystem.IsWindows())
{
    ((IConfigurationBuilder)builder.Configuration).Add(new RegistryConfigurationSource(RegistryConfigurationSource.AgentKey));
    builder.Configuration.AddIniFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Neurolito", "settings", "agent.ini"), optional: true, reloadOnChange: false);
}

builder.Logging.ClearProviders();

builder.Host.UseSerilog((ctx, services, lc) =>
    lc.ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .WriteTo.Sink(services.GetRequiredService<ConsoleLogForwarderSink>())
, writeToProviders: true);

builder.Services.AddOpenApi();
builder.Services.AddControllers();
builder.Services.AddSwaggerGen();

var aiConnectionString = builder.Configuration["ApplicationInsights:ConnectionString"];
if (!string.IsNullOrWhiteSpace(aiConnectionString))
{
    builder.Services.AddApplicationInsightsTelemetry(new ApplicationInsightsServiceOptions { ConnectionString = aiConnectionString });
}

builder.AddEngine();
builder.AddDesktop();

builder.AddQuilt4NetHealth(o =>
{
    o.Endpoints[HealthEndpoint.Health].Head.State = EndpointState.Hidden;
    o.Endpoints[HealthEndpoint.Live].Head.State = EndpointState.Hidden;
    o.Endpoints[HealthEndpoint.Live].Get.State = EndpointState.Hidden;
    o.Endpoints[HealthEndpoint.Ready].Head.State = EndpointState.Hidden;
    o.Endpoints[HealthEndpoint.Health].Get.Access.Level = AccessLevel.Everyone;
    o.Endpoints[HealthEndpoint.Health].Get.Details = DetailsLevel.Everyone;

    o.OverrideState = EndpointState.Visible;

    o.AddComponentService<ComponentService>();
});
builder.AddQuilt4NetLogging().AddHttpRequestLogging();

//NOTE: The key must be carried across explicitly - the library binds ServerAddress from configuration
//but not ApiKey. See AgentConnection.
builder.AddThargaCommunicationClient(o => AgentConnection.Configure(o, builder.Configuration));

builder.Services.AddSingleton(sp => new ConsoleLogForwarderSink(sp));

//NOTE: The endpoints that act on the machine are off unless configured on - see LocalApiOptions.
builder.Services.Configure<LocalApiOptions>(builder.Configuration.GetSection(LocalApiOptions.SectionName));

builder.Services.AddHostedService<GreetingService>();
//builder.Services.AddHostedService<HeartbeatService>();

try
{
    builder.Host.UseWindowsService();
    builder.UseDesktopLifetime();

    var app = builder.Build();

    app.UseQuilt4NetLogging();

    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var asm = Assembly.GetEntryAssembly()?.GetName();
        var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Tharga.Neurolito.Agent.Startup");
        startupLogger.LogInformation(
            "Tharga.Neurolito.Agent started. Name={Name} Version={Version} Environment={Environment} Machine={Machine} Runtime={Runtime} Pid={Pid} AppInsightsEnabled={AppInsightsEnabled}",
            asm?.Name,
            asm?.Version?.ToString(),
            app.Environment.EnvironmentName,
            Environment.MachineName,
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Environment.ProcessId,
            !string.IsNullOrWhiteSpace(aiConnectionString));
    });

    app.MapGet("/", () => Results.Content(
        """
        <!doctype html>
        <html>
        <head><title>Tharga Neurolito Agent</title></head>
        <body>
            <h1>Tharga Neurolito Agent</h1>
            <a href="/swagger">swagger</a>
        </body>
        </html>
        """,
        "text/html"
    )).ExcludeFromDescription();
    app.MapGet("/favicon.ico", () =>
    {
        var path = Path.Combine(app.Environment.ContentRootPath, "Resources", "favicon.png");
        return Results.File(path, "image/png");
    }).ExcludeFromDescription();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    app.UseSwagger();
    app.UseSwaggerUI();

    if (!builder.Environment.IsEnvironment("Docker") && !WindowsServiceHelpers.IsWindowsService())
    {
        app.UseHttpsRedirection();
    }

    app.UseAuthorization();

    app.MapControllers();

    app.UseQuilt4NetHealth();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Neurolito Agent terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}