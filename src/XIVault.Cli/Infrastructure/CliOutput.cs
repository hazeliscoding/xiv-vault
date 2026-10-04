using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace XIVault.Cli.Infrastructure;

/// <summary>
/// All CLI output goes through here: normal output to stdout, problems to stderr, and JSON as
/// raw text so it is never wrapped or styled.
/// </summary>
internal sealed class CliOutput(IAnsiConsole output, IAnsiConsole error)
{
    public const string Accent = "#6C9EFF";
    public const string Healthy = "#3CD5DE";
    public const string Warn = "#F2B441";
    public const string Critical = "#F0555D";
    public const string Dim = "grey";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public IAnsiConsole Out { get; } = output;

    public IAnsiConsole Err { get; } = error;

    /// <summary>Set by --quiet: only errors are written.</summary>
    public bool Quiet { get; set; }

    public bool Interactive => Out.Profile.Capabilities.Interactive && !Console.IsInputRedirected;

    public void Markup(string markup)
    {
        if (!Quiet)
        {
            Out.MarkupLine(markup);
        }
    }

    public void Line(string text = "") => Markup(Spectre.Console.Markup.Escape(text));

    public void Success(string text) => Markup($"[{Healthy}]✓[/] {Spectre.Console.Markup.Escape(text)}");

    public void Detail(string text) => Markup($"  [{Dim}]{Spectre.Console.Markup.Escape(text)}[/]");

    public void Warning(string text) => Err.MarkupLine($"[{Warn}]![/] {Spectre.Console.Markup.Escape(text)}");

    public void Error(string text) => Err.MarkupLine($"[{Critical}]✕[/] {Spectre.Console.Markup.Escape(text)}");

    public void Write(Spectre.Console.Rendering.IRenderable renderable)
    {
        if (!Quiet)
        {
            Out.Write(renderable);
        }
    }

    public void Json<T>(T value) => Out.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    /// <summary>Runs work behind a spinner when a person is watching; otherwise just runs it.</summary>
    public async Task<T> WithStatusAsync<T>(string initial, Func<Action<string>, Task<T>> work)
    {
        if (Quiet || !Out.Profile.Capabilities.Interactive)
        {
            return await work(_ => { }).ConfigureAwait(false);
        }

        return await Out.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse(Accent))
            .StartAsync(Spectre.Console.Markup.Escape(initial), context => work(message => context.Status(Spectre.Console.Markup.Escape(message))))
            .ConfigureAwait(false);
    }
}
