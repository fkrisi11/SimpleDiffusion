namespace SimpleDiffusion.Components;

/// <summary>The accent colours offered in Settings → Appearance. A theme is one of these accents
/// combined with a brightness (Light / Dark / AMOLED), so we only maintain 3 base palettes and
/// recolour the Primary per accent.</summary>
public static class AppThemes
{
    public static readonly (string Name, string Color)[] Accents =
    {
        ("Purple", "#7e6fff"),
        ("Blue",   "#4a86ff"),
        ("Teal",   "#1ec8a5"),
        ("Green",  "#3dcb6c"),
        ("Amber",  "#ffa726"),
        ("Rose",   "#ff5c8a"),
    };

    public const string DefaultAccent = "#7e6fff";
    public const string DefaultSecondary = "#ff5c8a";

    /// <summary>Scale each RGB channel by <paramref name="factor"/> to derive a lighter/darker shade
    /// (used for the Primary hover/lighten variants so they match a custom accent).</summary>
    public static string Shade(string hex, double factor)
    {
        try
        {
            var h = hex.TrimStart('#');
            if (h.Length != 6) return hex;
            int Ch(int start) => Math.Clamp((int)Math.Round(Convert.ToInt32(h.Substring(start, 2), 16) * factor), 0, 255);
            return $"#{Ch(0):x2}{Ch(2):x2}{Ch(4):x2}";
        }
        catch { return hex; }
    }
}
