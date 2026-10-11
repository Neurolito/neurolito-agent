using System.Text.Json;
using System.Text.RegularExpressions;
using Tharga.Neurolito.Client.Contract;

namespace Tharga.Neurolito.Agent.Features.LlamaServer;

/// <summary>A model installed for llama-server: its GGUF file, where it came from, and what its header says.</summary>
internal sealed record InstalledModel
{
    /// <summary>The name the server knows it by, for example <c>qwen3:1.7b</c>.</summary>
    public required string Name { get; init; }

    public required ModelSource Source { get; init; }

    /// <summary>The GGUF file, in the store's folder.</summary>
    public required string FileName { get; init; }

    public DateTime InstalledUtc { get; init; }
    public long SizeBytes { get; init; }
    public long ParameterCount { get; init; }
    public long? ActiveParameterCount { get; init; }
    public string ParameterSize { get; init; }

    /// <summary>The context the model was trained for.</summary>
    public long? ContextLength { get; init; }

    public bool SupportsThinking { get; init; }
}

/// <summary>
/// The models installed for llama-server: one GGUF file and one JSON description per model, in one folder. The
/// description is written last, so a model is installed exactly when its description exists.
/// </summary>
internal sealed partial class ModelStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Lock _lock = new();

    public ModelStore(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
    }

    public string Root { get; }

    public InstalledModel[] List()
    {
        lock (_lock)
        {
            return Directory.EnumerateFiles(Root, "*.json")
                .Select(TryLoad)
                .Where(x => x != null && File.Exists(PathOf(x)))
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    /// <summary>The model by its name; "name" and "name:latest" are the same model, as in Ollama.</summary>
    public InstalledModel Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return List().FirstOrDefault(x => string.Equals(Bare(x.Name), Bare(name), StringComparison.OrdinalIgnoreCase));
    }

    public string PathOf(InstalledModel model) => Path.Combine(Root, model.FileName);

    /// <summary>Where a model's GGUF file goes while and after it is downloaded.</summary>
    public string FilePathFor(string name) => Path.Combine(Root, Stem(name) + ".gguf");

    public void Save(InstalledModel model)
    {
        lock (_lock)
        {
            var path = DescriptionPathFor(model.Name);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(model, Json));
            File.Move(path + ".tmp", path, true);
        }
    }

    public void Remove(InstalledModel model)
    {
        lock (_lock)
        {
            //NOTE: The description first, so a file left behind by a failed delete is never taken for an installed model.
            File.Delete(DescriptionPathFor(model.Name));
            File.Delete(PathOf(model));
        }
    }

    private string DescriptionPathFor(string name) => Path.Combine(Root, Stem(name) + ".json");

    private static InstalledModel TryLoad(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<InstalledModel>(File.ReadAllText(path));
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>A file name for a model name: <c>qwen3:1.7b</c> becomes <c>qwen3_1.7b</c>.</summary>
    internal static string Stem(string name) => UnsafeCharacters().Replace(Bare(name).ToLowerInvariant(), "_");

    private static string Bare(string name) => name.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? name[..^":latest".Length] : name;

    [GeneratedRegex(@"[^a-z0-9.\-]")]
    private static partial Regex UnsafeCharacters();
}
