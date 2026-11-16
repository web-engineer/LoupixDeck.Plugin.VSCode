using System.Collections.Concurrent;
using System.Text.Json;
using LoupixDeck.Plugin.VSCode.Rendering;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.VSCode.Bridge;

/// <summary>
/// Reads a window's Peacock colour straight from its folder's <c>.vscode/settings.json</c>, for
/// windows whose companion does not send it (one installed before the colour was added, or not yet
/// reloaded since). Only single-folder local windows: a multi-root workspace keeps its settings in
/// the <c>.code-workspace</c> file, whose path the deck does not know. The file is read again only
/// when it changes.
/// </summary>
internal static class PeacockSettings
{
    private static readonly ConcurrentDictionary<string, (DateTime Written, PluginColor? Color)> Cache = new(StringComparer.Ordinal);

    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>The colour in the folder's settings, or null when there is none or it cannot be read. Never throws.</summary>
    public static PluginColor? ColorFor(IReadOnlyList<string> folders)
    {
        if (folders.Count != 1 || !Uri.TryCreate(folders[0], UriKind.Absolute, out var uri) || !uri.IsFile) return null;
        return Read(Path.Combine(uri.LocalPath, ".vscode", "settings.json"));
    }

    internal static PluginColor? Read(string path)
    {
        try
        {
            var written = File.GetLastWriteTimeUtc(path);
            if (written.Year < 1700) return null; // no such file
            if (Cache.TryGetValue(path, out var cached) && cached.Written == written) return cached.Color;

            using var doc = JsonDocument.Parse(File.ReadAllBytes(path), Options);
            var color = doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("peacock.color", out var value)
                        && value.ValueKind == JsonValueKind.String
                ? CodeKey.ParseHex(value.GetString())
                : null;
            Cache[path] = (written, color);
            return color;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
