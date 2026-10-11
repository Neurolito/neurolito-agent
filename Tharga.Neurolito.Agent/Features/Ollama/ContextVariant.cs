using System.Text.RegularExpressions;

namespace Tharga.Neurolito.Agent.Features.Ollama;

/// <summary>
/// Names of the larger-context copies of installed models that the agent creates for Anthropic requests.
/// </summary>
/// <remarks>
/// A variant changes the model's family, not its tag: <c>qwen3-coder:30b</c> becomes
/// <c>qwen3-coder-neurolito-ctx32768:30b</c>. That is deliberate. Name resolution accepts a bare family and
/// matches it against <c>family:</c> prefixes, so a variant that kept the family - <c>qwen3-coder:30b-ctx</c> -
/// would be picked for an ordinary prompt asking for <c>qwen3-coder</c>. The marker also tells whoever runs
/// <c>ollama list</c> on the machine where the extra entries came from.
/// </remarks>
internal static partial class ContextVariant
{
    private const string Marker = "-neurolito-ctx";

    [GeneratedRegex(@"-neurolito-ctx\d+(?=:|$)", RegexOptions.IgnoreCase)]
    private static partial Regex MarkerPattern();

    /// <summary>The variant of <paramref name="model"/> with a context of <paramref name="contextLength"/> tokens.</summary>
    public static string NameOf(string model, int contextLength)
    {
        var (family, tag) = Split(model);

        return $"{family}{Marker}{contextLength}:{tag}";
    }

    /// <summary>Whether <paramref name="model"/> is a variant the agent created, of any context length.</summary>
    public static bool IsVariant(string model)
    {
        return !string.IsNullOrEmpty(model) && MarkerPattern().IsMatch(model);
    }

    /// <summary>The installed model a variant was made from, with the same tag.</summary>
    public static string BaseOf(string variant)
    {
        return MarkerPattern().Replace(variant, string.Empty);
    }

    /// <summary>The model's name as Ollama lists it, with <c>latest</c> written out when no tag was given.</summary>
    public static string Normalize(string model)
    {
        var (family, tag) = Split(model);

        return $"{family}:{tag}";
    }

    /// <summary>
    /// Family and tag. A name without a tag means <c>latest</c>, as Ollama reads it. The last colon separates
    /// them, because a registry host can carry a port (<c>host:5000/model:tag</c>).
    /// </summary>
    private static (string Family, string Tag) Split(string model)
    {
        var slash = model.LastIndexOf('/');
        var colon = model.LastIndexOf(':');

        return colon > slash ? (model[..colon], model[(colon + 1)..]) : (model, "latest");
    }
}
