using System.Text;

namespace Tharga.Neurolito.Agent.Features.LlamaServer;

/// <summary>What the agent needs from a GGUF file's header.</summary>
internal sealed record GgufInfo
{
    /// <summary>For example <c>qwen3</c>, <c>llama</c>, <c>qwen3moe</c>: the prefix of the model's own metadata keys.</summary>
    public string Architecture { get; init; }

    /// <summary>The publisher's label, for example "1.7B". Null when the file has none.</summary>
    public string SizeLabel { get; init; }

    /// <summary>
    /// The model's weights, counted as Ollama counts them: every tensor, and the token embedding a second time when the
    /// model uses it as its output layer too (tied embeddings, as in Qwen3 and Llama 3.2). Work units are tokens times
    /// this, so the same job earns the same on either engine.
    /// </summary>
    public long ParameterCount { get; init; }

    /// <summary>The context the model was trained for.</summary>
    public long? ContextLength { get; init; }

    /// <summary>The chat template the file carries, which llama-server renders with <c>--jinja</c>.</summary>
    public string ChatTemplate { get; init; }

    /// <summary>The numeric, text and boolean metadata. Arrays (the tokenizer's vocabulary) are skipped.</summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; }

    /// <summary>A number from the model's own metadata, <c>{architecture}.{key}</c>.</summary>
    public long? Read(string key)
    {
        return Metadata.TryGetValue($"{Architecture}.{key}", out var value) ? value switch
        {
            long l => l,
            double d => (long)d,
            _ => null
        } : null;
    }

    /// <summary>Whether the model can think: its template has a thinking switch or a thinking block.</summary>
    public bool SupportsThinking => ChatTemplate != null
                                    && (ChatTemplate.Contains("enable_thinking", StringComparison.Ordinal)
                                        || ChatTemplate.Contains("<think>", StringComparison.Ordinal));
}

/// <summary>Reads the header of a GGUF file: its metadata and its tensor shapes, never the weights.</summary>
/// <remarks>The format: https://github.com/ggml-org/ggml/blob/master/docs/gguf.md. Little-endian throughout.</remarks>
internal static class GgufReader
{
    private const uint Magic = 0x46554747; // "GGUF"
    private const ulong MaxCount = 10_000_000;

    public static GgufInfo Read(string path)
    {
        using var stream = new BufferedStream(File.OpenRead(path), 1 << 20);
        return Read(stream);
    }

    public static GgufInfo Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        if (reader.ReadUInt32() != Magic) throw new InvalidDataException("Not a GGUF file.");
        var version = reader.ReadUInt32();
        if (version is < 2 or > 3) throw new InvalidDataException($"GGUF version {version} is not supported.");

        var tensorCount = Count(reader.ReadUInt64());
        var metadataCount = Count(reader.ReadUInt64());

        var metadata = new Dictionary<string, object>(StringComparer.Ordinal);
        for (ulong i = 0; i < metadataCount; i++)
        {
            var key = ReadString(reader);
            var value = ReadValue(reader, (GgufType)reader.ReadUInt32());
            if (value != null) metadata[key] = value;
        }

        long parameters = 0;
        long embedding = 0;
        var hasOutput = false;
        for (ulong i = 0; i < tensorCount; i++)
        {
            var name = ReadString(reader);
            var dimensions = reader.ReadUInt32();
            if (dimensions > 8) throw new InvalidDataException($"A tensor with {dimensions} dimensions.");
            long elements = 1;
            for (var d = 0; d < dimensions; d++) elements *= (long)reader.ReadUInt64();
            reader.ReadUInt32(); // type
            reader.ReadUInt64(); // offset
            parameters += elements;

            if (name == "token_embd.weight") embedding = elements;
            if (name == "output.weight") hasOutput = true;
        }

        //NOTE: 1,720,574,976 tensors in qwen3:1.7b; Ollama reports 2,031,739,904, the 311,164,928 more being the tied embedding.
        if (!hasOutput) parameters += embedding;

        var architecture = metadata.GetValueOrDefault("general.architecture") as string;
        var info = new GgufInfo
        {
            Architecture = architecture,
            SizeLabel = metadata.GetValueOrDefault("general.size_label") as string,
            ParameterCount = parameters,
            ChatTemplate = metadata.GetValueOrDefault("tokenizer.chat_template") as string,
            Metadata = metadata
        };

        return info with { ContextLength = info.Read("context_length") };
    }

    private static ulong Count(ulong value) => value <= MaxCount ? value : throw new InvalidDataException($"A count of {value} in the header.");

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadUInt64();
        if (length > int.MaxValue) throw new InvalidDataException($"A string of {length} bytes in the header.");
        return Encoding.UTF8.GetString(reader.ReadBytes((int)length));
    }

    private static object ReadValue(BinaryReader reader, GgufType type)
    {
        switch (type)
        {
            case GgufType.UInt8: return (long)reader.ReadByte();
            case GgufType.Int8: return (long)reader.ReadSByte();
            case GgufType.UInt16: return (long)reader.ReadUInt16();
            case GgufType.Int16: return (long)reader.ReadInt16();
            case GgufType.UInt32: return (long)reader.ReadUInt32();
            case GgufType.Int32: return (long)reader.ReadInt32();
            case GgufType.Float32: return (double)reader.ReadSingle();
            case GgufType.Bool: return reader.ReadByte() != 0;
            case GgufType.String: return ReadString(reader);
            case GgufType.UInt64: return (long)reader.ReadUInt64();
            case GgufType.Int64: return reader.ReadInt64();
            case GgufType.Float64: return reader.ReadDouble();
            case GgufType.Array:
                SkipArray(reader);
                return null;
            default: throw new InvalidDataException($"Unknown GGUF value type {(uint)type}.");
        }
    }

    private static void SkipArray(BinaryReader reader)
    {
        var type = (GgufType)reader.ReadUInt32();
        var count = Count(reader.ReadUInt64());
        var size = type switch
        {
            GgufType.UInt8 or GgufType.Int8 or GgufType.Bool => 1,
            GgufType.UInt16 or GgufType.Int16 => 2,
            GgufType.UInt32 or GgufType.Int32 or GgufType.Float32 => 4,
            GgufType.UInt64 or GgufType.Int64 or GgufType.Float64 => 8,
            _ => 0
        };

        if (size > 0)
        {
            reader.BaseStream.Seek((long)count * size, SeekOrigin.Current);
            return;
        }

        for (ulong i = 0; i < count; i++) ReadValue(reader, type);
    }

    private enum GgufType : uint
    {
        UInt8 = 0, Int8 = 1, UInt16 = 2, Int16 = 3, UInt32 = 4, Int32 = 5, Float32 = 6, Bool = 7, String = 8, Array = 9, UInt64 = 10, Int64 = 11, Float64 = 12
    }
}
