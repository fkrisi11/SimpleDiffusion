using System.Text.Json;

namespace SimpleDiffusion.Components.Services;

/// <summary>
/// Shared store for the user's saved tag groups (persisted in <c>tag_groups.json</c>). Scoped per
/// connection so the txt2img and img2img Prompt Tools panels share one list and stay in sync via
/// <see cref="Changed"/>.
/// </summary>
public sealed class TagGroupService
{
    private static string FilePath => Path.Combine(Directory.GetCurrentDirectory(), "tag_groups.json");
    private bool _loaded;

    public List<TagGroup> Groups { get; private set; } = new();

    /// <summary>Raised after the list changes (save/manage) so any open panel can re-render.</summary>
    public event Action? Changed;

    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(FilePath))
                Groups = JsonSerializer.Deserialize<List<TagGroup>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) { Console.WriteLine($"Error loading tag groups: {ex.Message}"); }
    }

    /// <summary>Persist the current list to disk and notify listeners.</summary>
    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Groups, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Console.WriteLine($"Error saving tag groups: {ex.Message}"); }
        Changed?.Invoke();
    }

    /// <summary>Add a new group or update an existing one (matched by name, case-insensitive), then persist.</summary>
    public void AddOrUpdate(string name, string tags)
    {
        EnsureLoaded();
        var existing = Groups.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) existing.Tags = tags;
        else Groups.Add(new TagGroup { Name = name, Tags = tags });
        Save();
    }
}
