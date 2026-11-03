namespace Tharga.Neurolito.Client.Contract;

public class InputBuilder
{
    private readonly List<Message> _rows = new();
    private string _model;
    private readonly List<string> _fallbackModels = new();
    private TimeSpan? _fallbackAfter;

    public Input Build()
    {
        return new Input(_rows, _model, _fallbackModels.Count == 0 ? null : _fallbackModels.ToArray(), _fallbackAfter);
    }

    public InputBuilder System(string content)
    {
        _rows.Add(new Message { Role = Role.System, Content = content });
        return this;
    }

    public InputBuilder Developer(string content)
    {
        _rows.Add(new Message { Role = Role.User, Content = content });
        return this;
    }

    public InputBuilder User(string content)
    {
        _rows.Add(new Message { Role = Role.User, Content = content });
        return this;
    }

    public InputBuilder Model(string model)
    {
        _model = model;
        return this;
    }

    /// <summary>
    /// Models to fall back on, in order, when no machine takes the model in time. Called again, it adds to the end.
    /// A hosted model is used only when the team has turned on hosted models.
    /// </summary>
    public InputBuilder Fallback(params string[] models)
    {
        _fallbackModels.AddRange((models ?? []).Where(x => !string.IsNullOrWhiteSpace(x)));
        return this;
    }

    /// <summary>How long each entry has before the next may be used; the server's default is 30 seconds.</summary>
    public InputBuilder FallbackAfter(TimeSpan wait)
    {
        _fallbackAfter = wait;
        return this;
    }

    public InputBuilder Rows(IEnumerable<Message> rows)
    {
        _rows.AddRange(rows);
        return this;
    }
}