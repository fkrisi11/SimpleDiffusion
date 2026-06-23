using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using SimpleDiffusion.Components.Civitai;

namespace SimpleDiffusion.Components.Tabs;

public partial class CivitaiBrowser : IDisposable
{
    [Inject] private CivitaiService Civitai { get; set; } = default!;
    [Inject] private CivitaiDownloadManager Downloads { get; set; } = default!;
    [Inject] private IDialogService DialogService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private UiPreferences Prefs { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    private ElementReference _rootEl;
    private DotNetObjectReference<CivitaiBrowser>? _selfRef;

    private const int PageSize = 50;

    private readonly List<CivitaiModel> _items = new();

    // Filter state (text fields apply on the Search button; dropdowns apply immediately)
    private string _searchQuery = "";
    private string _modelIdQuery = "";
    private CivitaiSort _sort = CivitaiSort.HighestRated;
    private CivitaiPeriod _period = CivitaiPeriod.AllTime;
    private IEnumerable<string> _selectedTypes = new List<string>();
    private IEnumerable<string> _selectedBaseModels = new List<string>();
    private bool _showNsfw;

    // Layout
    private int _cardWidth = 200;
    private bool _filtersOpen = true;

    // True once the user has run at least one search (drives the initial empty state).
    private bool _searched;

    // Cursor-based pagination (Prev/Next; each page replaces the list, so keys stay unique
    // and it works uniformly for browsing and name search). _pageCursors[i] is the cursor
    // used to load page i; page 0 uses a null cursor.
    private readonly List<string?> _pageCursors = new() { null };
    private int _pageIndex;
    private string? _nextCursor;
    private int? _totalItems;
    private bool _isModelIdSearch;
    private bool _loading;
    private string? _error;
    private int _jumpTarget = 1;

    // Lightweight per-session cache of already-visited pages (items + next cursor), keyed by
    // page index. Cleared on every new search; gone when the circuit (browser tab) closes.
    private readonly Dictionary<int, (List<CivitaiModel> Items, string? NextCursor)> _pageCache = new();

    // Drops results from a stale (superseded) search.
    private int _searchGeneration;

    protected override void OnInitialized()
    {
        // Do NOT search on load — wait for the user to press Search so switching
        // to this tab doesn't fire an unsolicited request.
        Downloads.Changed += OnDownloadsChanged;
        Prefs.Changed += OnPrefsChanged;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await Prefs.EnsureLoadedAsync();
            _cardWidth = Prefs.CivitaiCardWidth; // restore saved card size

            // Left/Right arrow keys page the grid (handler self-gates on tab visibility + no dialog).
            _selfRef = DotNetObjectReference.Create(this);
            await JS.InvokeVoidAsync("civPager.register", _selfRef, _rootEl);

            StateHasChanged();
        }
    }

    /// <summary>Invoked from JS on Left/Right arrow: page back/forward (no-ops when not possible).</summary>
    [JSInvokable]
    public Task OnPagerArrow(int dir) =>
        InvokeAsync(async () =>
        {
            if (_loading || _isModelIdSearch) return;
            if (dir > 0) await NextPage();
            else await PrevPage();
        });

    private CancellationTokenSource? _cardSaveCts;

    private void OnCardWidthChanged(int width)
    {
        _cardWidth = width;
        Prefs.CivitaiCardWidth = width;
        _cardSaveCts?.Cancel();
        _cardSaveCts = new CancellationTokenSource();
        var tok = _cardSaveCts.Token;
        _ = Task.Run(async () => { try { await Task.Delay(500, tok); await Prefs.PersistAsync(); } catch { } });
        StateHasChanged();
    }

    private async Task ResetCardSize()
    {
        _cardWidth = UiPreferences.DefaultCivitaiCardWidth;
        Prefs.CivitaiCardWidth = _cardWidth;
        await Prefs.PersistAsync();
        StateHasChanged();
    }

    private int _prevHideMask;

    private void OnPrefsChanged()
    {
        // If the hide-rating selection changed, abandon any active search and return to the
        // initial state (results may now include/exclude different models).
        if (Prefs.HideLevelMask != _prevHideMask)
        {
            ++_searchGeneration; // cancel any in-flight load
            _items.Clear();
            _error = null;
            _nextCursor = null;
            _pageCursors.Clear();
            _pageCursors.Add(null);
            _pageCache.Clear();
            _pageIndex = 0;
            _searched = false;
        }
        _prevHideMask = Prefs.HideLevelMask;
        InvokeAsync(StateHasChanged);
    }

    /// <summary>Effective rating for a model (manual override or the API's nsfwLevel).</summary>
    private int? LevelOf(CivitaiModel model) =>
        Prefs.EffectiveLevel($"civitai:{model.Id}", model.NsfwLevel);

    /// <summary>Should this card be blurred under the per-category blur settings?</summary>
    private bool Blur(CivitaiModel model, CivitaiImage? img) => Prefs.ShouldBlur(LevelOf(model));

    private bool BlurName(CivitaiModel model) => Prefs.BlurNames && Prefs.ShouldBlur(LevelOf(model));

    // ---- Filter handlers ----
    // Nothing searches until the Search button (or Enter in a text box) is used.
    private Task OnSearchClicked() => RunSearchAsync();

    private async Task OnSearchKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
            await RunSearchAsync();
    }

    /// <summary>Fresh search from page one.</summary>
    private async Task RunSearchAsync()
    {
        _searched = true;
        Downloads.RefreshLibraryIndex(force: true); // reflect any files added/removed since last search
        var generation = ++_searchGeneration;
        _items.Clear();
        _error = null;
        _totalItems = null;
        _nextCursor = null;
        _pageCursors.Clear();
        _pageCursors.Add(null);
        _pageCache.Clear();
        _pageIndex = 0;
        _jumpTarget = 1;
        _isModelIdSearch = false;

        if (int.TryParse(_modelIdQuery.Trim(), out var modelId) && modelId > 0)
        {
            _isModelIdSearch = true;
            await LoadByIdAsync(modelId, generation);
            return;
        }

        await LoadCursorPageAsync(generation);
    }

    private async Task NextPage()
    {
        if (_nextCursor is null) return;
        // Record the cursor for the page we're about to view (so Prev can return here).
        if (_pageIndex == _pageCursors.Count - 1)
            _pageCursors.Add(_nextCursor);
        _pageIndex++;
        await LoadCursorPageAsync(_searchGeneration);
    }

    private async Task PrevPage()
    {
        if (_pageIndex == 0) return;
        _pageIndex--;
        await LoadCursorPageAsync(_searchGeneration);
    }

    /// <summary>Jump to any page we already have a cursor for (1..discovered).</summary>
    private async Task JumpToPage()
    {
        var target = Math.Clamp(_jumpTarget, 1, _pageCursors.Count);
        _pageIndex = target - 1;
        await LoadCursorPageAsync(_searchGeneration);
    }

    private async Task LoadByIdAsync(int modelId, int generation)
    {
        _loading = true;
        StateHasChanged();
        try
        {
            var model = await Civitai.GetModelByIdAsync(modelId, Prefs.CivitaiApiKey);
            if (generation != _searchGeneration) return;

            if (model is not null && !Prefs.ShouldHide(LevelOf(model))) _items.Add(model);
            else if (model is null) _error = $"No model found with id {modelId}.";
        }
        catch (Exception ex)
        {
            if (generation == _searchGeneration) _error = ex.Message;
        }
        finally
        {
            if (generation == _searchGeneration) _loading = false;
            StateHasChanged();
        }
    }

    private async Task LoadCursorPageAsync(int generation)
    {
        _jumpTarget = _pageIndex + 1;

        // Serve from the session cache when we've already loaded this page.
        if (_pageCache.TryGetValue(_pageIndex, out var cached))
        {
            _items.Clear();
            _items.AddRange(cached.Items);
            _nextCursor = cached.NextCursor;
            if (_nextCursor != null && _pageIndex == _pageCursors.Count - 1)
                _pageCursors.Add(_nextCursor);
            StateHasChanged();
            return;
        }

        _loading = true;
        StateHasChanged();

        try
        {
            var query = new CivitaiQuery
            {
                Query = string.IsNullOrWhiteSpace(_searchQuery) ? null : _searchQuery,
                Sort = _sort,
                Period = _period,
                Types = _selectedTypes.ToList(),
                BaseModels = _selectedBaseModels.ToList(),
                // nsfw=true requires an API key whose account has mature content enabled.
                // (The undocumented browsingLevel param makes the public API return 400, so
                //  we don't send it.)
                Nsfw = _showNsfw ? true : false,
                Limit = PageSize,
                Page = 0,
                Cursor = _pageCursors[_pageIndex],
            };

            var result = await Civitai.SearchModelsAsync(query, Prefs.CivitaiApiKey);
            if (generation != _searchGeneration) return; // superseded

            // Replace the page contents (no appending), dedupe, and (optionally) drop NSFW.
            var items = result.Items
                .DistinctBy(i => i.Id)
                .Where(m => !Prefs.ShouldHide(LevelOf(m)))
                .ToList();
            _items.Clear();
            _items.AddRange(items);
            _nextCursor = result.Metadata.NextCursor;
            _totalItems = result.Metadata.TotalItems;
            _pageCache[_pageIndex] = (items, _nextCursor);
        }
        catch (Exception ex)
        {
            if (generation == _searchGeneration)
                _error = "Civitai request failed: " + ex.Message;
        }
        finally
        {
            if (generation == _searchGeneration) _loading = false;
            StateHasChanged();
        }
    }

    private async Task OpenDialog(CivitaiModel model)
    {
        var parameters = new DialogParameters
        {
            ["Model"] = model,
            ["CompatibleBaseModels"] = _selectedBaseModels.ToList()
        };
        var options = new DialogOptions
        {
            CloseButton = true,
            CloseOnEscapeKey = true,
            BackdropClick = true,
            MaxWidth = MaxWidth.Large,
            FullWidth = true
        };
        await DialogService.ShowAsync<Civitai.CivitaiModelDialog>(model.Name, parameters, options);
    }

    /// <summary>Card download button: let the user pick which version(s) to grab.</summary>
    private async Task QuickDownload(CivitaiModel model)
    {
        if (model.ModelVersions.All(v => v.PrimaryFile is null))
        {
            Snackbar.Add("This model has no downloadable file.", Severity.Warning);
            return;
        }

        var parameters = new DialogParameters
        {
            ["Model"] = model,
            ["CompatibleBaseModels"] = _selectedBaseModels.ToList()
        };
        var options = new DialogOptions
        {
            CloseButton = true,
            CloseOnEscapeKey = true,
            BackdropClick = true,
            MaxWidth = MaxWidth.Small,
            FullWidth = true
        };
        await DialogService.ShowAsync<Civitai.CivitaiVersionPickDialog>("Select version", parameters, options);
    }

    private async Task OpenDownloads()
    {
        var options = new DialogOptions
        {
            CloseButton = true,
            CloseOnEscapeKey = true,
            BackdropClick = true,
            MaxWidth = MaxWidth.Medium,
            FullWidth = true
        };
        await DialogService.ShowAsync<Civitai.CivitaiDownloadsDialog>("Downloads", options);
    }

    private void OnDownloadsChanged() => InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        Downloads.Changed -= OnDownloadsChanged;
        Prefs.Changed -= OnPrefsChanged;
        try { _ = JS.InvokeVoidAsync("civPager.unregister"); } catch { }
        _selfRef?.Dispose();
    }

    // ---- Display helpers ----

    /// <summary>Card thumbnail through the image proxy, sized for the grid.</summary>
    private static string ThumbUrl(CivitaiImage? img)
    {
        if (img is null || string.IsNullOrWhiteSpace(img.Url)) return "images/card-no-preview.png";
        // Videos use a different CDN transform than "width=N", so leave their URL untouched.
        var url = img.RenderAsVideo ? img.Url : CivitaiImageUrl.WithWidth(img.Url, 450);
        return "/civitai-image?url=" + Uri.EscapeDataString(url);
    }

    private string PageStatusText()
    {
        var text = $"Page {_pageIndex + 1}";
        if (_totalItems.HasValue) text += $" • {_totalItems.Value:N0} items";
        return text;
    }

    /// <summary>Distinct base models across a model's versions (for the card's base-model chip).</summary>
    private static List<string> BaseModelsOf(CivitaiModel model) =>
        model.ModelVersions
            .Select(v => v.BaseModel)
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Select(b => b!)
            .Distinct()
            .ToList();

    private static string SortLabel(CivitaiSort s) => s switch
    {
        CivitaiSort.HighestRated => "Highest Rated",
        CivitaiSort.MostDownloaded => "Most Downloaded",
        CivitaiSort.MostLiked => "Most Liked",
        CivitaiSort.Newest => "Newest",
        CivitaiSort.Oldest => "Oldest",
        _ => s.ToString()
    };

    private static string Compact(long n) => n switch
    {
        >= 1_000_000 => (n / 1_000_000.0).ToString("0.#") + "M",
        >= 1_000 => (n / 1_000.0).ToString("0.#") + "K",
        _ => n.ToString()
    };
}
