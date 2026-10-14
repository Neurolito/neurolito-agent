using System.Text;
using Tharga.Neurolito.Agent.Features.Engine;
using Tharga.Neurolito.Agent.Features.LlamaServer;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Tests;

/// <summary>The llama-server engine's pure parts: reading a GGUF header, naming, the request it sends, the model store.</summary>
public class LlamaServerEngineTests
{
    [Theory]
    [InlineData("Qwen3-1.7B-Q4_K_M.gguf", "Q4_K_M")]
    [InlineData("Qwen_Qwen3-1.7B-Q8_0.gguf", "Q8_0")]
    [InlineData("gemma-3-4b-it-IQ4_XS.gguf", "IQ4_XS")]
    [InlineData("Qwen3-1.7B-UD-Q4_K_XL.gguf", "UD-Q4_K_XL")]
    [InlineData("model-f16.gguf", "F16")]
    [InlineData("Qwen3-1.7B.gguf", null)]
    [InlineData(null, null)]
    public void TheQuantisationIsReadFromTheFileName(string fileName, string expected)
    {
        Assert.Equal(expected, ModelStore.QuantizationOf(fileName));
    }

    [Theory]
    [InlineData("Meta-Llama-3.1-8B-Instruct-Q6_K.gguf", "Q6_K", true)]
    [InlineData("Meta-Llama-3.1-8B-Instruct-Q6_K_L.gguf", "Q6_K", false)]
    [InlineData("qwen2.5-coder-7b-instruct-q4_k_m.gguf", "Q4_K_M", true)]
    public void AFileIsNamedForAQuantisationOnlyAsAWholePart(string file, string quantisation, bool expected)
    {
        Assert.Equal(expected, HuggingFaceClient.NamedFor(file, quantisation));
    }

    [Fact]
    public void GpuLayersAreLeftToLlamaServerUnlessConfigured()
    {
        //NOTE: llama-server fits only the arguments it was not given; an -ngl of ours stops a large model being split.
        var arguments = LlamaServerHost.Arguments(new LlamaServerOptions(), "qwen3-coder:30b", "m.gguf", 32768, 5000).ToArray();

        Assert.DoesNotContain("-ngl", arguments);
        Assert.Equal(["-m", "m.gguf", "--alias", "qwen3-coder:30b", "--jinja", "-c", "32768", "--parallel", "1", "--host", "127.0.0.1", "--port", "5000"], arguments);
    }

    [Fact]
    public void ConfiguredGpuLayersAndExtraArgumentsArePassedOn()
    {
        var arguments = LlamaServerHost.Arguments(new LlamaServerOptions { GpuLayers = 20, ExtraArguments = "--flash-attn on" }, "m", "m.gguf", 8192, 5000).ToArray();

        Assert.Equal(["-m", "m.gguf", "--alias", "m", "--jinja", "-ngl", "20", "-c", "8192", "--parallel", "1", "--host", "127.0.0.1", "--port", "5000", "--flash-attn", "on"], arguments);
    }

    [Fact]
    public void TheHeaderGivesTheParameterCountTheContextAndWhetherTheModelThinks()
    {
        var gguf = Gguf(
            [
                ("general.architecture", "qwen3"),
                ("general.size_label", "1.7B"),
                ("qwen3.context_length", 40960u),
                ("qwen3.block_count", 28u),
                ("tokenizer.chat_template", "{%- if enable_thinking is defined and enable_thinking is false %}<think>\n\n</think>{%- endif %}")
            ],
            [("token_embd.weight", [2048, 151936]), ("blk.0.attn_q.weight", [2048, 2048]), ("output_norm.weight", [2048])]);

        var info = GgufReader.Read(new MemoryStream(gguf));

        Assert.Equal("qwen3", info.Architecture);
        Assert.Equal("1.7B", info.SizeLabel);
        //NOTE: No output.weight, so the embedding is counted again as the output layer, as Ollama counts it.
        Assert.Equal(2 * 2048L * 151936 + 2048L * 2048 + 2048, info.ParameterCount);
        Assert.Equal(40960, info.ContextLength);
        Assert.Equal(28, info.Read("block_count"));
        Assert.True(info.SupportsThinking);
    }

    [Fact]
    public void TheVocabularyArrayIsSkippedAndAnUntiedOutputIsCountedOnce()
    {
        var gguf = Gguf([("general.architecture", "llama"), ("tokenizer.ggml.tokens", new[] { "<s>", "</s>", "hello" }), ("llama.context_length", 131072u)],
            [("token_embd.weight", [4, 3]), ("output.weight", [4, 3]), ("w", [4, 4])]);

        var info = GgufReader.Read(new MemoryStream(gguf));

        Assert.False(info.Metadata.ContainsKey("tokenizer.ggml.tokens"));
        Assert.Equal(131072, info.ContextLength);
        Assert.Equal(12 + 12 + 16, info.ParameterCount);
        Assert.False(info.SupportsThinking);
    }

    [Fact]
    public void AFileThatIsNotGgufIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => GgufReader.Read(new MemoryStream(Encoding.ASCII.GetBytes("PK\u0003\u0004 not a model"))));
    }

    [Theory]
    [InlineData("version: 0.6.0-dev (build 11541, commit f2918cabb)\nbuilt with Clang", "llama.cpp b11541")]
    [InlineData("version: 4589 (abc1234)", "llama.cpp b4589")]
    [InlineData("", "llama.cpp")]
    public void TheVersionIsTheBuildNumberReleasesAreNamedBy(string output, string version)
    {
        Assert.Equal(version, LlamaServerHost.VersionOf(output));
    }

    [Theory]
    [InlineData("hf.co/unsloth/Qwen3-1.7B-GGUF:Q4_K_M", "unsloth/Qwen3-1.7B-GGUF", "Q4_K_M")]
    [InlineData("hf.co/bartowski/Llama-3.2-1B-Instruct-GGUF", "bartowski/Llama-3.2-1B-Instruct-GGUF", "Q4_K_M")]
    [InlineData("https://huggingface.co/unsloth/Qwen3-1.7B-GGUF:Q8_0", "unsloth/Qwen3-1.7B-GGUF", "Q8_0")]
    public void AHuggingFaceNameGivesTheRepositoryAndTheQuantisation(string name, string repository, string quantisation)
    {
        Assert.True(HuggingFaceClient.TryParse(name, out var r, out var q));
        Assert.Equal((repository, quantisation), (r, q));
    }

    [Theory]
    [InlineData("qwen3:1.7b")]
    [InlineData("llama3.1:8b-instruct-q4_K_M")]
    [InlineData("hf.co/onlyowner")]
    public void AnOllamaNameIsNotAHuggingFaceName(string name)
    {
        Assert.False(HuggingFaceClient.TryParse(name, out _, out _));
    }

    [Fact]
    public void ThePromptsOptionsWinAndTheModelsDefaultsFillTheRest()
    {
        var model = Installed(new SamplingDefaults { Temperature = 0.6f, TopK = 20, TopP = 0.95f, Stop = ["<|im_end|>"] }, supportsThinking: true);
        var request = new EngineChatRequest
        {
            Model = model.Name,
            Messages = [new Message { Role = Role.System, Content = "Be brief." }, new Message { Role = Role.User, Content = "Hi" }],
            Options = new Options { Temperature = 0, NumPredict = 60 },
            Think = false
        };

        var body = LlamaServerEngine.Body(request, model);

        Assert.Equal("system", (string)body["messages"]![0]!["role"]);
        Assert.Equal(0f, (float)body["temperature"]!);
        Assert.Equal(20, (int)body["top_k"]!);
        Assert.Equal(0.95f, (float)body["top_p"]!);
        Assert.Equal(60, (int)body["max_tokens"]!);
        Assert.Equal("<|im_end|>", (string)body["stop"]![0]);
        Assert.False((bool)body["chat_template_kwargs"]!["enable_thinking"]!);
        Assert.True((bool)body["stream_options"]!["include_usage"]!);
    }

    [Fact]
    public void AModelThatCannotThinkIsNotToldAboutThinkingAndUnlimitedOutputSetsNoLimit()
    {
        var body = LlamaServerEngine.Body(new EngineChatRequest
        {
            Model = "llama3.2:1b",
            Messages = [new Message { Role = Role.User, Content = "Hi" }],
            Options = new Options { NumPredict = -1 },
            Think = true
        }, Installed(null, supportsThinking: false));

        Assert.False(body.ContainsKey("chat_template_kwargs"));
        Assert.False(body.ContainsKey("max_tokens"));
        Assert.False(body.ContainsKey("temperature"));
    }

    [Theory]
    [InlineData(2_031_739_904L, "2B")]
    [InlineData(8_030_261_312L, "8B")]
    [InlineData(1_235_814_400L, "1.2B")]
    [InlineData(494_032_768L, "494M")]
    public void ASizeLabelIsWrittenAsOllamaWritesOne(long parameters, string label)
    {
        Assert.Equal(label, LlamaServerEngine.SizeLabel(parameters));
    }

    [Fact]
    public void TheStoreFindsAModelByItsNameWithOrWithoutLatest()
    {
        var root = Path.Combine(Path.GetTempPath(), "neurolito-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ModelStore(root);
            var path = store.FilePathFor("tinyllama");
            File.WriteAllBytes(path, [1, 2, 3]);
            store.Save(Installed(null, false) with { Name = "tinyllama", FileName = Path.GetFileName(path) });

            Assert.Equal("tinyllama", store.Find("tinyllama:latest")?.Name);
            Assert.Equal("qwen3_1.7b", ModelStore.Stem("qwen3:1.7b"));

            store.Remove(store.Find("tinyllama"));
            Assert.Empty(store.List());
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static InstalledModel Installed(SamplingDefaults defaults, bool supportsThinking) => new()
    {
        Name = "qwen3:1.7b",
        Source = new ModelSource { Repository = "unsloth/Qwen3-1.7B-GGUF", File = "Qwen3-1.7B-Q4_K_M.gguf", Defaults = defaults },
        FileName = "qwen3_1.7b.gguf",
        SupportsThinking = supportsThinking
    };

    /// <summary>A GGUF v3 header with the given metadata and tensor shapes, and no weights.</summary>
    private static byte[] Gguf((string Key, object Value)[] metadata, (string Name, long[] Dimensions)[] tensors)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, Encoding.UTF8);

        void Text(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            w.Write((ulong)bytes.Length);
            w.Write(bytes);
        }

        w.Write(0x46554747u);
        w.Write(3u);
        w.Write((ulong)tensors.Length);
        w.Write((ulong)metadata.Length);
        foreach (var (key, value) in metadata)
        {
            Text(key);
            switch (value)
            {
                case string s:
                    w.Write(8u);
                    Text(s);
                    break;
                case uint u:
                    w.Write(4u);
                    w.Write(u);
                    break;
                case string[] array:
                    w.Write(9u);
                    w.Write(8u);
                    w.Write((ulong)array.Length);
                    foreach (var s in array) Text(s);
                    break;
                default:
                    throw new ArgumentException(value.GetType().Name);
            }
        }

        foreach (var (name, dimensions) in tensors)
        {
            Text(name);
            w.Write((uint)dimensions.Length);
            foreach (var d in dimensions) w.Write((ulong)d);
            w.Write(12u);
            w.Write(0ul);
        }

        w.Flush();
        return stream.ToArray();
    }
}
