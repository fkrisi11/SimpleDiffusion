using System.Text.RegularExpressions;

namespace SimpleDiffusion.Components;

/// <summary>
/// Resolves "dynamic prompt" syntax into a concrete prompt:
/// <list type="bullet">
/// <item><c>{a|b|c}</c> — pick one at random.</item>
/// <item><c>{2$$a|b|c}</c> — pick N distinct, joined with ", "; <c>{1-2$$...}</c> picks a random N in range.</item>
/// <item><c>__name__</c> — pick a random line from the <c>name</c> wildcard file.</item>
/// </list>
/// Variants may nest and wildcard contents may themselves contain syntax; both are resolved.
/// </summary>
public static class DynamicPrompts
{
    private static readonly Regex VariantRx = new(@"\{([^{}]*)\}", RegexOptions.Compiled);
    private static readonly Regex WildcardRx = new(@"__([a-zA-Z0-9/_.\-]+?)__", RegexOptions.Compiled);

    public static string Resolve(string? prompt, Random rng, Func<string, IReadOnlyList<string>?> wildcards, int maxPasses = 25)
    {
        if (string.IsNullOrEmpty(prompt)) return prompt ?? "";
        var result = prompt;
        for (int pass = 0; pass < maxPasses; pass++)
        {
            bool changed = false;
            result = ResolveWildcards(result, rng, wildcards, ref changed);
            result = ResolveVariants(result, rng, ref changed);
            if (!changed) break;
        }
        return result;
    }

    private static string ResolveWildcards(string text, Random rng, Func<string, IReadOnlyList<string>?> wildcards, ref bool changed)
    {
        bool any = false;
        var outp = WildcardRx.Replace(text, m =>
        {
            var opts = wildcards(m.Groups[1].Value);
            if (opts is null || opts.Count == 0) return m.Value; // unknown wildcard: leave it as-is
            any = true;
            return opts[rng.Next(opts.Count)];
        });
        if (any) changed = true;
        return outp;
    }

    private static string ResolveVariants(string text, Random rng, ref bool changed)
    {
        bool any = false;
        // Innermost braces only (no nested {} inside), so nesting resolves bottom-up across passes.
        var outp = VariantRx.Replace(text, m =>
        {
            any = true;
            var body = m.Groups[1].Value;
            if (body.Length == 0) return "";

            int count = 1;
            var sep = body.IndexOf("$$", StringComparison.Ordinal);
            var optsPart = body;
            if (sep >= 0)
            {
                count = ParseCount(body[..sep].Trim(), rng);
                optsPart = body[(sep + 2)..];
            }

            var options = optsPart.Split('|').Select(o => o.Trim()).ToList();
            if (options.Count == 0) return "";

            if (count <= 1) return options[rng.Next(options.Count)];

            count = Math.Min(count, options.Count);
            var picked = new List<string>(count);
            for (int i = 0; i < count && options.Count > 0; i++)
            {
                int j = rng.Next(options.Count);
                picked.Add(options[j]);
                options.RemoveAt(j);
            }
            return string.Join(", ", picked);
        });
        if (any) changed = true;
        return outp;
    }

    private static int ParseCount(string spec, Random rng)
    {
        if (string.IsNullOrEmpty(spec)) return 1;
        var dash = spec.IndexOf('-');
        if (dash > 0 &&
            int.TryParse(spec[..dash], out var lo) &&
            int.TryParse(spec[(dash + 1)..], out var hi) && hi >= lo)
            return rng.Next(lo, hi + 1);
        return int.TryParse(spec, out var n) ? Math.Max(0, n) : 1;
    }
}
