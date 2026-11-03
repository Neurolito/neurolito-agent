using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tharga.Neurolito.Client.Contract;

public record Input
{
    private readonly Message[] _rows;
    private readonly string _model;
    private readonly string _prompt;
    private readonly string[] _fallbackModels;
    private readonly TimeSpan? _fallbackAfter;

    internal Input(string prompt, string model)
    {
        _prompt = prompt;
        _model = model;
    }

    internal Input(IEnumerable<Message> rows, string model, string[] fallbackModels = null, TimeSpan? fallbackAfter = null)
    {
        _rows = rows.ToArray();
        _model = model;
        _fallbackModels = fallbackModels;
        _fallbackAfter = fallbackAfter;
    }

    public string Model => _model;

    /// <summary>Models to fall back on, in order, when no machine takes <see cref="Model"/> in time.</summary>
    public string[] FallbackModels => _fallbackModels;

    /// <summary>How long each entry has before the next may be used. Null is the server's default, 30 seconds.</summary>
    public TimeSpan? FallbackAfter => _fallbackAfter;

    public static implicit operator string(Input input)
    {
        if (!string.IsNullOrEmpty(input._prompt))
        {
            if (input._rows?.Any() ?? false) throw new InvalidOperationException("The builder have both a basic prompt and rows. There can be only one.");
            return input._prompt;
        }

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

        var value = JsonSerializer.Serialize(input._rows, options);
        return value;
    }

    public static implicit operator Input(InputDto input)
    {
        return new Input(input.Prompt, input.Model);
    }

    public static implicit operator Message[](Input input)
    {
        if (input._rows == null && !string.IsNullOrEmpty(input._prompt))
        {
            return [new Message { Role = Role.User, Content = input._prompt }];
        }

        return input._rows?.Select(x => new Message { Content = x.Content, Role = x.Role }).ToArray() ?? [];
    }
}