using System.Text.Json;
using OllamaSharp.Models;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.Model;

/// <summary>What the agent tells the server about one installed model.</summary>
internal static class ModelReport
{
    public static LLModel Build(OllamaSharp.Models.Model installed, RunningModel running, ShowModelResponse info)
    {
        return new LLModel
        {
            Name = installed.Name,
            Loaded = running != null,
            Details = new ModelDetails
            {
                ParameterSize = installed.Details.ParameterSize,
                ParameterCount = info?.Info?.ParameterCount,
                ActiveParameterCount = ActiveParameters(info?.Info),
                SizeBytes = installed.Size > 0 ? installed.Size : null,
                LoadedSizeBytes = running?.Size > 0 ? running.Size : null,
                LoadedVramBytes = running == null ? null : running.SizeVram
            }
        };
    }

    /// <summary>
    /// The parameters one token runs through. The total for a dense model; for a mixture-of-experts model the
    /// total less the routed experts a token does not use. Null when the model reports no parameter count.
    /// </summary>
    /// <remarks>
    /// Each routed expert is a gated feed-forward block of three matrices, embedding × expert width, in every
    /// layer. Shared experts are always used, so they stay counted. qwen3-coder:30b: 30.5B total, 3.35B active.
    /// </remarks>
    public static long? ActiveParameters(ModelInfo info)
    {
        var total = info?.ParameterCount;
        if (total is not > 0) return null;

        var extra = info.ExtraInfo;
        var architecture = info.Architecture;
        if (extra == null || string.IsNullOrEmpty(architecture)) return total;

        long? Read(string key) => extra.TryGetValue($"{architecture}.{key}", out var value) ? AsLong(value) : null;

        var experts = Read("expert_count");
        var used = Read("expert_used_count");
        if (experts is not > 0 || used is not > 0 || used >= experts) return total;

        var blocks = Read("block_count");
        var embedding = Read("embedding_length");
        var width = Read("expert_feed_forward_length") is > 0 and var expertWidth ? expertWidth : Read("feed_forward_length");
        if (blocks is not > 0 || embedding is not > 0 || width is not > 0) return total;

        var unused = blocks.Value * 3 * embedding.Value * width.Value * (experts.Value - used.Value);
        var active = total.Value - unused;
        return active > 0 ? active : total;
    }

    private static long? AsLong(object value)
    {
        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetInt64(out var l) => l,
            JsonElement => null,
            IConvertible c => TryConvert(c),
            _ => null
        };

        static long? TryConvert(IConvertible c)
        {
            try { return c.ToInt64(System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException) { return null; }
        }
    }
}
