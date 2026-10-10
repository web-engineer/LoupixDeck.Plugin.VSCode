using System.Text.Json;
using System.Text.Json.Nodes;
using LoupixDeck.Plugin.VSCode.Catalog;
using LoupixDeck.Plugin.VSCode.Rendering;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.VSCode.Commands;

/// <summary>
/// Runs any VS Code command by its id, with optional arguments, in the target window. The host
/// splits parameters on commas, so <c>Args</c> cannot contain one: write <c>;</c> wherever the
/// JSON needs a comma and the key turns each <c>;</c> outside a quoted string back into <c>,</c>
/// before parsing (a <c>;</c> inside a string stays a semicolon). A JSON
/// array is the argument list; any other value is the single argument; text that is not JSON is
/// passed as a string. Needs the companion.
/// </summary>
internal sealed class RunCommand(VSCodePlugin plugin) : CodeCommandBase(plugin)
{
    public const string Name = "VSCode.Run";
    private const string Glyph = "\U000F07B7"; // mdi-console-line

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = Name,
        DisplayName = "Run command by ID",
        Group = CommandCatalog.Main,
        Icon = Glyph,
        Description = "Run any VS Code command by its ID, such as editor.action.formatDocument (needs the companion). " +
                      "Args is optional JSON: an array is the argument list, anything else one argument. " +
                      "Write ; wherever the JSON needs a comma, e.g. [\"editor.fontSize\"; 16] or {\"to\":\"down\";\"by\":\"page\"}. " +
                      "A ; inside a quoted string stays a semicolon.",
        ParameterTemplate = "({Command},{Args})",
        Parameters =
        [
            new CommandParameter("Command", typeof(string)),
            new CommandParameter("Args", typeof(string))
        ],
        ButtonLayout = CodeKey.IconAndCaption(Glyph, "Run")
    };

    public override Task Execute(CommandContext ctx) => GuardAsync(ctx, "run command", async () =>
    {
        var command = Parameter(ctx, 0);
        if (command is null)
        {
            Hint(ctx, "Set command");
            return;
        }

        // Anything after the first parameter belongs to Args: the host split it on commas the
        // user typed by mistake, so put them back rather than dropping half the JSON.
        var raw = ctx.Parameters.Length > 1 ? string.Join(",", ctx.Parameters.Skip(1)) : null;
        await RequestAsync(ctx, command, (w, focus) => w.ExecAsync(command, ParseUserArgs(raw), focus)).ConfigureAwait(false);
    });

    /// <summary>
    /// The <c>Args</c> parameter as an argument list: <c>;</c> outside quoted strings becomes <c>,</c>, a JSON array is
    /// taken as the list, any other JSON value as the only argument, and text that is not JSON as
    /// one string argument. Null when blank.
    /// </summary>
    internal static JsonArray? ParseUserArgs(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = SemicolonsToCommas(raw.Trim());
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return new JsonArray(JsonValue.Create(raw.Trim()));
        }

        return node switch
        {
            JsonArray list => list,
            null => new JsonArray((JsonNode?)null),
            _ => new JsonArray(node)
        };
    }

    /// <summary>
    /// Turns each <c>;</c> outside a JSON string literal into <c>,</c>. Semicolons inside quotes
    /// (shell text, CSS, regexes) are kept; a backslash escapes the next character in a string.
    /// </summary>
    internal static string SemicolonsToCommas(string text)
    {
        var chars = text.ToCharArray();
        var inString = false;
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
            }
            else if (c == '"') inString = true;
            else if (c == ';') chars[i] = ',';
        }

        return new string(chars);
    }
}
