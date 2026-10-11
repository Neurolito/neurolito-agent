using System.Text.Json;
using OllamaSharp.Models;
using Tharga.Neurolito.Agent.Features.Model;

namespace Tharga.Neurolito.Agent.Tests;

/// <summary>What an agent reports about an installed model.</summary>
public class ModelReportTests
{
    //NOTE: model_info as Ollama returns it from /api/show, trimmed to the keys that matter.
    private const string QwenCoder = """
        {
          "general.architecture": "qwen3moe",
          "general.parameter_count": 30532122624,
          "qwen3moe.block_count": 48,
          "qwen3moe.embedding_length": 2048,
          "qwen3moe.expert_count": 128,
          "qwen3moe.expert_feed_forward_length": 768,
          "qwen3moe.expert_shared_feed_forward_length": 0,
          "qwen3moe.expert_used_count": 8,
          "qwen3moe.feed_forward_length": 5472
        }
        """;

    private const string Llama = """
        {
          "general.architecture": "llama",
          "general.parameter_count": 8030261312,
          "llama.block_count": 32,
          "llama.embedding_length": 4096,
          "llama.feed_forward_length": 14336
        }
        """;

    [Fact]
    public void AMixtureOfExpertsModelReportsTheParametersATokenRunsThrough()
    {
        //30.53 B in all; each token uses 8 of 128 experts, which leaves 3.35 B (published: 3.3 B).
        Assert.Equal(3353032704L, ModelReport.ActiveParameters(Info(QwenCoder)));
    }

    [Fact]
    public void ADenseModelReportsItsTotal()
    {
        Assert.Equal(8030261312L, ModelReport.ActiveParameters(Info(Llama)));
    }

    [Fact]
    public void WithoutTheExpertWidthTheFeedForwardWidthIsUsed()
    {
        //Mixtral-style metadata: the experts' width is the model's feed-forward width.
        var info = Info("""
            {
              "general.architecture": "llama",
              "general.parameter_count": 46702792704,
              "llama.block_count": 32,
              "llama.embedding_length": 4096,
              "llama.expert_count": 8,
              "llama.expert_used_count": 2,
              "llama.feed_forward_length": 14336
            }
            """);

        //46.70 B − 32 × 3 × 4096 × 14336 × 6 = 12.88 B (published: 12.9 B).
        Assert.Equal(12879925248L, ModelReport.ActiveParameters(info));
    }

    [Fact]
    public void AModelWithoutAParameterCountReportsNothing()
    {
        Assert.Null(ModelReport.ActiveParameters(Info("""{ "general.architecture": "llama" }""")));
        Assert.Null(ModelReport.ActiveParameters(null));
    }

    [Fact]
    public void AnExpertCountThatMakesNoSenseFallsBackToTheTotal()
    {
        var info = Info("""
            {
              "general.architecture": "x",
              "general.parameter_count": 1000,
              "x.block_count": 48,
              "x.embedding_length": 2048,
              "x.expert_count": 128,
              "x.expert_feed_forward_length": 768,
              "x.expert_used_count": 8
            }
            """);

        Assert.Equal(1000L, ModelReport.ActiveParameters(info));
    }

    [Fact]
    public void ALoadedModelReportsHowMuchOfItIsInVideoMemory()
    {
        var installed = new OllamaSharp.Models.Model { Name = "llama3.3:70b", Size = 42_520_000_000, Details = new Details { ParameterSize = "70.6B" } };
        var running = new RunningModel { Name = "llama3.3:70b", Size = 47_000_000_000, SizeVram = 7_000_000_000 };

        var details = ModelReport.Build(installed, running, new ShowModelResponse { Info = Info(Llama) }).Details;

        Assert.Equal(42_520_000_000, details.SizeBytes);
        Assert.Equal(47_000_000_000, details.LoadedSizeBytes);
        Assert.Equal(7_000_000_000, details.LoadedVramBytes);
    }

    [Fact]
    public void AModelThatIsNotLoadedReportsOnlyItsSize()
    {
        var installed = new OllamaSharp.Models.Model { Name = "llama3.1:8b", Size = 4_920_753_328, Details = new Details { ParameterSize = "8.0B" } };

        var model = ModelReport.Build(installed, null, new ShowModelResponse { Info = Info(Llama) });

        Assert.False(model.Loaded);
        Assert.Equal(4_920_753_328, model.Details.SizeBytes);
        Assert.Null(model.Details.LoadedSizeBytes);
        Assert.Null(model.Details.LoadedVramBytes);
    }

    private static ModelInfo Info(string json) => JsonSerializer.Deserialize<ModelInfo>(json);
}
