using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LoupixDeck.Plugin.VSCode.Bridge;

/// <summary>
/// Constants and helpers of the companion bridge protocol (docs/PROTOCOL.md is the contract): the
/// protocol version, the line limit, where the registration files live and the error codes the
/// client adds of its own.
/// </summary>
internal static class BridgeProtocol
{
    /// <summary>The protocol version this plugin speaks. A server with a higher one is refused.</summary>
    public const int Protocol = 1;

    /// <summary>Lines longer than this (1 MiB) are an error and close the connection.</summary>
    public const int MaxLine = 1024 * 1024;

    /// <summary>Overrides the registration directory on both sides (tests, isolated instances).</summary>
    public const string DirectoryVariable = "LOUPIXDECK_VSCODE_DIR";

    /// <summary>How long a request waits for its reply before it gives up.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Commands can open pickers or run for a while, so <c>exec</c> waits longer.</summary>
    public static readonly TimeSpan ExecTimeout = TimeSpan.FromSeconds(15);

    /// <summary>What the client sends as <c>client</c> in <c>hello</c>, e.g. <c>LoupixDeck.Plugin.VSCode/0.1.0</c>.</summary>
    public static string Client { get; } = "LoupixDeck.Plugin.VSCode/" + PluginVersion();

    /// <summary>
    /// The directory the companion writes its <c>&lt;pid&gt;.json</c> files to:
    /// <c>LOUPIXDECK_VSCODE_DIR</c> when set, else <c>~/.config/LoupixDeck/vscode</c> on macOS and
    /// Linux and <c>%APPDATA%\LoupixDeck\vscode</c> on Windows.
    /// </summary>
    public static string RegistrationDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LoupixDeck", "vscode");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "LoupixDeck", "vscode");
    }

    private static string PluginVersion()
    {
        var v = typeof(BridgeProtocol).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(BridgeProtocol).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        // The SDK appends "+<commit>" to the informational version; the server only wants the number.
        var plus = v.IndexOf('+');
        return plus > 0 ? v[..plus] : v;
    }
}

/// <summary>Error codes: the server's (see PROTOCOL.md) plus the ones the client reports itself.</summary>
internal static class BridgeErrors
{
    public const string BadRequest = "bad_request";
    public const string UnknownOp = "unknown_op";
    public const string UnknownCommand = "unknown_command";
    public const string NotFound = "not_found";
    public const string Failed = "failed";
    public const string Denied = "denied";
    public const string Busy = "busy";

    /// <summary>Client side: no window is connected.</summary>
    public const string Offline = "offline";

    /// <summary>Client side: the window did not answer in time.</summary>
    public const string Timeout = "timeout";

    /// <summary>Client side: the connection closed before the reply arrived.</summary>
    public const string Disconnected = "disconnected";

    /// <summary>Client side: the request was cancelled by the caller.</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>
/// One window's registration file, <c>&lt;pid&gt;.json</c> in the registration directory. Parsing
/// is tolerant: missing fields get defaults, unknown ones are ignored.
/// </summary>
internal sealed record Registration(
    int V,
    int Pid,
    string Socket,
    string Token,
    string App,
    string UriScheme,
    string? AppRoot,
    string? Version,
    string? RemoteName,
    string? WorkspaceName,
    IReadOnlyList<string> Folders,
    string? ExtVersion,
    DateTimeOffset? StartedAt)
{
    /// <summary>Null when the text is not a registration (not JSON, or no pid, socket or token).</summary>
    public static Registration? Parse(string json)
    {
        JsonObject? o;
        try
        {
            o = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (o is null) return null;
        var pid = Json.Int(o, "pid");
        var socket = Json.Str(o, "socket");
        var token = Json.Str(o, "token");
        if (pid is not > 0 || string.IsNullOrEmpty(socket) || string.IsNullOrEmpty(token)) return null;

        return new Registration(
            Json.Int(o, "v") ?? 1,
            pid.Value,
            socket,
            token,
            Json.Str(o, "app") ?? "Visual Studio Code",
            Json.Str(o, "uriScheme") ?? "vscode",
            Json.Str(o, "appRoot"),
            Json.Str(o, "version"),
            Json.Str(o, "remoteName"),
            Json.Str(o, "workspaceName"),
            Json.Strings(o, "folders"),
            Json.Str(o, "extVersion"),
            Json.Time(o, "startedAt"));
    }

    /// <summary>Reads and parses a registration file; null when it cannot be read or parsed.</summary>
    public static Registration? TryRead(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>One entry of a <c>list</c> result (tasks, launch configurations, problems, editors…).</summary>
internal sealed record BridgeItem(
    string Ref,
    string Label,
    string? Detail,
    string? Kind,
    string? Severity,
    bool Dirty,
    bool Active,
    int? Group)
{
    /// <summary>Null when the node is not an object with a ref.</summary>
    public static BridgeItem? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        var reference = Json.Str(o, "ref");
        if (string.IsNullOrEmpty(reference)) return null;
        return new BridgeItem(
            reference,
            Json.Str(o, "label") ?? string.Empty,
            Json.Str(o, "detail"),
            Json.Str(o, "kind"),
            Json.Str(o, "severity"),
            Json.Bool(o, "dirty") ?? false,
            Json.Bool(o, "active") ?? false,
            Json.Int(o, "group"));
    }

    /// <summary>The items of a <c>{"items":[…]}</c> result; empty for anything else.</summary>
    public static IReadOnlyList<BridgeItem> ListFrom(JsonNode? result)
    {
        if (result is not JsonObject o || o["items"] is not JsonArray items) return [];
        var list = new List<BridgeItem>(items.Count);
        foreach (var node in items)
        {
            if (FromJson(node) is { } item) list.Add(item);
        }

        return list;
    }
}

/// <summary>
/// The outcome of a request: <see cref="Ok"/> with the server's <see cref="Result"/>, or an error
/// code (the server's or one of <see cref="BridgeErrors"/>) and message.
/// </summary>
internal sealed record BridgeResult(bool Ok, JsonNode? Result, string? ErrorCode, string? ErrorMessage)
{
    public static BridgeResult Success(JsonNode? result = null) => new(true, result, null, null);

    public static BridgeResult Fail(string code, string? message = null) => new(false, null, code, message);

    /// <summary>Turns a reply line (<c>{"id":n,"ok":…}</c>) into a result.</summary>
    public static BridgeResult FromReply(JsonObject reply)
    {
        if (Json.Bool(reply, "ok") == true) return Success(reply["result"]?.DeepClone());
        var error = reply["error"] as JsonObject;
        return Fail(Json.Str(error, "code") ?? BridgeErrors.Failed, Json.Str(error, "message"));
    }

    /// <summary>True when the command id is not registered in the window (e.g. an extension that is not installed).</summary>
    public bool IsUnknownCommand => ErrorCode == BridgeErrors.UnknownCommand;

    public override string ToString() => Ok ? "ok" : ErrorMessage is { Length: > 0 } m ? $"{ErrorCode}: {m}" : ErrorCode ?? "failed";
}

/// <summary>Builds <c>exec</c> arguments.</summary>
internal static class Args
{
    /// <summary>
    /// A file or folder path as <c>{"$uri":"file:///…"}</c>, which the companion turns into a
    /// <c>vscode.Uri</c> before the call.
    /// </summary>
    public static JsonObject Uri(string path) => new() { ["$uri"] = new System.Uri(Path.GetFullPath(path)).AbsoluteUri };

    /// <summary>An argument array from plain values (strings, numbers, bools, nodes); null items stay null.</summary>
    public static JsonArray Of(params object?[] values)
    {
        var array = new JsonArray();
        foreach (var v in values)
        {
            array.Add(v switch
            {
                null => null,
                JsonNode n => n.Parent is null ? n : n.DeepClone(),
                string s => JsonValue.Create(s),
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                double d => JsonValue.Create(d),
                _ => JsonValue.Create(v.ToString())
            });
        }

        return array;
    }
}

/// <summary>
/// Tolerant readers for <see cref="JsonNode"/> trees: a missing key, a null or a value of the wrong
/// kind gives null instead of throwing, so a newer companion never breaks an older plugin.
/// </summary>
internal static class Json
{
    public static string? Str(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    public static int? Int(JsonObject? o, string key)
    {
        if (Number(o, key) is not { } d) return null;
        if (d >= int.MaxValue) return int.MaxValue;
        if (d <= int.MinValue) return int.MinValue;
        return (int)Math.Round(d);
    }

    public static long? Long(JsonObject? o, string key) => Number(o, key) is { } d ? (long)d : null;

    public static double? Number(JsonObject? o, string key)
    {
        if (o?[key] is not JsonValue v || v.GetValueKind() != JsonValueKind.Number) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        return v.TryGetValue<int>(out var i) ? i : null;
    }

    public static bool? Bool(JsonObject? o, string key) =>
        o?[key] is JsonValue v ? v.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        } : null;

    /// <summary>An epoch-milliseconds number as a time; null when missing or not a number.</summary>
    public static DateTimeOffset? Time(JsonObject? o, string key)
    {
        if (Number(o, key) is not { } ms || ms <= 0 || ms > 253402300799999) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
    }

    /// <summary>The string items of an array; non-strings are skipped, a missing array is empty.</summary>
    public static IReadOnlyList<string> Strings(JsonObject? o, string key)
    {
        if (o?[key] is not JsonArray a) return [];
        var list = new List<string>(a.Count);
        foreach (var n in a)
        {
            if (n is JsonValue v && v.GetValueKind() == JsonValueKind.String) list.Add(v.GetValue<string>());
        }

        return list;
    }
}
