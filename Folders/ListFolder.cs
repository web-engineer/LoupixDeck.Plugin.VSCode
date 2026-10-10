using LoupixDeck.Plugin.VSCode.Bridge;
using LoupixDeck.Plugin.VSCode.Rendering;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.VSCode.Folders;

/// <summary>
/// One key's worth of a live list: its text and colour, and what pressing it does. <see cref="Press"/>
/// returns null when it worked, else a short reason for the key ("Gone", "Failed").
/// </summary>
internal sealed record FolderItem(string Text, PluginColor BackColor, Func<Task<string?>> Press, bool Bold = false);

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
/// One page of a live list folder, one item per key. The grid comes from the host, so the same
/// folder fills a 4x3 CT and a 5x3 Live alike. When the items do not fit, the last key of the page
/// is "More ›", which opens the next page (the host's Back key returns to the one before), and
/// turning the first dial to the right does the same.
/// </summary>
internal sealed class ListFolder : FolderProviderBase
{
    private readonly FolderList _list;
    private readonly IPluginHost _host;
    private readonly string _empty;
    private readonly int _page;
    private ListFolder? _next;

    public ListFolder(string title, FolderList list, IPluginHost host, string empty, int page = 0)
    {
        Title = page == 0 ? title : $"{title} {page + 1}";
        BaseTitle = title;
        _list = list;
        _host = host;
        _empty = empty;
        _page = page;
    }

    public override string Title { get; }

    private string BaseTitle { get; }

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

    public override IReadOnlyDictionary<int, RotaryOverride> RotaryOverrides =>
        HasNextPage()
            ? new Dictionary<int, RotaryOverride>
            {
                // The host has no "back" a plugin can call, so the dial only pages forward; Back pages back.
                [0] = new() { OnRight = () => { _host.OpenFolder(NextPage()); return Task.CompletedTask; } }
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
        var paged = items.Count > capacity;
        var perPage = paged ? capacity - 1 : capacity;
        var pages = paged ? (items.Count + perPage - 1) / perPage : 1;
        // The list may have shrunk under a later page; show what is left on the last one.
        var page = Math.Min(_page, pages - 1);
        var start = page * perPage;

        var entries = new List<FolderEntry>();
        for (var i = 0; i < perPage && start + i < items.Count; i++)
        {
            var slot = grid.SlotForIndex(i);
            if (slot < 0) break;
            var item = items[start + i];
            entries.Add(new FolderEntry
            {
                SlotIndex = slot,
                Text = item.Text,
                TextSize = 13,
                Bold = item.Bold,
                BackColor = item.BackColor,
                TextColor = PluginColor.White,
                OnPress = () => PressAsync(item, slot)
            });
        }

        if (paged && page < pages - 1)
        {
            entries.Add(new FolderEntry
            {
                SlotIndex = grid.SlotForIndex(perPage),
                Text = $"{_host.Tr("More")} ›\n{page + 2}/{pages}",
                TextSize = 14,
                Bold = true,
                BackColor = CodeKey.Dim,
                TextColor = PluginColor.White,
                OpensFolder = NextPage()
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

    private bool HasNextPage()
    {
        var items = _list.Items;
        var capacity = Capacity(_host.FolderGrid);
        if (items is null || capacity < 2 || items.Count <= capacity) return false;
        var perPage = capacity - 1;
        return _page < (items.Count + perPage - 1) / perPage - 1;
    }

    private ListFolder NextPage() => _next ??= new ListFolder(BaseTitle, _list, _host, _empty, _page + 1);

    private async Task PressAsync(FolderItem item, int slot)
    {
        string? problem;
        try
        {
            problem = await item.Press().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _host.Logger.Error($"VS Code folder {BaseTitle}: press failed", ex);
            problem = "Failed";
        }

        if (problem is not null) _host.OverlayTouchText(slot, _host.Tr(problem), TimeSpan.FromSeconds(2));
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
