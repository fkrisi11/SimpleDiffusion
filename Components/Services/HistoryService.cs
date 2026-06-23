using System.Text.Json;
using Microsoft.JSInterop;
using SimpleDiffusion.Components.Models;

namespace SimpleDiffusion.Components.Services;

/// <summary>
/// Per-device generation history, persisted in the browser's localStorage so it survives a page
/// refresh (or a dropped circuit). Only metadata + result-store ids are stored — never image bytes —
/// so it stays tiny. The images are served from the result store and recoverable while still cached.
/// Scoped per circuit, like <see cref="UiPreferences"/>; call <see cref="EnsureLoadedAsync"/> before use.
/// </summary>
public sealed class HistoryService
{
    private const string StorageKey = "sd-generation-history";
    private const int MaxEntries = 200;   // metadata only, so this is a few hundred KB at most

    private readonly IJSRuntime _js;
    private bool _loaded;
    private List<HistoryEntry> _entries = new();

    public HistoryService(IJSRuntime js) => _js = js;

    /// <summary>Newest first.</summary>
    public IReadOnlyList<HistoryEntry> Entries => _entries;
    public bool Loaded => _loaded;
    public event Action? Changed;

    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var json = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var list = JsonSerializer.Deserialize<List<HistoryEntry>>(json!);
                if (list is not null) _entries = list;
            }
        }
        catch { /* empty history is fine */ }
        Changed?.Invoke();
    }

    public async Task AddAsync(HistoryEntry entry)
    {
        // Load first so a generation that fires before the tab is opened doesn't clobber existing history.
        await EnsureLoadedAsync();
        if (entry.ResultIds.Count == 0) return;   // nothing to recover — don't record it

        _entries.Insert(0, entry);
        if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        await PersistAsync();
        Changed?.Invoke();
    }

    public async Task RemoveAsync(string id)
    {
        await EnsureLoadedAsync();
        _entries.RemoveAll(e => e.Id == id);
        await PersistAsync();
        Changed?.Invoke();
    }

    public async Task ClearAsync()
    {
        await EnsureLoadedAsync();
        _entries.Clear();
        await PersistAsync();
        Changed?.Invoke();
    }

    private async Task PersistAsync()
    {
        try { await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, JsonSerializer.Serialize(_entries)); }
        catch { /* localStorage full / unavailable — history just won't persist */ }
    }
}
