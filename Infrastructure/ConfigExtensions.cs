using Microsoft.Extensions.Configuration;

namespace SimpleDiffusion.Infrastructure
{
    public static class ConfigExtensions
    {
        /// <summary>
        /// Reads a boolean feature flag. Returns <paramref name="fallback"/> when the key is absent,
        /// empty, or not a valid bool — unlike <c>bool.Parse(config[key])</c>, which both warns on a
        /// possibly-null argument and throws at runtime if the value isn't a clean "true"/"false".
        /// </summary>
        public static bool Flag(this IConfiguration config, string key, bool fallback = true)
            => bool.TryParse(config[key], out var value) ? value : fallback;
    }
}
