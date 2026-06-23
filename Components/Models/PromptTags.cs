using System.Text.RegularExpressions;

namespace SimpleDiffusion.Components.Models
{
    /// <summary>Shared prompt/tag string operations used by the tag board and favorites bar.</summary>
    public static class PromptTags
    {
        // A tag token is bounded by start/end, whitespace, comma, or attention brackets — not by
        // surrounding word characters. This avoids matching a tag as a substring of a larger word,
        // and (unlike \b) still matches tags whose escaped form ends in "\)".
        private const string Before = @"(?<![^\s,(<])";
        private const string After = @"(?![^\s,)>])";

        /// <summary>
        /// Escape the Stable Diffusion attention/weighting characters in a tag so a literal tag like
        /// "aqua_(konosuba)" is inserted as "aqua_\(konosuba\)" instead of being parsed as weighting.
        /// </summary>
        public static string Escape(string tag) =>
            string.IsNullOrEmpty(tag) ? tag : tag.Replace("(", "\\(").Replace(")", "\\)");

        /// <summary>Case-insensitive regex matching the (escaped) tag as a standalone token — bare or
        /// wrapped in a <c>(tag:weight)</c> form.</summary>
        public static Regex MatchRegex(string tag)
        {
            var esc = Regex.Escape(Escape(tag));
            return new Regex($@"(?i){Before}(\({esc}:\d+(\.\d+)?\)|{esc}){After}");
        }

        public static bool Contains(string prompt, string tag)
        {
            if (string.IsNullOrEmpty(prompt) || string.IsNullOrEmpty(tag)) return false;
            return MatchRegex(tag).IsMatch(prompt);
        }

        /// <summary>Add the tag (escaped) if absent, remove it if present; returns the new prompt.</summary>
        public static string Toggle(string prompt, string tag)
        {
            if (string.IsNullOrEmpty(tag)) return prompt;
            var inserted = Escape(tag);

            if (string.IsNullOrEmpty(prompt)) return inserted + ", ";

            if (Contains(prompt, tag))
            {
                var esc = Regex.Escape(inserted);
                var pattern = $@"(?i){Before}(\({esc}:\d+(\.\d+)?\)|{esc}){After}\s*,?\s*";
                var p = Regex.Replace(prompt, pattern, string.Empty).Trim();
                p = Regex.Replace(p, @"^,+|,+$", string.Empty).Trim();
                p = Regex.Replace(p, @",\s*,", ", ").Trim();
                if (!string.IsNullOrEmpty(p) && !p.EndsWith(",")) p += ", ";
                return p;
            }

            var clean = prompt.Trim();
            if (clean.EndsWith(",")) return clean + " " + inserted + ", ";
            if (string.IsNullOrEmpty(clean)) return inserted + ", ";
            return clean + ", " + inserted + ", ";
        }
    }
}
