using System.Text.Json;

namespace SimpleDiffusion.Components.Services;

/// <summary>
/// Per-LoRA favourites + usage counts, keyed by LoRA name and persisted to lora_stats.json.
/// Scoped so the LoRA browser and the settings dialog share one instance and update via
/// <see cref="Changed"/>.
/// </summary>
public sealed class LoraStatsService
{
    private static string FilePath => Path.Combine(Directory.GetCurrentDirectory(), "lora_stats.json");
    private readonly object _gate = new();
    private bool _loaded;

    private HashSet<string> _favorites = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, int> _usage = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    private sealed class Data
    {
        public List<string> Favorites { get; set; } = new();
        public Dictionary<string, int> Usage { get; set; } = new();
    }

    public void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (File.Exists(FilePath))
                {
                    var d = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath));
                    if (d != null)
                    {
                        _favorites = new(d.Favorites ?? new(), StringComparer.OrdinalIgnoreCase);
                        _usage = new(d.Usage ?? new(), StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"Error loading lora stats: {ex.Message}"); }
        }
    }

    private void Save()
    {
        try
        {
            Data d;
            lock (_gate)
            {
                d = new Data { Favorites = _favorites.ToList(), Usage = new Dictionary<string, int>(_usage) };
            }
            File.WriteAllText(FilePath, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Console.WriteLine($"Error saving lora stats: {ex.Message}"); }
        Changed?.Invoke();
    }

    public bool IsFavorite(string? name)
    {
        EnsureLoaded();
        lock (_gate) return !string.IsNullOrEmpty(name) && _favorites.Contains(name);
    }

    public void ToggleFavorite(string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        EnsureLoaded();
        lock (_gate) { if (!_favorites.Add(name)) _favorites.Remove(name); }
        Save();
    }

    public int GetUsage(string? name)
    {
        EnsureLoaded();
        lock (_gate) return !string.IsNullOrEmpty(name) && _usage.TryGetValue(name, out var c) ? c : 0;
    }

    public void IncrementUsage(string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        EnsureLoaded();
        lock (_gate) _usage[name] = (_usage.TryGetValue(name, out var c) ? c : 0) + 1;
        Save();
    }

    public void ResetUsage()
    {
        EnsureLoaded();
        lock (_gate) _usage.Clear();
        Save();
    }

    public void ClearFavorites()
    {
        EnsureLoaded();
        lock (_gate) _favorites.Clear();
        Save();
    }
}
