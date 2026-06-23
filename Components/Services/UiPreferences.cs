using System.Text.Json;
using Microsoft.JSInterop;
using SimpleDiffusion.Components.Civitai;

namespace SimpleDiffusion.Components.Services;

/// <summary>
/// Per-device UI preferences (Civitai API key + NSFW handling), persisted in the browser's
/// localStorage. NSFW filtering is per rating category via bitmasks (PG=1, PG-13=2, R=4,
/// X=8, XXX=16). The user can also override a specific model's rating when none is known.
/// Scoped per circuit; call <see cref="EnsureLoadedAsync"/> once after first render.
/// </summary>
public sealed class UiPreferences
{
    private const string StorageKey = "sd-device-settings";

    private readonly IJSRuntime _js;
    private bool _loaded;

    public UiPreferences(IJSRuntime js) => _js = js;

    public string CivitaiApiKey { get; set; } = "";

    /// <summary>Rating bits to blur (default: XXX).</summary>
    public int BlurLevelMask { get; set; } = CivitaiNsfw.Xxx;

    /// <summary>Rating bits to hide entirely.</summary>
    public int HideLevelMask { get; set; }

    public bool RevealOnHover { get; set; } = true;
    public bool BlurNames { get; set; }
    public bool BlockNsfwGallery { get; set; }

    /// <summary>Append a LoRA's trigger words to the prompt when it's added.</summary>
    public bool AutoAddTriggerWords { get; set; } = true;

    /// <summary>Autoplay preview videos on cards/galleries (off by default). The fullscreen viewer always plays.</summary>
    public bool AutoplayVideos { get; set; }

    /// <summary>Mobile haptic feedback (vibration) for generate / rearrange / pick actions.</summary>
    public bool HapticFeedback { get; set; }

    /// <summary>Gallery image optimization: when on, the fullscreen viewer shows a downscaled JPEG
    /// of large images so pan/pinch/zoom/close stay smooth (especially on phones). The original PNG
    /// is never touched — metadata, downloads and Send always use it. Off serves the raw PNG.</summary>
    public bool OptimizeLargeImages { get; set; } = true;

    /// <summary>Fullscreen viewer gesture (only when not zoomed): the action for a swipe up / down on
    /// the image. One of "none", "close", "metadata". Works regardless of whether the chrome is shown.</summary>
    public string LightboxSwipeUp { get; set; } = "close";
    public string LightboxSwipeDown { get; set; } = "none";

    /// <summary>UI theme brightness: "Dark" (default), "Amoled" (pure-black for OLED), or "Light".</summary>
    public string ThemeMode { get; set; } = "Dark";

    /// <summary>Accent (Primary) colour for the theme — a hex from <see cref="AppThemes.Accents"/>.</summary>
    public string AccentColor { get; set; } = AppThemes.DefaultAccent;

    /// <summary>Secondary colour for the theme — a hex from <see cref="AppThemes.Accents"/>.</summary>
    public string SecondaryColor { get; set; } = AppThemes.DefaultSecondary;

    /// <summary>Whether the first-run setup wizard has been completed on this device.</summary>
    public bool OnboardingCompleted { get; set; }

    public const int DefaultLoraCardWidth = 180;
    public const int DefaultCivitaiCardWidth = 200;
    public const int DefaultGalleryCardWidth = 180;
    public int LoraCardWidth { get; set; } = DefaultLoraCardWidth;
    public int CivitaiCardWidth { get; set; } = DefaultCivitaiCardWidth;
    public int GalleryCardWidth { get; set; } = DefaultGalleryCardWidth;

    /// <summary>Per-model manual rating overrides (key -> nsfwLevel bit).</summary>
    public Dictionary<string, int> ManualRatings { get; set; } = new();

    public bool Loaded => _loaded;
    public event Action? Changed;

    // ---- Decisions ----

    public bool ShouldBlur(int? level) => level is { } l && (l & BlurLevelMask) != 0;
    public bool ShouldHide(int? level) => level is { } l && (l & HideLevelMask) != 0;

    /// <summary>Manual override for a model key, if the user set one.</summary>
    public int? ManualRating(string key) =>
        ManualRatings.TryGetValue(key, out var v) ? v : null;

    /// <summary>The rating to use for a model: a manual override if present, else the known level.</summary>
    public int? EffectiveLevel(string key, int? known) => ManualRating(key) ?? known;

    public async Task SetManualRatingAsync(string key, int? level)
    {
        if (level is { } l) ManualRatings[key] = l;
        else ManualRatings.Remove(key);
        await SaveAsync();
    }

    // ---- Persistence ----

    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var json = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var d = JsonSerializer.Deserialize<Dto>(json!);
                if (d is not null)
                {
                    CivitaiApiKey = d.CivitaiApiKey ?? "";
                    BlurLevelMask = d.BlurLevelMask;
                    HideLevelMask = d.HideLevelMask;
                    RevealOnHover = d.RevealOnHover;
                    BlurNames = d.BlurNames;
                    BlockNsfwGallery = d.BlockNsfwGallery;
                    AutoAddTriggerWords = d.AutoAddTriggerWords;
                    AutoplayVideos = d.AutoplayVideos;
                    HapticFeedback = d.HapticFeedback;
                    OptimizeLargeImages = d.OptimizeLargeImages;
                    LightboxSwipeUp = string.IsNullOrWhiteSpace(d.LightboxSwipeUp) ? "close" : d.LightboxSwipeUp;
                    LightboxSwipeDown = string.IsNullOrWhiteSpace(d.LightboxSwipeDown) ? "none" : d.LightboxSwipeDown;
                    ThemeMode = string.IsNullOrWhiteSpace(d.ThemeMode) ? "Dark" : d.ThemeMode;
                    AccentColor = string.IsNullOrWhiteSpace(d.AccentColor) ? AppThemes.DefaultAccent : d.AccentColor;
                    SecondaryColor = string.IsNullOrWhiteSpace(d.SecondaryColor) ? AppThemes.DefaultSecondary : d.SecondaryColor;
                    OnboardingCompleted = d.OnboardingCompleted;
                    LoraCardWidth = d.LoraCardWidth > 0 ? d.LoraCardWidth : DefaultLoraCardWidth;
                    CivitaiCardWidth = d.CivitaiCardWidth > 0 ? d.CivitaiCardWidth : DefaultCivitaiCardWidth;
                    GalleryCardWidth = d.GalleryCardWidth > 0 ? d.GalleryCardWidth : DefaultGalleryCardWidth;
                    ManualRatings = d.ManualRatings ?? new();
                }
            }
        }
        catch { /* defaults are fine */ }

        await ApplyHoverClassAsync();
        Changed?.Invoke();
    }

    /// <summary>Write to localStorage WITHOUT firing <see cref="Changed"/> — for cheap, frequent
    /// saves like the card-size slider that shouldn't trigger re-renders elsewhere.</summary>
    public async Task PersistAsync()
    {
        var dto = new Dto
        {
            CivitaiApiKey = CivitaiApiKey,
            BlurLevelMask = BlurLevelMask,
            HideLevelMask = HideLevelMask,
            RevealOnHover = RevealOnHover,
            BlurNames = BlurNames,
            BlockNsfwGallery = BlockNsfwGallery,
            AutoAddTriggerWords = AutoAddTriggerWords,
            AutoplayVideos = AutoplayVideos,
            HapticFeedback = HapticFeedback,
            OptimizeLargeImages = OptimizeLargeImages,
            LightboxSwipeUp = LightboxSwipeUp,
            LightboxSwipeDown = LightboxSwipeDown,
            ThemeMode = ThemeMode,
            AccentColor = AccentColor,
            SecondaryColor = SecondaryColor,
            OnboardingCompleted = OnboardingCompleted,
            LoraCardWidth = LoraCardWidth,
            CivitaiCardWidth = CivitaiCardWidth,
            GalleryCardWidth = GalleryCardWidth,
            ManualRatings = ManualRatings,
        };
        try { await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, JsonSerializer.Serialize(dto)); }
        catch { }
    }

    public async Task SaveAsync()
    {
        await PersistAsync();
        await ApplyHoverClassAsync();
        Changed?.Invoke();
    }

    /// <summary>Reset all per-device settings to their defaults (factory reset). Model rating
    /// overrides are only cleared when <paramref name="resetRatings"/> is true.</summary>
    public async Task ResetToDefaultsAsync(bool resetRatings)
    {
        CivitaiApiKey = "";
        BlurLevelMask = CivitaiNsfw.Xxx;
        HideLevelMask = 0;
        RevealOnHover = true;
        BlurNames = false;
        BlockNsfwGallery = false;
        AutoAddTriggerWords = true;
        AutoplayVideos = false;
        OptimizeLargeImages = true;
        LightboxSwipeUp = "close";
        LightboxSwipeDown = "none";
        ThemeMode = "Dark";
        AccentColor = AppThemes.DefaultAccent;
        SecondaryColor = AppThemes.DefaultSecondary;
        OnboardingCompleted = false;
        LoraCardWidth = DefaultLoraCardWidth;
        CivitaiCardWidth = DefaultCivitaiCardWidth;
        GalleryCardWidth = DefaultGalleryCardWidth;
        if (resetRatings) ManualRatings = new();
        await SaveAsync();
    }

    private async Task ApplyHoverClassAsync()
    {
        try { await _js.InvokeVoidAsync("civNsfw.setHover", RevealOnHover); } catch { }
        try { await _js.InvokeVoidAsync("sdHaptic.setEnabled", HapticFeedback); } catch { }
    }

    private sealed class Dto
    {
        public string? CivitaiApiKey { get; set; }
        public int BlurLevelMask { get; set; } = CivitaiNsfw.Xxx;
        public int HideLevelMask { get; set; }
        public bool RevealOnHover { get; set; } = true;
        public bool BlurNames { get; set; }
        public bool BlockNsfwGallery { get; set; }
        public bool AutoAddTriggerWords { get; set; } = true;
        public bool AutoplayVideos { get; set; }
        public bool HapticFeedback { get; set; }
        public bool OptimizeLargeImages { get; set; } = true;
        public string? LightboxSwipeUp { get; set; } = "close";
        public string? LightboxSwipeDown { get; set; } = "none";
        public string? ThemeMode { get; set; } = "Dark";
        public string? AccentColor { get; set; } = AppThemes.DefaultAccent;
        public string? SecondaryColor { get; set; } = AppThemes.DefaultSecondary;
        public bool OnboardingCompleted { get; set; }
        public int LoraCardWidth { get; set; } = DefaultLoraCardWidth;
        public int CivitaiCardWidth { get; set; } = DefaultCivitaiCardWidth;
        public int GalleryCardWidth { get; set; } = DefaultGalleryCardWidth;
        public Dictionary<string, int>? ManualRatings { get; set; }
    }
}
