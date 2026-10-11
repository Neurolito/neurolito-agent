using Tharga.Neurolito.Agent.Features.Ollama;

namespace Tharga.Neurolito.Agent.Tests;

/// <summary>
/// Names of the larger-context copies the agent makes for Anthropic requests.
/// </summary>
public class ContextVariantTests
{
    [Theory]
    [InlineData("qwen3-coder:30b", 32768, "qwen3-coder-neurolito-ctx32768:30b")]
    [InlineData("llama3.2", 16384, "llama3.2-neurolito-ctx16384:latest")]
    [InlineData("hf.co/someone/model:Q4_K_M", 32768, "hf.co/someone/model-neurolito-ctx32768:Q4_K_M")]
    [InlineData("registry:5000/team/model", 8192, "registry:5000/team/model-neurolito-ctx8192:latest")]
    public void AVariantKeepsTheTagAndMarksTheFamily(string model, int contextLength, string expected)
    {
        Assert.Equal(expected, ContextVariant.NameOf(model, contextLength));
    }

    [Theory]
    [InlineData("qwen3-coder-neurolito-ctx32768:30b", true)]
    [InlineData("llama3.2-neurolito-ctx4096:latest", true)]
    [InlineData("qwen3-coder:30b", false)]
    [InlineData("neurolito-ctx-notes:latest", false)]
    [InlineData(null, false)]
    public void AVariantIsRecognised(string model, bool expected)
    {
        Assert.Equal(expected, ContextVariant.IsVariant(model));
    }

    [Fact]
    public void AVariantNamesItsBaseModel()
    {
        Assert.Equal("qwen3-coder:30b", ContextVariant.BaseOf(ContextVariant.NameOf("qwen3-coder:30b", 32768)));
    }

    [Fact]
    public void AVariantIsNeverAPrefixMatchForItsFamily()
    {
        // Name resolution matches a bare family against "family:" prefixes. A variant must not be found that way,
        // or an ordinary prompt for "qwen3-coder" could be run on it.
        Assert.False(ContextVariant.NameOf("qwen3-coder:30b", 32768).StartsWith("qwen3-coder:", StringComparison.OrdinalIgnoreCase));
    }
}
