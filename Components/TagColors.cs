namespace SimpleDiffusion.Components;

/// <summary>Resolves the display colour for a tag — from the loaded tag-list colour config when it
/// has one, else category defaults. Shared by the autocomplete popup, the mobile suggestion ribbon,
/// and anywhere else that lists tag suggestions (e.g. the Quick Tags / Favourites boards).</summary>
public static class TagColors
{
    public static string Category(TagItem item, TagService tagService)
    {
        // 1. The tag list's own colour config, keyed by category id.
        if (!string.IsNullOrEmpty(item.TagListName) &&
            tagService._colorConfig.TryGetValue(item.TagListName, out var listConfig) &&
            listConfig.TryGetValue(item.Category.ToString(), out var colors) && colors.Length > 0)
        {
            return colors[0];
        }

        // 2. Hard-coded defaults.
        return item.Category switch
        {
            1 => "#ea4b4b", // Artist
            3 => "#b15fe6", // Copyright / series
            4 => "#00ad00", // Character
            5 => "#ff8a00", // Meta
            7 => "#ffffff",
            9 => "#ff72ff", // LoRA
            _ => "#e0e0e0"  // General
        };
    }
}
