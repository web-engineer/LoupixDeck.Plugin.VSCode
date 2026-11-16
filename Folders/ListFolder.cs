using LoupixDeck.Plugin.VSCode.Bridge;
using LoupixDeck.Plugin.VSCode.Rendering;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.VSCode.Folders;

/// <summary>
/// One key's worth of a live list: its text and colours, and what pressing it does. <see cref="Press"/>
/// returns null when it worked, else a short reason for the key ("Gone", "Failed"). A
/// <see cref="Symbol"/> (a host symbol id) is drawn small above the text; <see cref="Framed"/> draws
/// a border round the key, for the one that stands out in a set of coloured keys.
/// </summary>
internal sealed record FolderItem(string Text, PluginColor BackColor, Func<Task<string?>> Press, bool Bold = false)
{
    /// <summary>The text colour; white when not set.</summary>
    public PluginColor? TextColor { get; init; }

    public string? Symbol { get; init; }

    public bool Framed { get; init; }
}

/// <summary>
/// The items of one live list (tasks, problems, open editors…), shared by every page of its
/// folder. Loads when the first page opens and again whenever the hub says the list may have
/// changed, and stops listening when the last page closes. A newer load always wins over an
/// older one that finishes late.
/// </summary>
internal sealed class FolderList
{
    private readonly BridgeHub _hub;
    private readonly Func<BridgeHub, Task<IReadOnlyList<FolderItem>>> _load;
    private readonly Func<HubChange, bool> _reloadOn;
    private readonly IPluginLogger _log;
    private readonly object _lock = new();
    private int _users;
    private int _generation;
    private IReadOnlyList<FolderItem>? _items;

    public FolderList(BridgeHub hub, Func<BridgeHub, Task<IReadOnlyList<FolderItem>>> load, Func<HubChange, bool> reloadOn, IPluginLogger log)
    {
        _hub = hub;
        _load = load;
        _reloadOn = reloadOn;
        _log = log;
    }

    /// <summary>The items, or null while the first load is still running.</summary>
    public IReadOnlyList<FolderItem>? Items
    {
        get
        {
            lock (_lock) return _items;
        }
    }

    /// <summary>Raised after the items were (re)loaded.</summary>
    public event Action? Changed;

    /// <summary>A page opened: the first one starts listening and loads.</summary>
    public void Attach()
    {
        lock (_lock)
        {
            if (_users++ > 0) return;
            _hub.Changed += OnHubChanged;
        }

        _ = ReloadAsync();
    }

    /// <summary>A page closed: the last one stops listening.</summary>
    public void Detach()
    {
        lock (_lock)
        {
            if (_users == 0 || --_users > 0) return;
            _hub.Changed -= OnHubChanged;
            _generation++;
        }
    }

    private void OnHubChanged(HubChange change)
    {
        if (_reloadOn(change)) _ = ReloadAsync();
    }

    /// <summary>Loads the items again. Never throws.</summary>
    public async Task ReloadAsync()
    {
        int generation;
        lock (_lock) generation = ++_generation;

        IReadOnlyList<FolderItem> items;
        try
        {
            items = await _load(_hub).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("VS Code folder: loading the list failed", ex);
            items = [];
        }

        lock (_lock)
        {
            if (generation != _generation) return;
            _items = items;
        }

        Changed?.Invoke();
    }
}

/// <summary>
/// A live list folder, one item per key. The grid comes from the host, so the same folder fills a
/// 4x3 CT and a 5x3 Live alike. When the items do not fit, the folder pages in place: the last key
/// is "More ›", which moves to the next page and wraps round to the first; the first dial turns to
/// the next or previous page and its press goes back to the first. Paging never opens another
/// folder, so the host's Back key always leaves the list in one press. (The host turns the
/// physical buttons off while a folder is open, and the other dials do only what a folder asks.)
/// </summary>
internal sealed class ListFolder : FolderProviderBase
{
    private readonly FolderList _list;
    private readonly IPluginHost _host;
    private readonly string _empty;
    private int _page;

    public ListFolder(string title, FolderList list, IPluginHost host, string empty)
    {
        Title = title;
        _list = list;
        _host = host;
        _empty = empty;
    }

    public override string Title { get; }

    /// <summary>The page shown, from 0; kept within the pages the list has now.</summary>
    internal int Page => Math.Min(_page, Math.Max(0, PageCount() - 1));

    public override void OnEnter()
    {
        _list.Changed += RaiseEntriesChanged;
        _list.Attach();
    }

    public override void OnExit()
    {
        _list.Changed -= RaiseEntriesChanged;
        _list.Detach();
    }

    // The host reads this on every turn and press, so it follows the list as it loads and changes.
    public override IReadOnlyDictionary<int, RotaryOverride> RotaryOverrides =>
        PageCount() > 1
            ? new Dictionary<int, RotaryOverride>
            {
                [0] = new()
                {
                    OnLeft = () => GoTo(Page - 1),
                    OnRight = () => GoTo(Page + 1),
                    OnPress = () => GoTo(0)
                }
            }
            : new Dictionary<int, RotaryOverride>();

    public override IReadOnlyList<FolderEntry> BuildEntries()
    {
        var grid = _host.FolderGrid;
        var items = _list.Items;
        if (items is null) return [Message(grid, _host.Tr("Loading…"))];
        if (items.Count == 0) return [Message(grid, _host.Tr(_empty))];

        var capacity = Capacity(grid);
        if (capacity == 0) return [];
        var pages = PageCount();
        var perPage = pages > 1 ? capacity - 1 : capacity;
        // The list may have shrunk under a later page; show what is left on the last one.
        var page = Page;
        var start = page * perPage;

        var entries = new List<FolderEntry>();
        for (var i = 0; i < perPage && start + i < items.Count; i++)
        {
            var slot = grid.SlotForIndex(i);
            if (slot < 0) break;
            var item = items[start + i];
            var drawn = item.Symbol is not null || item.Framed;
            entries.Add(new FolderEntry
            {
                SlotIndex = slot,
                // With a symbol the text goes under it, so the callback draws both.
                Text = item.Symbol is null ? item.Text : string.Empty,
                TextSize = 13,
                Bold = item.Bold,
                BackColor = item.BackColor,
                TextColor = item.TextColor ?? PluginColor.White,
                Render = drawn ? canvas => Draw(canvas, item) : null,
                OnPress = () => PressAsync(item, slot)
            });
        }

        if (pages > 1)
        {
            var next = (page + 1) % pages;
            entries.Add(new FolderEntry
            {
                SlotIndex = grid.SlotForIndex(perPage),
                Text = $"{_host.Tr("More")} ›\n{next + 1}/{pages}",
                TextSize = 14,
                Bold = true,
                BackColor = CodeKey.Dim,
                TextColor = PluginColor.White,
                OnPress = () => GoTo(next)
            });
        }

        return entries;
    }

    /// <summary>How many keys the grid has for entries (every slot except Back).</summary>
    internal static int Capacity(FolderGridInfo grid)
    {
        var n = 0;
        while (grid.SlotForIndex(n) >= 0) n++;
        return n;
    }

    /// <summary>How many pages the list fills; 1 while loading or when everything fits.</summary>
    private int PageCount()
    {
        var items = _list.Items;
        var capacity = Capacity(_host.FolderGrid);
        if (items is null || capacity < 2 || items.Count <= capacity) return 1;
        var perPage = capacity - 1;
        return (items.Count + perPage - 1) / perPage;
    }

    /// <summary>Shows <paramref name="page"/>, kept within the pages there are; the dial stops at either end.</summary>
    private Task GoTo(int page)
    {
        var target = Math.Clamp(page, 0, PageCount() - 1);
        if (target == Page) return Task.CompletedTask;
        _page = target;
        RaiseEntriesChanged();
        return Task.CompletedTask;
    }

    private async Task PressAsync(FolderItem item, int slot)
    {
        string? problem;
        try
        {
            problem = await item.Press().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _host.Logger.Error($"VS Code folder {Title}: press failed", ex);
            problem = "Failed";
        }

        if (problem is not null) _host.OverlayTouchText(slot, _host.Tr(problem), TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Draws the frame and the symbol with the text straight under it, the two centred on the key
    /// as one block, scaled to the key's real size (the sizes are for a 90 px key). The host paints
    /// the background first and plain text after.
    /// </summary>
    internal static void Draw(IRenderCanvas canvas, FolderItem item)
    {
        var w = canvas.Width;
        var h = canvas.Height;
        var scale = Math.Min(w, h) / 90.0;
        int S(double v) => (int)Math.Round(v * scale);
        var color = item.TextColor ?? PluginColor.White;

        if (item.Framed) canvas.DrawRoundedRectangle(S(2), S(2), w - S(4), h - S(4), S(8), Math.Max(2, S(3)), color);
        if (item.Symbol is null) return;

        var icon = S(16);
        var fontSize = S(13);
        var boxWidth = w - S(8);
        // The host wraps at the box width and spaces lines by the font's own spacing, which the
        // canvas does not expose; 1.2 x the size is close enough to centre the block.
        var lines = item.Text.Split('\n').Sum(line =>
            Math.Max(1, (int)Math.Ceiling(canvas.MeasureText(line, fontSize, item.Bold) / Math.Max(1, boxWidth))));
        var gap = S(2);
        var block = icon + gap + (int)Math.Round(lines * fontSize * 1.2);
        var top = Math.Max(S(4), (h - block) / 2);

        canvas.DrawSymbol(item.Symbol, (w - icon) / 2, top, icon, icon, color);
        var textTop = top + icon + gap;
        canvas.DrawText(item.Text, S(4), textTop, boxWidth, h - textTop - S(2), color, fontSize,
            TextHAlign.Center, TextVAlign.Top, item.Bold);
    }

    private static FolderEntry Message(FolderGridInfo grid, string text) => new()
    {
        SlotIndex = grid.SlotForIndex(0),
        Text = text,
        TextSize = 13,
        BackColor = CodeKey.Dim,
        TextColor = PluginColor.White
    };
}
