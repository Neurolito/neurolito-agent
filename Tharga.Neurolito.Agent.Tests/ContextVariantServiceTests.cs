using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Tharga.Neurolito.Agent.Features.Ollama;

namespace Tharga.Neurolito.Agent.Tests;

/// <summary>
/// The agent gives Anthropic requests a real context by running them on a variant of the model.
/// </summary>
public class ContextVariantServiceTests
{
    private const string Model = "qwen3-coder:30b";

    [Fact]
    public async Task AVariantIsCreatedWithTheWantedContext()
    {
        var ollama = new FakeOllama().Install(Model, trained: 262144);

        var resolved = await Build(ollama).ResolveAsync(Model, CancellationToken.None);

        Assert.Equal("qwen3-coder-neurolito-ctx32768:30b", resolved);
        var create = Assert.Single(ollama.Created);
        Assert.Equal(Model, create.From);
        Assert.Equal(32768, create.NumCtx);
    }

    [Fact]
    public async Task TheContextIsCappedAtWhatTheModelWasTrainedFor()
    {
        // Asking a model for more context than it was trained on gives nothing but memory use.
        var ollama = new FakeOllama().Install("llama3.1:8b", trained: 8192);

        Assert.Equal("llama3.1-neurolito-ctx8192:8b", await Build(ollama).ResolveAsync("llama3.1:8b", CancellationToken.None));
    }

    [Fact]
    public async Task AVariantIsCreatedOnce()
    {
        var ollama = new FakeOllama().Install(Model, trained: 262144);
        var sut = Build(ollama);

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => sut.ResolveAsync(Model, CancellationToken.None)));
        await sut.ResolveAsync(Model, CancellationToken.None);

        Assert.Single(ollama.Created);
    }

    [Fact]
    public async Task AnExistingVariantIsUsedWithoutCreatingIt()
    {
        // The agent restarted; the variant it made last time is still in Ollama.
        var ollama = new FakeOllama().Install(Model, trained: 262144).Install("qwen3-coder-neurolito-ctx32768:30b", trained: 262144, numCtx: 32768);

        Assert.Equal("qwen3-coder-neurolito-ctx32768:30b", await Build(ollama).ResolveAsync(Model, CancellationToken.None));
        Assert.Empty(ollama.Created);
    }

    [Fact]
    public async Task AVariantForAnEarlierSettingIsRemoved()
    {
        var ollama = new FakeOllama().Install(Model, trained: 262144).Install("qwen3-coder-neurolito-ctx16384:30b", trained: 262144, numCtx: 16384);

        await Build(ollama).ResolveAsync(Model, CancellationToken.None);

        Assert.Equal(["qwen3-coder-neurolito-ctx16384:30b"], ollama.Deleted);
    }

    [Fact]
    public async Task AModelAlreadyGivenEnoughContextIsUsedAsItIs()
    {
        var ollama = new FakeOllama().Install(Model, trained: 262144, numCtx: 65536);

        Assert.Equal(Model, await Build(ollama).ResolveAsync(Model, CancellationToken.None));
        Assert.Empty(ollama.Created);
    }

    [Fact]
    public async Task ZeroLeavesEveryRequestOnTheInstalledModel()
    {
        var ollama = new FakeOllama().Install(Model, trained: 262144);

        Assert.Equal(Model, await Build(ollama, contextLength: 0).ResolveAsync(Model, CancellationToken.None));
        Assert.Equal(0, ollama.Calls);
    }

    [Fact]
    public async Task AnOllamaThatCannotCreateTheVariantKeepsWorkingAsBefore()
    {
        // Ollama before 0.5 has no "from" on api/create.
        var ollama = new FakeOllama { RefuseCreate = true }.Install(Model, trained: 262144);

        Assert.Equal(Model, await Build(ollama).ResolveAsync(Model, CancellationToken.None));
    }

    [Fact]
    public async Task AVariantAskedForByNameIsNotVariedAgain()
    {
        var ollama = new FakeOllama();

        Assert.Equal("qwen3-coder-neurolito-ctx32768:30b", await Build(ollama).ResolveAsync("qwen3-coder-neurolito-ctx32768:30b", CancellationToken.None));
        Assert.Equal(0, ollama.Calls);
    }

    [Fact]
    public async Task UninstallingAModelRemovesItsVariants()
    {
        // A variant shares the model's files; one left behind would keep them on disk.
        var ollama = new FakeOllama().Install(Model, trained: 262144).Install("llama3.2:latest", trained: 131072);
        var sut = Build(ollama);
        await sut.ResolveAsync(Model, CancellationToken.None);
        await sut.ResolveAsync("llama3.2", CancellationToken.None);

        await sut.RemoveVariantsAsync(Model, CancellationToken.None);

        Assert.Equal(["qwen3-coder-neurolito-ctx32768:30b"], ollama.Deleted);
    }

    [Fact]
    public async Task AModelThatIsNotInstalledGoesToOllamaUnchanged()
    {
        // Ollama's own error then reaches the caller, as it always has.
        Assert.Equal("missing:1b", await Build(new FakeOllama()).ResolveAsync("missing:1b", CancellationToken.None));
    }

    private static ContextVariantService Build(FakeOllama ollama, int contextLength = OllamaOptions.DefaultAnthropicContextLength)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(ContextVariantService.ClientName).Returns(_ => new HttpClient(ollama, false) { BaseAddress = new Uri("http://ollama/") });

        return new ContextVariantService(factory, Options.Create(new OllamaOptions { Address = "http://ollama/", AnthropicContextLength = contextLength }), NullLogger<ContextVariantService>.Instance);
    }

    /// <summary>The four Ollama endpoints the service uses, over a list of installed models.</summary>
    private sealed class FakeOllama : HttpMessageHandler
    {
        private readonly Dictionary<string, (int Trained, int NumCtx)> _models = new(StringComparer.OrdinalIgnoreCase);
        private int _calls;

        public bool RefuseCreate { get; init; }
        public List<(string From, int NumCtx)> Created { get; } = [];
        public List<string> Deleted { get; } = [];
        public int Calls => _calls;

        public FakeOllama Install(string name, int trained, int numCtx = 0)
        {
            _models[name] = (trained, numCtx);
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var body = request.Content == null ? default : JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken)).RootElement;

            lock (_models)
            {
                switch (request.RequestUri!.AbsolutePath)
                {
                    case "/api/show":
                        if (!_models.TryGetValue(body.GetProperty("model").GetString()!, out var shown)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                        return Json(new
                        {
                            parameters = shown.NumCtx > 0 ? $"num_ctx                        {shown.NumCtx}\ntemperature 0.7" : "temperature 0.7",
                            model_info = new Dictionary<string, object> { ["general.architecture"] = "qwen3moe", ["qwen3moe.context_length"] = shown.Trained }
                        });
                    case "/api/create":
                        if (RefuseCreate) return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"neither 'from' or 'files' was specified\"}") };
                        var from = body.GetProperty("from").GetString()!;
                        var numCtx = body.GetProperty("parameters").GetProperty("num_ctx").GetInt32();
                        Created.Add((from, numCtx));
                        _models[body.GetProperty("model").GetString()!] = (_models[from].Trained, numCtx);
                        return Json(new { status = "success" });
                    case "/api/tags":
                        return Json(new { models = _models.Keys.Select(x => new { name = x }).ToArray() });
                    case "/api/delete":
                        var name = body.GetProperty("model").GetString()!;
                        Deleted.Add(name);
                        _models.Remove(name);
                        return new HttpResponseMessage(HttpStatusCode.OK);
                    default:
                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                }
            }
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
