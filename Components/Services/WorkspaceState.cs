namespace SimpleDiffusion.Components.Services
{
    /// <summary>
    /// Holds the user's in-progress generation inputs (prompt, settings, selected preset,
    /// active tab) so they survive in-app navigation within the same Blazor circuit — e.g.
    /// visiting the Settings page and coming back should not wipe the fields on the home page.
    /// Registered as a scoped service, so it lives for the lifetime of the connection/session.
    /// </summary>
    public class WorkspaceState
    {
        public Txt2ImgRequest Request { get; set; } = new();
        public string SelectedPresetName { get; set; } = "";
        public int ActiveTabIndex { get; set; }

        // A/B comparison mode (Txt2Img): a second control set, plus which one is being edited.
        public Txt2ImgRequest RequestB { get; set; } = new();
        public bool AbEnabled { get; set; }
        public int AbActive { get; set; } // 0 = A, 1 = B

        // ControlNet units per txt2img set (kept here so they survive tab switches and the A/B-set
        // toggle, which recreates the form). The active set's list is edited by the form; both are
        // mirrored onto their request's alwayson_scripts before generation.
        public List<ControlNetUnit> ControlNetUnitsA { get; set; } = new();
        public List<ControlNetUnit> ControlNetUnitsB { get; set; } = new();

        /// <summary>"Surprise Me" randomness, 0..10. Higher = more words drawn from a wider slice of
        /// the tag dictionaries. Deliberately NOT part of Txt2ImgRequest so presets don't capture it.</summary>
        public int SurpriseCraziness { get; set; } = SurpriseCrazinessDefault;
        public const int SurpriseCrazinessDefault = 3;
        public const int SurpriseCrazinessMax = 10;

        /// <summary>When true, "Surprise Me" prepends the user's own positive/negative prompts to the
        /// randomly-composed ones instead of fully overriding them.</summary>
        public bool SurpriseUsePrompts { get; set; }

        /// <summary>
        /// Raised after the Settings dialog saves, so the home page can reload settings-derived
        /// state (server connection, tag dictionaries, feature toggles) without being recreated.
        /// </summary>
        public event Action? SettingsSaved;

        public void NotifySettingsSaved() => SettingsSaved?.Invoke();
    }
}
