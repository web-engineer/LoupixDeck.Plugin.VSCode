using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.VSCode.Platform;

/// <summary>
/// Sends a VS Code shortcut through the host's key macros when no companion window is connected,
/// bringing the editor to the front first so the keys land there and not in whatever app had
/// focus. One step goes out as <c>System.KeyCombination</c>; a chord such as <c>Cmd+K Cmd+S</c>
/// goes out as one <c>System.KeySequence</c>, so its steps cannot arrive out of order.
/// </summary>
internal sealed class KeySender(Func<bool> bringToFront)
{
    /// <summary>
    /// After the editor was seen in front, trust that for this long. A dial sends a burst of
    /// shortcuts and checking the front app costs two child processes each time.
    /// </summary>
    private static readonly TimeSpan FrontTrust = TimeSpan.FromSeconds(1.5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _frontUntil;
    private string? _frontKey;

    /// <summary>
    /// Returns false when there is no editor to send to, the shortcut is blank, or the editor could
    /// not be brought to the front; no keys are sent then.
    /// </summary>
    public async Task<bool> SendAsync(IPluginHost host, EditorApp? app, string shortcut, int repeat = 1)
    {
        if (app is null || Format(shortcut) is not { } command) return false;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (bringToFront() && (DateTime.UtcNow >= _frontUntil || _frontKey != app.Key))
            {
                if (!await EditorApps.ActivateAsync(host, app).ConfigureAwait(false)) return false;
            }

            _frontUntil = DateTime.UtcNow + FrontTrust;
            _frontKey = app.Key;
            for (var i = 0; i < repeat; i++)
            {
                host.ExecuteCommand(command);
                if (i < repeat - 1) await Task.Delay(40).ConfigureAwait(false);
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forget that the editor was in front, e.g. after the user switched apps on purpose.</summary>
    public void Invalidate() => _frontUntil = DateTime.MinValue;

    /// <summary>
    /// The host command for a shortcut in host syntax, chord steps separated by spaces:
    /// <c>Cmd+S</c> → <c>System.KeyCombination(Cmd+S)</c>,
    /// <c>Cmd+K Cmd+S</c> → <c>System.KeySequence(Cmd+K,Cmd+S)</c>. Null for a blank shortcut.
    /// </summary>
    internal static string? Format(string? shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut)) return null;
        var steps = shortcut.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return steps.Length == 1
            ? $"System.KeyCombination({steps[0]})"
            : $"System.KeySequence({string.Join(',', steps)})";
    }
}
