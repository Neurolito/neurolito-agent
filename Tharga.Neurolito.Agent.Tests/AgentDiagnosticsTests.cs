using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tharga.Neurolito.Agent.Features.Diagnostics;
using Tharga.Neurolito.Agent.Features.LlamaServer;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Tests;

/// <summary>
/// The server can ask an agent what it sees and change the two engine settings it lets the server change. The agent runs
/// a probe from a closed list, never a command; masks what must not leave the machine; and refuses a device it does not list.
/// </summary>
public class AgentDiagnosticsTests : IDisposable
{
    private const string TwoDevices = """
        ggml_vulkan: Found 2 Vulkan devices:
        ggml_vulkan: 0 = NVIDIA GeForce RTX 5070 (NVIDIA) | uma: 0 | fp16: 1
        Available devices:
          Vulkan0: NVIDIA GeForce RTX 5070 (12227 MiB, 11000 MiB free)
          Vulkan1: AMD Radeon(TM) Graphics (512 MiB, 400 MiB free)
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "neurolito-diagnostics-" + Guid.NewGuid().ToString("N"));

    public AgentDiagnosticsTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    [Fact]
    public void TheDevicesAreReadFromWhatLlamaServerPrintsAndNoiseIsIgnored()
    {
        var devices = DeviceList.Parse(TwoDevices);

        Assert.Equal(["Vulkan0", "Vulkan1"], devices.Select(x => x.Name));
        Assert.Equal("NVIDIA GeForce RTX 5070", devices[0].Description);
        Assert.Equal(12227, devices[0].TotalMiB);
        Assert.Equal(400, devices[1].FreeMiB);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("llama-server: command not found")]
    public void NoDevicesAreFoundInOutputWithNone(string output)
    {
        Assert.Empty(DeviceList.Parse(output));
    }

    [Theory]
    [InlineData("Vulkan0", "Vulkan0")]
    [InlineData("vulkan1", "Vulkan1")]
    [InlineData("RTX 5070", "Vulkan0")]
    [InlineData("radeon", "Vulkan1")]
    [InlineData("  Vulkan0  ", "Vulkan0")]
    public void ADeviceIsFoundByItsNameOrByAPartOfItsDescription(string setting, string expected)
    {
        Assert.Equal(expected, DeviceList.Resolve(setting, DeviceList.Parse(TwoDevices)));
    }

    [Theory]
    [InlineData("Vulkan7")]
    [InlineData("nvidia radeon")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnknownDeviceIsNotGuessedAt(string setting)
    {
        Assert.Null(DeviceList.Resolve(setting, DeviceList.Parse(TwoDevices)));
    }

    [Fact]
    public void ADescriptionThatFitsTwoDevicesIsNotChosen()
    {
        var devices = DeviceList.Parse("  Vulkan0: NVIDIA GeForce RTX 5070 (1 MiB)\n  Vulkan1: NVIDIA GeForce RTX 4070 (1 MiB)");

        Assert.Null(DeviceList.Resolve("NVIDIA", devices));
    }

    [Fact]
    public void ALlamaServerIsToldWhichDeviceToUseOnlyWhenOneIsChosen()
    {
        var without = LlamaServerHost.Arguments(new LlamaServerOptions(), "m", "m.gguf", 8192, 5000).ToArray();
        var with = LlamaServerHost.Arguments(new LlamaServerOptions(), "m", "m.gguf", 8192, 5000, "Vulkan0").ToArray();

        Assert.DoesNotContain("--device", without);
        Assert.Equal(["-m", "m.gguf", "--alias", "m", "--jinja", "--device", "Vulkan0", "-c", "8192", "--parallel", "1", "--host", "127.0.0.1", "--port", "5000"], with);
    }

    [Theory]
    [InlineData("api_key=abc123def456", "api_key=***")]
    [InlineData("X-API-KEY: abc123def456", "X-API-KEY: ***")]
    [InlineData("Authorization: Bearer abc123def456", "Authorization: ***")]
    [InlineData("""{"token":"abc123def456"}""", """{"token":"***"}""")]
    [InlineData("password = hunter2hunter2", "password = ***")]
    public void AValueAfterAWordLikeKeyOrTokenIsHidden(string line, string expected)
    {
        Assert.Equal(expected, SecretMask.Apply(line));
    }

    [Fact]
    public void AConfiguredSecretAndALongUnbrokenTokenAreHiddenWhereverTheyAre()
    {
        var text = SecretMask.Apply($"downloading with hf_abcdefghijklmnop for {new string('a', 60)} done", "hf_abcdefghijklmnop");

        Assert.Equal("downloading with *** for *** done", text);
    }

    [Fact]
    public void OrdinaryLogTextIsLeftAlone()
    {
        const string line = "2026-11-21 10:03:32 [INF] llama-server is ready with qwen3-coder:30b. Tokens per second 5.6, context 8192";

        Assert.Equal(line, SecretMask.Apply(line));
    }

    [Fact]
    public void TheLastLinesOfALogAreReadWhileTheAgentStillWritesToIt()
    {
        var path = Path.Combine(_folder, "agent.log");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        writer.Write(Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(1, 10).Select(x => $"line {x}")) + "\n"));
        writer.Flush();

        Assert.Equal(["line 8", "line 9", "line 10"], LogTail.Read(path, 3, 64 * 1024));
    }

    [Fact]
    public void ALogReadFromTheMiddleDropsTheLineItStartedIn()
    {
        var path = Path.Combine(_folder, "agent.log");
        File.WriteAllText(path, "aaaaaaaaaa\nbbbbbbbbbb\ncccccccccc\n");

        var lines = LogTail.Read(path, 100, 15);

        Assert.Equal(["cccccccccc"], lines);
    }

    [Fact]
    public void TheNewestLogIsTheOneWrittenLastInAnyFolder()
    {
        var older = Directory.CreateDirectory(Path.Combine(_folder, "a")).FullName;
        var newer = Directory.CreateDirectory(Path.Combine(_folder, "b")).FullName;
        File.WriteAllText(Path.Combine(older, "agent-1.log"), "old");
        File.WriteAllText(Path.Combine(newer, "agent-2.log"), "new");
        File.SetLastWriteTimeUtc(Path.Combine(older, "agent-1.log"), DateTime.UtcNow.AddDays(-1));

        Assert.Equal(Path.Combine(newer, "agent-2.log"), LogTail.Newest([older, newer, Path.Combine(_folder, "missing"), null]));
        Assert.Null(LogTail.Newest([Path.Combine(_folder, "missing")]));
    }

    [Theory]
    [InlineData("rm -rf /")]
    [InlineData("")]
    [InlineData(null)]
    public async Task AProbeThatIsNotInTheListIsRefused(string probe)
    {
        var reply = await Diagnostics().RunAsync(new AgentDiagnosticsRequest { Probe = probe }, CancellationToken.None);

        Assert.Equal(DiagnosticsStatus.UnknownProbe, reply.Status);
        Assert.Null(reply.Text);
        Assert.Contains("Log, Devices, Gpu, Engine, Version", reply.Message);
    }

    [Fact]
    public async Task TheLogProbeReturnsTheNewestLinesCappedAndMasked()
    {
        File.WriteAllText(Path.Combine(_folder, "agent.log"), string.Join('\n', Enumerable.Range(1, 500).Select(x => x == 500 ? "last api_key=abc123def456" : $"line {x}")));

        var reply = await Diagnostics(maxLogLines: 5).RunAsync(new AgentDiagnosticsRequest { Probe = "log", Lines = 1000 }, CancellationToken.None);

        Assert.Equal(DiagnosticsStatus.Ok, reply.Status);
        Assert.Equal(DiagnosticsProbes.Log, reply.Probe);
        Assert.Equal(5, reply.Text.Split('\n').Length);
        Assert.EndsWith("last api_key=***", reply.Text);
        Assert.DoesNotContain("abc123def456", reply.Text);
    }

    [Fact]
    public async Task TheLogProbeSaysSoWhenThereIsNoLog()
    {
        var reply = await Diagnostics(logDirectory: Path.Combine(_folder, "missing")).RunAsync(new AgentDiagnosticsRequest { Probe = DiagnosticsProbes.Log }, CancellationToken.None);

        Assert.Equal(DiagnosticsStatus.Unavailable, reply.Status);
        Assert.Equal("No log file was found.", reply.Message);
    }

    [Theory]
    [InlineData(DiagnosticsProbes.Devices)]
    [InlineData(DiagnosticsProbes.Engine)]
    public async Task AnAgentWithoutLlamaServerSaysItHasNoSuchEngine(string probe)
    {
        var reply = await Diagnostics().RunAsync(new AgentDiagnosticsRequest { Probe = probe }, CancellationToken.None);

        Assert.Equal(DiagnosticsStatus.Unavailable, reply.Status);
        Assert.Contains("llama-server", reply.Message);
    }

    [Fact]
    public async Task TheVersionProbeNamesTheAgentAndTheMachine()
    {
        var reply = await Diagnostics().RunAsync(new AgentDiagnosticsRequest { Probe = "VERSION" }, CancellationToken.None);

        Assert.Equal(DiagnosticsStatus.Ok, reply.Status);
        Assert.Contains("Agent:", reply.Text);
        Assert.Contains("System:", reply.Text);
    }

    [Fact]
    public async Task ARequestWithNothingToChangeChangesNothing()
    {
        var control = new FakeControl(TwoDevices);
        var reply = await Store(control, out _).ChangeAsync(new ChangeEngineSettingsRequest(), CancellationToken.None);

        Assert.Equal(EngineSettingsStatus.Unchanged, reply.Status);
        Assert.Equal(0, control.Unloads);
    }

    [Fact]
    public async Task ADeviceTheEngineListsIsKeptUnloadsTheModelAndSurvivesARestart()
    {
        var control = new FakeControl(TwoDevices);
        var store = Store(control, out var options);

        var reply = await store.ChangeAsync(new ChangeEngineSettingsRequest { Device = "RTX 5070", GpuLayers = 99 }, CancellationToken.None);

        Assert.Equal(EngineSettingsStatus.Changed, reply.Status);
        Assert.Equal("RTX 5070", options.Device);
        Assert.Equal(99, options.GpuLayers);
        Assert.Equal(1, control.Unloads);

        var restarted = new LlamaServerOptions();
        EngineSettingsStore.ApplySaved(restarted, SettingsPath);
        Assert.Equal("RTX 5070", restarted.Device);
        Assert.Equal(99, restarted.GpuLayers);
    }

    [Fact]
    public async Task ADeviceTheEngineDoesNotListIsRefusedAndNothingChanges()
    {
        var control = new FakeControl(TwoDevices);
        var store = Store(control, out var options);

        var reply = await store.ChangeAsync(new ChangeEngineSettingsRequest { Device = "Vulkan9", GpuLayers = 10 }, CancellationToken.None);

        Assert.Equal(EngineSettingsStatus.Refused, reply.Status);
        Assert.Contains("Vulkan0 = NVIDIA GeForce RTX 5070", reply.Message);
        Assert.Null(options.Device);
        Assert.Null(options.GpuLayers);
        Assert.Equal(0, control.Unloads);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public async Task ADeviceCannotBeCheckedWhenTheEngineListsNone()
    {
        var reply = await Store(new FakeControl(null), out var options).ChangeAsync(new ChangeEngineSettingsRequest { Device = "Vulkan0" }, CancellationToken.None);

        Assert.Equal(EngineSettingsStatus.Refused, reply.Status);
        Assert.Null(options.Device);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(1000)]
    public async Task LayersOutsideTheRangeAreRefused(int layers)
    {
        var reply = await Store(new FakeControl(TwoDevices), out var options).ChangeAsync(new ChangeEngineSettingsRequest { GpuLayers = layers }, CancellationToken.None);

        Assert.Equal(EngineSettingsStatus.Refused, reply.Status);
        Assert.Null(options.GpuLayers);
    }

    [Fact]
    public async Task EmptyAndMinusOneGoBackToTheEnginesOwnChoice()
    {
        var control = new FakeControl(TwoDevices);
        var store = Store(control, out var options);
        await store.ChangeAsync(new ChangeEngineSettingsRequest { Device = "Vulkan0", GpuLayers = 20 }, CancellationToken.None);

        var reply = await store.ChangeAsync(new ChangeEngineSettingsRequest { Device = "", GpuLayers = -1 }, CancellationToken.None);

        Assert.Equal(EngineSettingsStatus.Changed, reply.Status);
        Assert.Null(options.Device);
        Assert.Null(options.GpuLayers);

        var restarted = new LlamaServerOptions { Device = "installer", GpuLayers = 5 };
        EngineSettingsStore.ApplySaved(restarted, SettingsPath);
        Assert.Null(restarted.Device);
        Assert.Null(restarted.GpuLayers);
    }

    [Fact]
    public async Task OneSettingChangedLeavesTheOtherAsTheServerLastSetIt()
    {
        var store = Store(new FakeControl(TwoDevices), out _);
        await store.ChangeAsync(new ChangeEngineSettingsRequest { GpuLayers = 30 }, CancellationToken.None);
        await store.ChangeAsync(new ChangeEngineSettingsRequest { Device = "Vulkan0" }, CancellationToken.None);

        var restarted = new LlamaServerOptions();
        EngineSettingsStore.ApplySaved(restarted, SettingsPath);

        Assert.Equal("Vulkan0", restarted.Device);
        Assert.Equal(30, restarted.GpuLayers);
    }

    [Fact]
    public void AnAgentWithNothingTheServerSetFollowsItsInstaller()
    {
        var options = new LlamaServerOptions { Device = "Vulkan0", GpuLayers = 12 };

        EngineSettingsStore.ApplySaved(options, SettingsPath);

        Assert.Equal("Vulkan0", options.Device);
        Assert.Equal(12, options.GpuLayers);
    }

    [Fact]
    public async Task AnAgentWithoutDiagnosticsOrEngineAnswersInsteadOfStayingSilent()
    {
        var probe = await new AgentDiagnosticsRequestHandler().Handle(new AgentDiagnosticsRequest { Probe = DiagnosticsProbes.Log });
        var change = await new ChangeEngineSettingsRequestHandler().Handle(new ChangeEngineSettingsRequest { GpuLayers = 1 });

        Assert.Equal(DiagnosticsStatus.Unavailable, probe.Status);
        Assert.Equal(EngineSettingsStatus.Unavailable, change.Status);
    }

    private string SettingsPath => Path.Combine(_folder, "engine.json");

    private EngineSettingsStore Store(IEngineControl control, out LlamaServerOptions options)
    {
        options = new LlamaServerOptions();
        return new EngineSettingsStore(options, control, SettingsPath, NullLogger<EngineSettingsStore>.Instance);
    }

    private AgentDiagnostics Diagnostics(int maxLogLines = 200, string logDirectory = null)
    {
        var options = new DiagnosticsOptions { MaxLogLines = maxLogLines, LogDirectory = logDirectory ?? _folder };
        return new AgentDiagnostics(Microsoft.Extensions.Options.Options.Create(options), new ConfigurationBuilder().Build(), NullLogger<AgentDiagnostics>.Instance);
    }

    private sealed class FakeControl(string devices) : IEngineControl
    {
        public int Unloads { get; private set; }

        public Task<string> ListDevicesAsync(CancellationToken cancellationToken) => Task.FromResult(devices);

        public Task UnloadAsync(string name, CancellationToken cancellationToken)
        {
            Unloads++;
            return Task.CompletedTask;
        }
    }
}
