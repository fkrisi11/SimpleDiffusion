using System.IO;
using System.Text.Json;
using System.Linq;

namespace SimpleDiffusion.Components.Services
{
    public class TagItem
    {
        public string TagListName { get; set; }
        public string Name { get; set; }
        public int Category { get; set; }
        public long Count { get; set; }
        // Null when the tag has no aliases — avoids allocating ~100k+ empty lists across the dictionary.
        public List<string>? Aliases { get; set; }
        public string DisplayText => IsAlias ? $"{MatchedAlias} → {Name}" : Name;
        public bool IsAlias { get; set; }
        public string MatchedAlias { get; set; }
        public bool IsLora { get; set; }

        internal int RelevanceScore { get; set; }

        public string FormattedCount => IsLora ? "LoRA" :
                                       Count >= 1_000_000 ? (Count / 1_000_000.0).ToString("0.#") + "M" :
                                       Count >= 1_000 ? (Count / 1_000.0).ToString("0.#") + "K" :
                                       Count.ToString();
    }

    public class TagService
    {
        private Dictionary<string, TagItem> _tags = new(StringComparer.OrdinalIgnoreCase);
        private List<TagItem> _loras = new();
        public int MaxSuggestions { get; set; } = 12;
        private int EffectiveMaxSuggestions => Math.Clamp(MaxSuggestions, 1, 200);
        public Dictionary<string, List<string>> QuickTags { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        // Alias exact hit index
        private readonly Dictionary<string, (TagItem Tag, string Alias)> _aliasToTag = new(StringComparer.OrdinalIgnoreCase);

        // Substring-friendly inverted index (trigrams)
        private const int GramSize = 3;

        // gram -> candidates that contain that gram (in Name)
        private readonly Dictionary<string, List<TagItem>> _tagsByGram = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<TagItem>> _lorasByGram = new(StringComparer.OrdinalIgnoreCase);

        public void ClearTags()
        {
            _tags.Clear();
            _loras.Clear();
            _aliasToTag.Clear();
            _tagsByGram.Clear();
            _lorasByGram.Clear();
        }

        // Guards a one-time load of the (large, identical-for-everyone) tag data. The signature is the
        // set of inputs that affect the loaded data; if it's unchanged we skip the expensive rebuild.
        private readonly SemaphoreSlim _loadLock = new(1, 1);
        private string? _loadedSignature;

        /// <summary>Load the full tag/LoRA/quick-tag/colour data exactly once for the given inputs.
        /// Concurrent callers (multiple clients connecting) are serialized; repeat calls with the same
        /// inputs are no-ops. Pass different inputs (e.g. the user changed the dictionary in settings)
        /// to force a rebuild.</summary>
        /// <summary>Force the next <see cref="EnsureLoadedAsync"/> to rebuild even if the inputs are
        /// unchanged — e.g. after the user edited custom tags or added LoRA files.</summary>
        public void Invalidate() => _loadedSignature = null;

        public async Task EnsureLoadedAsync(string tagFolder, string selectedDictionary, string customFileName, string loraFolder)
        {
            var sig = $"{tagFolder}|{selectedDictionary}|{customFileName}|{loraFolder}";
            if (_loadedSignature == sig) return;

            await _loadLock.WaitAsync();
            try
            {
                if (_loadedSignature == sig) return; // built while we waited for the lock

                ClearTags();

                // SelectedDictionary may be a comma-separated list of CSVs; "All" (or empty) loads everything.
                var dicts = (selectedDictionary ?? "All").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (dicts.Length == 0 || dicts.Any(d => d.Equals("All", StringComparison.OrdinalIgnoreCase)))
                    await LoadTags(tagFolder, "All", customFileName);
                else
                    foreach (var d in dicts) await LoadTags(tagFolder, d, customFileName);

                await LoadCustomTags(tagFolder, customFileName);
                await LoadQuickTags(tagFolder);
                await LoadLoras(loraFolder);
                BuildIndexes(); // also builds the alias index
                LoadColorConfig(Path.Combine(tagFolder, "color_config.json"));

                _loadedSignature = sig;
            }
            finally { _loadLock.Release(); }
        }

        public async Task LoadTags(string folderPath, string selectedDictionary = "All", string customFileName = "custom_tags.txt")
        {
            if (!Directory.Exists(folderPath)) return;

            IEnumerable<string> files;
            if (string.IsNullOrEmpty(selectedDictionary) || selectedDictionary.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                files = Directory.GetFiles(folderPath, "*.csv");
            }
            else
            {
                var targetFile = Path.Combine(folderPath, selectedDictionary);
                if (File.Exists(targetFile))
                {
                    files = new[] { targetFile };
                }
                else
                {
                    files = Directory.GetFiles(folderPath, "*.csv");
                }
            }

            foreach (var file in files)
            {
                var fileNameWithExt = Path.GetFileName(file);
                if (fileNameWithExt.Equals("color_config.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(customFileName) && fileNameWithExt.Equals(customFileName, StringComparison.OrdinalIgnoreCase)) continue;

                var lines = await File.ReadAllLinesAsync(file);
                string fileName = Path.GetFileNameWithoutExtension(file);

                foreach (var line in lines)
                {
                    var parts = line.Split(',');
                    if (parts.Length < 3) continue;

                    string name = parts[0].Trim('"');
                    int category = int.TryParse(parts[1], out var cat) ? cat : 0;
                    long count = long.TryParse(parts[2].Trim('"'), out var c) ? c : 0;

                    if (_tags.TryGetValue(name, out var existing))
                    {
                        bool isNewSpecial = (category == 1 || category == 3 || category == 4);
                        bool isExistingSpecial = (existing.Category == 1 || existing.Category == 3 || existing.Category == 4);

                        if ((isNewSpecial && !isExistingSpecial) || (count > existing.Count))
                            _tags[name] = CreateTag(name, category, count, parts, fileName);
                    }
                    else
                    {
                        _tags[name] = CreateTag(name, category, count, parts, fileName);
                    }
                }
            }
        }

        public async Task LoadCustomTags(string folderPath, string customFileName = "custom_tags.txt")
        {
            if (string.IsNullOrEmpty(customFileName) || !Directory.Exists(folderPath)) return;

            var filePath = Path.Combine(folderPath, customFileName);
            if (!File.Exists(filePath)) return;

            var lines = await File.ReadAllLinesAsync(filePath);
            foreach (var line in lines)
            {
                var clean = line.Trim();
                if (string.IsNullOrEmpty(clean) || clean.StartsWith("#")) continue;

                var parts = clean.Split(',');
                string name = parts[0].Trim().Trim('"');
                if (string.IsNullOrEmpty(name)) continue;

                int category = 0;
                long count = 0;

                if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out var cat))
                {
                    category = cat;
                }
                if (parts.Length > 2 && long.TryParse(parts[2].Trim().Trim('"'), out var c))
                {
                    count = c;
                }

                var item = new TagItem
                {
                    Name = name,
                    Category = category,
                    Count = count,
                    TagListName = "Custom Tag"
                };

                if (!_tags.TryGetValue(name, out var existing))
                {
                    _tags[name] = item;
                }
                else
                {
                    existing.Category = category;
                    if (count > 0) existing.Count = count;
                }
            }
        }

        public async Task LoadLoras(string loraFolder)
        {
            if (!Directory.Exists(loraFolder)) return;

            // Recursive disk scan — run it off the calling thread (also satisfies the async contract).
            await Task.Run(() =>
            {
                var files = Directory.GetFiles(loraFolder, "*.*", SearchOption.AllDirectories)
                            .Where(f => f.EndsWith(".safetensors") || f.EndsWith(".pt"));

                foreach (var file in files)
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    _loras.Add(new TagItem
                    {
                        Name = name,
                        Category = 9,
                        IsLora = true,
                        TagListName = "Local LoRA"
                    });
                }
            });
        }

        private TagItem CreateTag(string name, int cat, long count, string[] parts, string tagListName) => new TagItem
        {
            Name = name,
            Category = cat,
            Count = count,
            TagListName = tagListName,
            Aliases = parts.Length > 3
                ? parts[3].Trim('"').Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList()
                : null
        };

        // The dictionary structure: ListName -> CategoryID -> [PrimaryColor, SecondaryColor]
        public Dictionary<string, Dictionary<string, string[]>> _colorConfig = new(StringComparer.OrdinalIgnoreCase);

        public void LoadColorConfig(string jsonPath)
        {
            if (!File.Exists(jsonPath))
            {
                Console.WriteLine(jsonPath + " was not found.");
                return;
            }

            var json = File.ReadAllText(jsonPath);
            _colorConfig = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string[]>>>(json) ?? new();
        }

        /// <summary>
        /// Call after loading tags + loras (and after any reload).
        /// </summary>
        public void BuildIndexes()
        {
            BuildAliasIndex();
            BuildGramIndexes();
        }

        public void BuildAliasIndex()
        {
            _aliasToTag.Clear();

            foreach (var t in _tags.Values)
            {
                if (t.Aliases is not { Count: > 0 }) continue;

                foreach (var a in t.Aliases)
                {
                    var alias = a.Trim();
                    if (alias.Length == 0) continue;

                    _aliasToTag.TryAdd(alias, (t, alias));
                }
            }
        }

        private void BuildGramIndexes()
        {
            _tagsByGram.Clear();
            _lorasByGram.Clear();

            foreach (var t in _tags.Values)
                AddToGramIndex(_tagsByGram, t);

            foreach (var l in _loras)
                AddToGramIndex(_lorasByGram, l);
        }

        private static void AddToGramIndex(Dictionary<string, List<TagItem>> index, TagItem item)
        {
            var key = Normalize(item?.Name);
            if (key.Length == 0) return;

            // For very short names (< GramSize), still index them as a whole token
            if (key.Length < GramSize)
            {
                Add(index, key, item);
                return;
            }

            // Add all unique grams from the name
            // (HashSet prevents duplicate adds for repeated patterns)
            HashSet<string> seen = null;

            for (int i = 0; i <= key.Length - GramSize; i++)
            {
                var gram = key.Substring(i, GramSize);
                (seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(gram);
            }

            if (seen == null) return;

            foreach (var gram in seen)
                Add(index, gram, item);
        }

        private static void Add(Dictionary<string, List<TagItem>> index, string gram, TagItem item)
        {
            if (!index.TryGetValue(gram, out var list))
                index[gram] = list = new List<TagItem>(); // grow as needed; most gram buckets are small

            list.Add(item);
        }

        public IEnumerable<TagItem> Search(string term, bool isLoraContext)
        {
            term = (term ?? "").Trim();

            // Allow empty term ONLY for LoRA context (so typing "lora:" shows something)
            if (term.Length == 0)
            {
                if (!isLoraContext) return Enumerable.Empty<TagItem>();
                return GetDefaultLoraSuggestions();
            }

            // Normalize once
            string normTerm = Normalize(term);

            // Top-n results
            int limit = EffectiveMaxSuggestions;
            List<TagItem> top = new(limit);

            // Alias exact-hit fast path (normal tags only)
            if (!isLoraContext && _aliasToTag.TryGetValue(term, out var hit))
            {
                var (tag, matchedAlias) = hit;

                InsertTopN(top, new TagItem
                {
                    TagListName = tag.TagListName,
                    Name = tag.Name,
                    Category = tag.Category,
                    Count = tag.Count,
                    IsLora = tag.IsLora,
                    IsAlias = true,
                    MatchedAlias = matchedAlias,
                    RelevanceScore = 90
                }, EffectiveMaxSuggestions);
            }

            // Candidate selection (substring-friendly)
            IEnumerable<TagItem> candidates = GetCandidates(normTerm, isLoraContext);

            foreach (var t in candidates)
            {
                int score = 0;
                string foundAlias = null;

                // Name scoring (now substring is always meaningful because candidates include substring matches)
                if (t.Name.Equals(term, StringComparison.OrdinalIgnoreCase)) score = 100;
                else if (t.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase)) score = 80;
                else if (t.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) score = 50;

                // Alias scoring (normal tags only)
                if (!isLoraContext)
                {
                    var aliases = t.Aliases;
                    if (aliases is { Count: > 0 })
                    {
                        foreach (var alias in aliases)
                        {
                            int aliasScore = 0;
                            if (alias.Equals(term, StringComparison.OrdinalIgnoreCase)) aliasScore = 90;
                            else if (alias.StartsWith(term, StringComparison.OrdinalIgnoreCase)) aliasScore = 70;
                            else if (alias.Contains(term, StringComparison.OrdinalIgnoreCase)) aliasScore = 40;

                            if (aliasScore > score)
                            {
                                score = aliasScore;
                                foundAlias = alias;
                                if (score == 90) break;
                            }
                        }
                    }
                }

                if (score == 0) continue;

                var candidate = new TagItem
                {
                    TagListName = t.TagListName,
                    Name = t.Name,
                    Category = t.Category,
                    Count = t.Count,
                    IsLora = t.IsLora,
                    IsAlias = foundAlias != null,
                    MatchedAlias = foundAlias,
                    RelevanceScore = score
                };

                // Duplicate guard
                if (top.Any(x => x.Name.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                InsertTopN(top, candidate, EffectiveMaxSuggestions);
            }

            return top;
        }

        private IEnumerable<TagItem> GetCandidates(string normTerm, bool isLoraContext)
        {
            // If term is shorter than GramSize, trigram can’t narrow properly.
            // For short terms, scanning all is often acceptable and avoids false negatives.
            if (normTerm.Length < GramSize)
            {
                return isLoraContext ? _loras : _tags.Values;
            }

            var index = isLoraContext ? _lorasByGram : _tagsByGram;

            // Pick the rarest gram in the term to minimize candidate set
            string bestGram = null;
            int bestCount = int.MaxValue;

            foreach (var gram in EnumerateGrams(normTerm, GramSize))
            {
                if (index.TryGetValue(gram, out var list))
                {
                    if (list.Count < bestCount)
                    {
                        bestCount = list.Count;
                        bestGram = gram;
                        if (bestCount <= 32) break; // early win
                    }
                }
            }

            // If no gram bucket found, fall back to scanning all (still correct)
            if (bestGram == null)
            {
                return isLoraContext ? _loras : _tags.Values;
            }

            return index[bestGram];
        }

        private static IEnumerable<string> EnumerateGrams(string s, int size)
        {
            for (int i = 0; i <= s.Length - size; i++)
                yield return s.Substring(i, size);
        }

        private IEnumerable<TagItem> GetDefaultLoraSuggestions()
        {
            return _loras
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Take(EffectiveMaxSuggestions)
                .Select(x => new TagItem
                {
                    Name = x.Name,
                    Category = x.Category,
                    Count = x.Count,
                    IsLora = x.IsLora,
                    RelevanceScore = 60
                })
                .ToList();
        }

        private static string Normalize(string s)
            => (s ?? "").Trim().ToLowerInvariant();

        private static void InsertTopN(List<TagItem> top, TagItem candidate, int limit)
        {
            int i = 0;
            for (; i < top.Count; i++)
            {
                var cur = top[i];
                if (candidate.RelevanceScore > cur.RelevanceScore ||
                   (candidate.RelevanceScore == cur.RelevanceScore && candidate.Count > cur.Count))
                {
                    break;
                }
            }

            if (i >= limit) return;

            top.Insert(i, candidate);

            if (top.Count > limit)
                top.RemoveAt(limit);
        }

        // --- Tag Favorite and Frequency Tracking ---
        private readonly object _statsLock = new object();
        private HashSet<string> _favorites = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int> _frequencies = new(StringComparer.OrdinalIgnoreCase);
        private bool _statsLoaded = false;

        private string GetStatsFilePath()
        {
            return Path.Combine(Directory.GetCurrentDirectory(), "tag_stats.json");
        }

        public void LoadStats()
        {
            lock (_statsLock)
            {
                if (_statsLoaded) return;
                _statsLoaded = true;
                try
                {
                    var path = GetStatsFilePath();
                    if (File.Exists(path))
                    {
                        var content = File.ReadAllText(path);
                        var data = JsonSerializer.Deserialize<TagStatsData>(content);
                        if (data != null)
                        {
                            if (data.Favorites != null)
                            {
                                _favorites = new HashSet<string>(data.Favorites, StringComparer.OrdinalIgnoreCase);
                            }
                            if (data.Frequencies != null)
                            {
                                _frequencies = new Dictionary<string, int>(data.Frequencies, StringComparer.OrdinalIgnoreCase);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error loading tag stats: {ex.Message}");
                }
            }
        }

        public void SaveStats()
        {
            lock (_statsLock)
            {
                try
                {
                    var path = GetStatsFilePath();
                    var data = new TagStatsData
                    {
                        Favorites = _favorites.ToList(),
                        Frequencies = _frequencies.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase)
                    };
                    var content = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(path, content);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error saving tag stats: {ex.Message}");
                }
            }
        }

        public bool IsFavorite(string tag)
        {
            LoadStats();
            return _favorites.Contains(tag);
        }

        public void AddFavorite(string tag)
        {
            LoadStats();
            if (_favorites.Add(tag))
            {
                SaveStats();
            }
        }

        public void RemoveFavorite(string tag)
        {
            LoadStats();
            if (_favorites.Remove(tag))
            {
                SaveStats();
            }
        }

        public IEnumerable<string> GetFavorites()
        {
            LoadStats();
            return _favorites.ToList();
        }

        // --- Random picks (Surprise Me) ---

        /// <summary>Pick <paramref name="count"/> distinct random tag names. <paramref name="wildness"/>
        /// (0..1) widens the pool: at 0 it's the popular general/character tags (usable); at 1 it's
        /// literally any word in the loaded dictionaries (chaos).</summary>
        public IEnumerable<string> GetRandomTags(int count, Random rng, double wildness = 0)
        {
            if (count <= 0 || _tags.Count == 0) return Array.Empty<string>();
            wildness = Math.Clamp(wildness, 0, 1);

            List<TagItem> pool;
            if (wildness >= 0.999)
            {
                // Max craziness: every tag from every category is fair game.
                pool = _tags.Values.ToList();
            }
            else
            {
                pool = _tags.Values.Where(t => t.Category == 0 || t.Category == 4).ToList();
                if (pool.Count == 0) pool = _tags.Values.ToList();

                // Popularity slice grows from the top ~600 (tame) toward the whole pool (wild).
                pool = pool.OrderByDescending(t => t.Count).ToList();
                int slice = (int)(600 + wildness * pool.Count);
                pool = pool.Take(Math.Max(slice, count)).ToList();
            }

            var picks = new List<string>();
            var used = new HashSet<int>();
            int guard = 0;
            while (picks.Count < count && used.Count < pool.Count && guard++ < count * 40)
            {
                int i = rng.Next(pool.Count);
                if (used.Add(i)) picks.Add(pool[i].Name);
            }
            return picks;
        }

        /// <summary>A random loaded LoRA name, or null if none are loaded.</summary>
        public string? GetRandomLora(Random rng) =>
            _loras.Count == 0 ? null : _loras[rng.Next(_loras.Count)].Name;

        private string CleanTagForFrequency(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return "";
            var clean = tag.Trim();

            // Skip LoRA tags completely
            if (clean.StartsWith("<") && clean.EndsWith(">")) return "";

            // Strip parentheses or brackets used for weight/emphasis
            while (clean.StartsWith("(") || clean.StartsWith("["))
            {
                clean = clean.Substring(1);
            }
            int colonIdx = clean.IndexOf(':');
            if (colonIdx != -1)
            {
                clean = clean.Substring(0, colonIdx);
            }
            while (clean.EndsWith(")") || clean.EndsWith("]"))
            {
                clean = clean.Substring(0, clean.Length - 1);
            }

            return clean.Trim().ToLowerInvariant();
        }

        public void IncrementFrequency(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return;
            var cleanTag = CleanTagForFrequency(tag);
            if (string.IsNullOrEmpty(cleanTag)) return;

            LoadStats();
            lock (_statsLock)
            {
                if (_frequencies.TryGetValue(cleanTag, out var count))
                {
                    _frequencies[cleanTag] = count + 1;
                }
                else
                {
                    _frequencies[cleanTag] = 1;
                }
            }
            SaveStats();
        }

        public void IncrementPromptTags(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt)) return;
            var tags = prompt.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var tag in tags)
            {
                IncrementFrequency(tag);
            }
        }

        public IEnumerable<string> GetFrequentTags(int limit)
        {
            LoadStats();
            return _frequencies
                .OrderByDescending(kvp => kvp.Value)
                .Select(kvp => kvp.Key)
                .Take(limit)
                .ToList();
        }

        public int GetFrequency(string tag)
        {
            LoadStats();
            var cleanTag = CleanTagForFrequency(tag);
            if (string.IsNullOrEmpty(cleanTag)) return 0;
            return _frequencies.TryGetValue(cleanTag, out var count) ? count : 0;
        }

        public void ResetFrequencies()
        {
            LoadStats();
            lock (_statsLock)
            {
                _frequencies.Clear();
            }
            SaveStats();
        }

        public void UpdateFavorites(IEnumerable<string> favorites)
        {
            LoadStats();
            lock (_statsLock)
            {
                _favorites = new HashSet<string>(favorites, StringComparer.OrdinalIgnoreCase);
            }
            SaveStats();
        }

        public void ClearFavorites()
        {
            LoadStats();
            lock (_statsLock)
            {
                _favorites.Clear();
            }
            SaveStats();
        }

        public async Task LoadQuickTags(string folderPath, string fileName = "quick_tags.txt")
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            {
                InitializeDefaultQuickTags();
                return;
            }

            var filePath = Path.Combine(folderPath, fileName);
            if (!File.Exists(filePath))
            {
                var defaultContent = @"[Quality & Style]
masterpiece, best quality, highly detailed, photorealistic, illustration, anime, digital art, concept art, oil painting, sketch

[Subject & Layout]
1girl, 1boy, solo, couple, group, portrait, close up, full body, cowboy shot, upper body

[Environment & Lighting]
outdoors, indoors, night, day, studio lighting, dramatic lighting, cinematic lighting, sunset, sky";
                try
                {
                    await File.WriteAllTextAsync(filePath, defaultContent);
                }
                catch { }
            }

            if (File.Exists(filePath))
            {
                try
                {
                    var content = await File.ReadAllTextAsync(filePath);
                    QuickTags = ParseQuickTags(content);
                }
                catch
                {
                    InitializeDefaultQuickTags();
                }
            }
            else
            {
                InitializeDefaultQuickTags();
            }
        }

        public static Dictionary<string, List<string>> ParseQuickTags(string content)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(content)) return result;

            string currentCategory = null;
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;

                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    currentCategory = trimmed.Substring(1, trimmed.Length - 2).Trim();
                    if (!result.ContainsKey(currentCategory))
                    {
                        result[currentCategory] = new List<string>();
                    }
                }
                else if (currentCategory != null)
                {
                    var tags = trimmed.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var tag in tags)
                    {
                        var cleanTag = tag.Trim();
                        if (!string.IsNullOrEmpty(cleanTag))
                        {
                            result[currentCategory].Add(cleanTag);
                        }
                    }
                }
            }
            return result;
        }

        private void InitializeDefaultQuickTags()
        {
            QuickTags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Quality & Style"] = new() { "masterpiece", "best quality", "highly detailed", "photorealistic", "illustration", "anime", "digital art", "concept art", "oil painting", "sketch" },
                ["Subject & Layout"] = new() { "1girl", "1boy", "solo", "couple", "group", "portrait", "close up", "full body", "cowboy shot", "upper body" },
                ["Environment & Lighting"] = new() { "outdoors", "indoors", "night", "day", "studio lighting", "dramatic lighting", "cinematic lighting", "sunset", "sky" }
            };
        }

        private class TagStatsData
        {
            public List<string> Favorites { get; set; } = new();
            public Dictionary<string, int> Frequencies { get; set; } = new();
        }
    }
}
