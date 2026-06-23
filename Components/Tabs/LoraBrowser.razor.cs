using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Microsoft.JSInterop;
using MudBlazor;
using SimpleDiffusion.Infrastructure;
using System.Text.Json;

namespace SimpleDiffusion.Components.Tabs
{
    public partial class LoraBrowser : IDisposable
    {
        [Inject] IDialogService DialogService { get; set; } = default!;
        [Inject] HttpClient Http { get; set; } = default!;
        [Inject] ISnackbar Snackbar { get; set; } = default!;
        [Inject] UiPreferences Prefs { get; set; } = default!;
        [Inject] CrossTab CrossTab { get; set; } = default!;
        [Inject] LoraStatsService LoraStats { get; set; } = default!;
        [Inject] ControlNetService ControlNet { get; set; } = default!;

        // Optional external input (you can still pass it, but you don't have to)
        [Parameter] public List<LoraModel>? Loras { get; set; }

        // This is the real input we need for disk scan
        [Parameter] public string BaseLoraPath { get; set; } = "";

        [Parameter] public EventCallback<LoraSelection> OnLoraSelected { get; set; }
        [Parameter] public EventCallback<string> OnTagSelected { get; set; }
        [Parameter] public HttpClient SdHttpClient { get; set; } = default!;

        [Inject] private IJSRuntime JS { get; set; } = default!;

        // ===== Internal state =====
        private bool _isLoaded;
        private readonly List<LoraModel> _all = new();
        private List<LoraModel> _filtered = new();

        private string _selectedFolder = "All";
        private string _searchQuery = "";

        // Sorting + "missing info" filters
        public enum LoraSort { Name, Age, FileSize, Type, Usage, Random }
        private LoraSort _sortBy = LoraSort.Name;
        private bool _sortDesc;

        private async Task ToggleSortDir()
        {
            _sortDesc = !_sortDesc;
            RebuildFiltered();
            if (_virtualize != null) await _virtualize.RefreshDataAsync();
        }

        // Stable random ordering: each LoRA path gets a random rank and the list orders by it, so the
        // shuffle stays put across re-renders/scrolling (virtualization-safe) until "Reshuffle".
        private Dictionary<string, int> _shuffleOrder = new(StringComparer.OrdinalIgnoreCase);

        private void AssignShuffleRanks() =>
            _shuffleOrder = _all
                .Where(l => !string.IsNullOrEmpty(l.path))
                .ToDictionary(l => l.path!, _ => Random.Shared.Next(), StringComparer.OrdinalIgnoreCase);

        private int ShuffleRank(string? path) =>
            path != null && _shuffleOrder.TryGetValue(path, out var r) ? r : 0;

        private async Task Reshuffle()
        {
            AssignShuffleRanks();
            RebuildFiltered();
            if (_virtualize != null) await _virtualize.RefreshDataAsync();
            StateHasChanged();
        }
        private bool _missPreview, _missJson, _missTxt, _missHtml;

        private async Task OnSortChanged(LoraSort sort)
        {
            _sortBy = sort;
            if (sort == LoraSort.Random && _shuffleOrder.Count == 0) AssignShuffleRanks();
            RebuildFiltered();
            if (_virtualize != null) await _virtualize.RefreshDataAsync();
        }

        private async Task ToggleMissing(string which, bool on)
        {
            switch (which)
            {
                case "preview": _missPreview = on; break;
                case "json": _missJson = on; break;
                case "txt": _missTxt = on; break;
                case "html": _missHtml = on; break;
            }
            RebuildFiltered();
            if (_virtualize != null) await _virtualize.RefreshDataAsync();
        }

        // ===== Row virtualization =====
        private int _cardWidth = 180;
        private int _cardHeight = 264;
        private const int Gap = 16;
        private int RowHeight => _cardHeight + Gap;
        private int _columns = 11;

        private ElementReference _scrollHost;
        private Virtualize<List<LoraModel>>? _virtualize;

        protected override async Task OnInitializedAsync()
        {
            Prefs.Changed += OnPrefsChanged;
            CrossTab.JumpToLoraRequested += OnJumpToLora;
            LoraStats.EnsureLoaded();
            LoraStats.Changed += OnStatsChanged;
            await LoadFromDiskAsync();
            _isLoaded = true;
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                await Prefs.EnsureLoadedAsync();
                _cardWidth = Prefs.LoraCardWidth; // restore the saved card size
            }
            await RecomputeLayoutAsync(true);
        }

        private CancellationTokenSource? _cardSaveCts;

        private void PersistCardWidthDebounced()
        {
            _cardSaveCts?.Cancel();
            _cardSaveCts = new CancellationTokenSource();
            var tok = _cardSaveCts.Token;
            _ = Task.Run(async () => { try { await Task.Delay(500, tok); await Prefs.PersistAsync(); } catch { } });
        }

        private async Task ResetCardSize()
        {
            _cardWidth = UiPreferences.DefaultLoraCardWidth;
            Prefs.LoraCardWidth = _cardWidth;
            await RecomputeLayoutAsync(refresh: true);
            await Prefs.PersistAsync();
        }

        /// <summary>Search/scroll to a model when navigated here from the Civitai browser.</summary>
        private async void OnJumpToLora(string query)
        {
            await InvokeAsync(async () =>
            {
                // Re-scan disk first — the model may have just been downloaded.
                _searchQuery = query ?? "";
                _selectedFolder = "All";
                await LoadFromDiskAsync();   // rebuilds the filtered list using the new query
                if (_virtualize != null)
                    await _virtualize.RefreshDataAsync();
                StateHasChanged();
            });
        }

        private bool BlurName(LoraModel lora) => Prefs.BlurNames && Prefs.ShouldBlur(LevelOf(lora));

        private async void OnPrefsChanged()
        {
            await InvokeAsync(async () =>
            {
                // Every preference handled here (hide/blur/name/reveal) is a pure in-memory filter
                // over already-scanned data — the rating comes from LoraModel.nsfwLevel + manual
                // overrides, neither of which is on disk — so re-filtering is enough; no disk read.
                RebuildFiltered();

                if (_virtualize != null)
                    await _virtualize.RefreshDataAsync();
                StateHasChanged();
            });
        }

        private async Task OnCardWidthChanged(int newWidth)
        {
            _cardWidth = newWidth;
            Prefs.LoraCardWidth = newWidth;
            await RecomputeLayoutAsync(refresh: true);
            PersistCardWidthDebounced();
        }

        protected override Task OnParametersSetAsync()
        {
            if (!_isLoaded)
                return Task.CompletedTask;

            if (Loras is { Count: > 0 } && !ReferenceEquals(Loras, _all))
            {
                _all.Clear();
                _all.AddRange(Loras);
                RebuildFiltered();
            }
            return Task.CompletedTask;
        }

        private async Task LoadFromDiskAsync()
        {
            _isLoaded = false;
            await InvokeAsync(StateHasChanged);

            var path = BaseLoraPath;

            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                _all.Clear();
                _filtered.Clear();
                _isLoaded = true;
                await InvokeAsync(StateHasChanged);
                return;
            }

            var list = await Task.Run(() => ScanLoras(path));

            _all.Clear();
            _all.AddRange(list);
            RebuildFolderCounts();
            RebuildBaseModelOptions();
            if (_sortBy == LoraSort.Random) AssignShuffleRanks(); // give newly-scanned items a rank

            // Build only the root level initially
            var rootFolders = GetImmediateSubfolders(BaseLoraPath);

            var root = new FolderNode
            {
                Name = "All",
                FullPath = BaseLoraPath,
                Children = rootFolders,
                ChildrenLoaded = true,
                HasChildren = rootFolders.Any()
            };

            var rootTreeItem = new TreeItemData<FolderNode>
            {
                Value = root,
                Text = root.Name,
                Icon = Icons.Material.Filled.FolderSpecial,
                Expanded = true,
                Children = BuildTreeItemsListLazy(root.Children)
            };

            _folderTree = new HashSet<TreeItemData<FolderNode>> { rootTreeItem };
            _selectedFolder = "All";

            RebuildFiltered();

            _isLoaded = true;
            await InvokeAsync(StateHasChanged);
        }

        private FolderNode? _selectedTreeItem;
        private IReadOnlyCollection<FolderNode>? _selectedTreeItems;

        private async Task RecomputeLayoutAsync(bool refresh)
        {
            var oldCols = _columns;
            var oldHeight = _cardHeight;

            // height = width * 16/9 (9:16 vertical poster)
            _cardHeight = (int)Math.Round(_cardWidth * (16.0 / 9.0));
            _cardHeight = Math.Clamp(_cardHeight, 130, 1100);

            // columns depends on container width and chosen card width
            _columns = await MeasureColumnsAsync(_scrollHost, _cardWidth, Gap);

            bool changed = (_columns != oldCols) || (_cardHeight != oldHeight);

            if (refresh && changed && _virtualize != null)
                await _virtualize.RefreshDataAsync();

            if (changed)
            {
                await InvokeAsync(StateHasChanged);
            }
        }

        private async Task<int> MeasureColumnsAsync(ElementReference host, int cardWidth, int gap)
        {
            try
            {
                var width = await JS.InvokeAsync<double>("loraGridMeasure.getWidth", host);
                if (width <= 0) return _columns;

                // floor((width + gap) / (cardWidth + gap))
                var cols = (int)Math.Floor((width + gap) / (cardWidth + gap));
                return Math.Max(1, cols);
            }
            catch
            {
                return _columns;
            }
        }

        // Parsed .civitai.json sidecars cached across reloads (init, jump, prefs change, manual
        // refresh, post-dialog), keyed by full path and invalidated per-file when its size or
        // last-write time changes. Lets unchanged sidecars skip the open + JSON parse — the
        // dominant cost of a rescan on a large library. ConcurrentDictionary because ScanLoras
        // runs on a background thread and reloads can overlap.
        private readonly record struct SidecarInfo(int Level, string? Type, string? Triggers, string? BaseModel);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Size, long Mtime, SidecarInfo Data)> _sidecarCache
            = new(StringComparer.OrdinalIgnoreCase);

        private List<LoraModel> ScanLoras(string basePath)
        {
            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".safetensors", ".pt", ".ckpt"
            };

            // List every file once and group by directory, so we don't re-enumerate a folder for
            // each model/preview/sidecar lookup (matters a lot with thousands of LoRAs). Enumerate
            // via DirectoryInfo so each FileInfo already carries size/mtime gathered during the
            // walk — no extra per-file stat for the sidecar key or the model's size/date.
            var byDir = new Dictionary<string, List<FileInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (var fi in new DirectoryInfo(basePath).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                var d = fi.DirectoryName ?? "";
                if (!byDir.TryGetValue(d, out var bucket)) { bucket = new(); byDir[d] = bucket; }
                bucket.Add(fi);
            }

            var result = new List<LoraModel>();
            foreach (var (dir, files) in byDir)
            {
                // Precompute this folder's lookups once.
                var byName = new Dictionary<string, FileInfo>(files.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var fi in files) byName[fi.Name] = fi;

                var images = files
                    .Where(f => ImgExts.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
                    .Select(f => { var norm = NormalizeName(Path.GetFileNameWithoutExtension(f.Name)); return new ImageCandidate(f.FullName, f.Name, norm, Tokenize(norm)); })
                    .ToList();

                foreach (var fi in files)
                {
                    if (!exts.Contains(fi.Extension)) continue;
                    var baseName = Path.GetFileNameWithoutExtension(fi.Name);
                    byName.TryGetValue(baseName + ".civitai.json", out var jsonFi);
                    var (level, type, triggers, baseModel) = ReadSidecar(jsonFi);
                    var preview = FindPreviewFromList(baseName, images);
                    var info = SafeFileInfo(fi);
                    result.Add(new LoraModel
                    {
                        path = fi.FullName,
                        name = baseName,
                        previewPath = preview,
                        nsfwLevel = level,
                        type = type,
                        triggerWords = triggers,
                        baseModel = baseModel,
                        hasPreview = preview != null,
                        hasJson = jsonFi != null,
                        hasTxt = byName.ContainsKey(baseName + ".txt"),
                        hasHtml = byName.ContainsKey(baseName + ".html"),
                        modified = info.LastWriteUtc,
                        sizeBytes = info.Size
                    });
                }
            }
            return result;
        }

        private readonly record struct ImageCandidate(string Path, string FileName, string Norm, HashSet<string> Tokens);

        private static (DateTime LastWriteUtc, long Size) SafeFileInfo(FileInfo fi)
        {
            try { return (fi.LastWriteTimeUtc, fi.Length); }
            catch { return (DateTime.MinValue, 0); }
        }

        /// <summary>Read nsfwLevel, model type, and trigger words from the .civitai.json sidecar
        /// (cached by path + size + mtime so unchanged files aren't re-parsed).</summary>
        private (int Level, string? Type, string? Triggers, string? BaseModel) ReadSidecar(FileInfo? jsonFi)
        {
            if (jsonFi is null) return (0, null, null, null);
            try
            {
                long size = jsonFi.Length, mtime = jsonFi.LastWriteTimeUtc.Ticks;
                if (_sidecarCache.TryGetValue(jsonFi.FullName, out var cached)
                    && cached.Size == size && cached.Mtime == mtime)
                    return (cached.Data.Level, cached.Data.Type, cached.Data.Triggers, cached.Data.BaseModel);

                using var s = jsonFi.OpenRead();
                using var doc = JsonDocument.Parse(s);
                var root = doc.RootElement;

                var level = root.TryGetProperty("nsfwLevel", out var lv) && lv.ValueKind == JsonValueKind.Number
                            && lv.TryGetInt32(out var lvl) ? lvl : 0;

                string? type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()
                    : null;

                // Trigger words + base model live on the (first) model version.
                string? triggers = null;
                string? baseModel = null;
                if (root.TryGetProperty("modelVersions", out var mvs) && mvs.ValueKind == JsonValueKind.Array
                    && mvs.GetArrayLength() > 0)
                {
                    var mv = mvs[0];
                    if (mv.TryGetProperty("baseModel", out var bm) && bm.ValueKind == JsonValueKind.String)
                        baseModel = bm.GetString();

                    if (mv.TryGetProperty("trainedWords", out var tw) && tw.ValueKind == JsonValueKind.Array)
                    {
                        var words = tw.EnumerateArray()
                            .Where(x => x.ValueKind == JsonValueKind.String)
                            .Select(x => x.GetString())
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .ToList();
                        if (words.Count > 0) triggers = string.Join(", ", words);
                    }
                }

                _sidecarCache[jsonFi.FullName] = (size, mtime, new SidecarInfo(level, type, triggers, baseModel));
                return (level, type, triggers, baseModel);
            }
            catch { return (0, null, null, null); }
        }

        /// <summary>Effective rating for a LoRA: a manual override if set, else the sidecar level.</summary>
        private int? LevelOf(LoraModel lora) =>
            Prefs.EffectiveLevel($"lora:{lora.name}", lora.nsfwLevel);

        private bool Blur(LoraModel lora) => Prefs.ShouldBlur(LevelOf(lora));

        /// <summary>How many model files share each folder (the count of downloaded versions).</summary>
        private Dictionary<string, int> _folderVersionCounts = new(StringComparer.OrdinalIgnoreCase);

        private int VersionsInFolder(string loraPath)
        {
            var dir = Path.GetDirectoryName(loraPath);
            return dir is not null && _folderVersionCounts.TryGetValue(dir, out var n) ? n : 1;
        }

        private void RebuildFolderCounts()
        {
            _folderVersionCounts = _all
                .Select(l => Path.GetDirectoryName(l.path))
                .Where(d => !string.IsNullOrEmpty(d))
                .GroupBy(d => d!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            Prefs.Changed -= OnPrefsChanged;
            CrossTab.JumpToLoraRequested -= OnJumpToLora;
            LoraStats.Changed -= OnStatsChanged;
            _searchCts?.Cancel();
            _cardSaveCts?.Cancel();
        }

        private async void OnStatsChanged()
        {
            await InvokeAsync(async () =>
            {
                RebuildFiltered(); // favourites filter / most-used order may have changed
                if (_virtualize != null) await _virtualize.RefreshDataAsync();
                StateHasChanged();
            });
        }

        // Adds the LoRA to the prompt and counts it as "used".
        private async Task AddLora(LoraModel lora)
        {
            LoraStats.IncrementUsage(lora.name);
            await OnLoraSelected.InvokeAsync(new LoraSelection(lora.name, lora.triggerWords));
        }

        private bool IsFavorite(LoraModel lora) => LoraStats.IsFavorite(lora.name);
        private void ToggleFavorite(LoraModel lora) => LoraStats.ToggleFavorite(lora.name);

        private bool _favoritesOnly;
        private async Task ToggleFavoritesOnly()
        {
            _favoritesOnly = !_favoritesOnly;
            RebuildFiltered();
            if (_virtualize != null) await _virtualize.RefreshDataAsync();
        }

        // Base-model compatibility filter (multi-select). Options are the distinct base models
        // actually present in the library; empty selection = show everything.
        private List<string> _availableBaseModels = new();
        private IEnumerable<string> _selectedBaseModels = new List<string>();
        private HashSet<string> _baseModelFilter = new(StringComparer.OrdinalIgnoreCase);

        private void RebuildBaseModelOptions() =>
            _availableBaseModels = _all
                .Select(l => l.baseModel)
                .Where(b => !string.IsNullOrWhiteSpace(b))
                .Select(b => b!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(b => b, StringComparer.OrdinalIgnoreCase)
                .ToList();

        private async Task OnBaseModelsChanged(IEnumerable<string> values)
        {
            _selectedBaseModels = values?.ToList() ?? new List<string>();
            _baseModelFilter = new HashSet<string>(_selectedBaseModels, StringComparer.OrdinalIgnoreCase);
            RebuildFiltered();
            if (_virtualize != null) await _virtualize.RefreshDataAsync();
        }

        private void RebuildFiltered()
        {
            var q = _searchQuery?.Trim();

            var query = _all.Where(l =>
                    !string.IsNullOrEmpty(l.path) &&
                    !Prefs.ShouldHide(LevelOf(l)) &&
                    (string.IsNullOrEmpty(q) || (l.name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)) &&
                    (_selectedFolder == "All" || IsInSelectedFolder(l.path)) &&
                    (!_favoritesOnly || LoraStats.IsFavorite(l.name)) &&
                    (_baseModelFilter.Count == 0 || (l.baseModel != null && _baseModelFilter.Contains(l.baseModel))) &&
                    (!_missPreview || !l.hasPreview) &&
                    (!_missJson || !l.hasJson) &&
                    (!_missTxt || !l.hasTxt) &&
                    (!_missHtml || !l.hasHtml));

            // Each sort has a "normal" direction; the reverse toggle flips the final list.
            IEnumerable<LoraModel> ordered = _sortBy switch
            {
                LoraSort.Age => query.OrderByDescending(l => l.modified),                                  // newest first
                LoraSort.FileSize => query.OrderBy(l => l.sizeBytes),                                      // smallest first (reverse = largest)
                LoraSort.Type => query.OrderBy(l => l.type ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(l => l.name, StringComparer.OrdinalIgnoreCase),
                LoraSort.Usage => query.OrderByDescending(l => LoraStats.GetUsage(l.name)).ThenBy(l => l.name, StringComparer.OrdinalIgnoreCase),
                LoraSort.Random => query.OrderBy(l => ShuffleRank(l.path)),
                _ => query.OrderBy(l => l.name, StringComparer.OrdinalIgnoreCase)                          // Name A→Z
            };

            var list = ordered.ToList();
            if (_sortDesc) list.Reverse();
            _filtered = list;
        }

        private bool IsInSelectedFolder(string fullPath)
        {
            if (string.IsNullOrEmpty(BaseLoraPath)) return true;

            var dir = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(dir)) return false;

            var rel = Path.GetRelativePath(BaseLoraPath, dir).Replace('\\', '/');
            var selected = _selectedFolder.Replace('\\', '/');

            return rel.Equals(selected, StringComparison.OrdinalIgnoreCase)
                   || rel.StartsWith(selected + "/", StringComparison.OrdinalIgnoreCase);
        }

        // Row virtualization: Virtualize rows, each row has _columns items
        private ValueTask<ItemsProviderResult<List<LoraModel>>> LoadRows(ItemsProviderRequest request)
        {
            if (_filtered.Count == 0)
                return ValueTask.FromResult(new ItemsProviderResult<List<LoraModel>>(Array.Empty<List<LoraModel>>(), 0));

            var cols = Math.Max(1, _columns);
            var totalRows = (int)Math.Ceiling(_filtered.Count / (double)cols);

            if (request.StartIndex >= totalRows)
                return ValueTask.FromResult(new ItemsProviderResult<List<LoraModel>>(Array.Empty<List<LoraModel>>(), totalRows));

            var rowCount = Math.Min(request.Count, totalRows - request.StartIndex);

            var rows = new List<List<LoraModel>>(rowCount);
            for (int r = 0; r < rowCount; r++)
            {
                var rowIndex = request.StartIndex + r;
                var start = rowIndex * cols;
                var count = Math.Min(cols, _filtered.Count - start);
                rows.Add(_filtered.GetRange(start, count));
            }

            return ValueTask.FromResult(new ItemsProviderResult<List<LoraModel>>(rows, totalRows));
        }

        private string GetPreviewUrlFromDisk(string? loraPath)
        {
            if (string.IsNullOrEmpty(loraPath))
                return "images/no_preview.png";

            return $"/lora-preview?loraPath={Uri.EscapeDataString(loraPath)}";
        }

        /// <summary>
        /// Card preview URL using the preview path resolved during the scan — serves the file
        /// directly, so the server doesn't re-scan the folder for every visible card.
        /// </summary>
        private string PreviewUrl(LoraModel lora)
        {
            if (string.IsNullOrEmpty(lora.previewPath))
                return "images/card-no-preview.png";
            return $"/lora-preview-file?filePath={Uri.EscapeDataString(lora.previewPath)}";
        }

        private static readonly string[] ImgExts = [".webp", ".png", ".jpg", ".jpeg", ".webm", ".mp4"];

        /// <summary>Match a preview for a LoRA against a folder's pre-listed image candidates (no disk I/O).</summary>
        private static string? FindPreviewFromList(string loraBase, List<ImageCandidate> images)
        {
            if (images.Count == 0) return null;

            // 1) Exact base name (base.ext), in our preferred extension order.
            foreach (var ext in ImgExts)
            {
                var name = loraBase + ext;
                foreach (var c in images)
                    if (c.FileName.Equals(name, StringComparison.OrdinalIgnoreCase)) return c.Path;
            }

            // 2) Underscore -> space variant.
            var spaced = loraBase.Replace('_', ' ');
            foreach (var ext in ImgExts)
            {
                var name = spaced + ext;
                foreach (var c in images)
                    if (c.FileName.Equals(name, StringComparison.OrdinalIgnoreCase)) return c.Path;
            }

            // 3) Normalized matching (exact, then best overlap).
            var key = NormalizeName(loraBase);
            foreach (var c in images)
                if (c.Norm == key) return c.Path;

            // Each image's token set is precomputed once per folder; only the key is tokenized here.
            var keyTokens = Tokenize(key);
            if (keyTokens.Count == 0) return null;

            string? bestPath = null;
            double bestScore = 0;
            foreach (var c in images)
            {
                var score = JaccardScore(keyTokens, c.Tokens);
                if (score > bestScore) { bestScore = score; bestPath = c.Path; }
            }
            return bestScore >= 0.75 ? bestPath : null;
        }

        private static string NormalizeName(string s)
        {
            // Lowercase, remove punctuation except letters/numbers, collapse whitespace
            var chars = s.ToLowerInvariant()
                .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')
                .ToArray();

            var cleaned = new string(chars);
            while (cleaned.Contains("  "))
                cleaned = cleaned.Replace("  ", " ");

            return cleaned.Trim();
        }

        private static HashSet<string> Tokenize(string norm) =>
            norm.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        // Jaccard overlap (0..1) over two precomputed token sets — no per-call splitting/allocation.
        private static double JaccardScore(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0 || b.Count == 0) return 0;

            int intersect = 0;
            // Iterate the smaller set for the membership tests.
            var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
            foreach (var t in small)
                if (large.Contains(t)) intersect++;

            int union = a.Count + b.Count - intersect;
            return union == 0 ? 0 : (double)intersect / union;
        }

        private CancellationTokenSource? _searchCts;

        private void OnSearchChanged(string value)
        {
            // Update the bound value immediately so the text field stays responsive, but debounce
            // the expensive part (full filter+sort over the whole library + a Virtualize refresh)
            // so a fast typist doesn't trigger it on every keystroke.
            _searchQuery = value ?? "";

            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var tok = _searchCts.Token;
            _ = InvokeAsync(async () =>
            {
                try { await Task.Delay(200, tok); }
                catch (TaskCanceledException) { return; }

                RebuildFiltered();
                if (_virtualize != null)
                    await _virtualize.RefreshDataAsync();
                StateHasChanged();
            });
        }

        private async Task OpenMetaDialog(LoraModel lora)
        {
            // The file may have been deleted/moved on disk since the grid was built.
            if (string.IsNullOrEmpty(lora.path) || !File.Exists(lora.path))
            {
                Snackbar.Add("That model file no longer exists on disk. Reloading…", Severity.Warning);
                await LoadFromDiskAsync();
                return;
            }

            // Open immediately; the dialog fetches its metadata/previews itself (no click lag).
            var parameters = new DialogParameters
            {
                ["Lora"] = lora,
                ["SdHttpClient"] = SdHttpClient
            };

            var options = new DialogOptions
            {
                CloseButton = true,
                CloseOnEscapeKey = true,
                BackdropClick = true,
                MaxWidth = MaxWidth.ExtraLarge,
                FullWidth = true
            };

            var dialog = await DialogService.ShowAsync<LoraMetaDialog>("LoRA details", parameters, options);
            var result = await dialog.Result;
            if (result != null && !result.Canceled)
            {
                if (result.Data is string s && (s == "deleted" || s == "refreshed"))
                {
                    // The dialog deleted versions or refreshed metadata — re-scan from disk.
                    await LoadFromDiskAsync();
                    if (_virtualize != null)
                        await _virtualize.RefreshDataAsync();
                    if (s == "deleted") Snackbar.Add("Deleted.", Severity.Success);
                }
                else if (result.Data is LoraVersionSwitch vs)
                {
                    // Reopen the dialog for the chosen version (reuses the normal load path).
                    var target = _all.FirstOrDefault(l => string.Equals(l.path, vs.Path, StringComparison.OrdinalIgnoreCase))
                                 ?? new LoraModel { path = vs.Path, name = Path.GetFileNameWithoutExtension(vs.Path) };
                    await OpenMetaDialog(target);
                }
            }
        }

        /// <summary>Find LoRAs without a .civitai.json, let the user pick, then fetch from Civitai.</summary>
        private async Task OpenFetchMetadata()
        {
            var candidates = _all
                .Where(l => !l.hasJson && !string.IsNullOrEmpty(l.path))
                .OrderBy(l => l.name, StringComparer.OrdinalIgnoreCase)
                .Select(l => new LoraMetadataFetchDialog.FetchItem { Name = l.name, Path = l.path })
                .ToList();

            if (candidates.Count == 0)
            {
                Snackbar.Add("Every LoRA already has a .civitai.json.", Severity.Info);
                return;
            }

            var parameters = new DialogParameters<LoraMetadataFetchDialog> { { x => x.Items, candidates } };
            var options = new DialogOptions { CloseOnEscapeKey = true, MaxWidth = MaxWidth.Small, FullWidth = true };
            var dlg = await DialogService.ShowAsync<LoraMetadataFetchDialog>("Fetch missing metadata", parameters, options);
            var res = await dlg.Result;
            if (res is not null && !res.Canceled)
                await ReloadModels(); // reflect the newly-written sidecars
        }

        /// <summary>Re-scan the LoRA folder from disk.</summary>
        private async Task ReloadModels()
        {
            // Also drop the ControlNet discovery cache so freshly-downloaded CN models show up in the
            // ControlNet panel without an app restart (open panels reload via ControlNetService.Changed).
            ControlNet.Invalidate();
            await LoadFromDiskAsync();
            if (_virtualize != null)
                await _virtualize.RefreshDataAsync();
        }

        bool _foldersOpen = false;

        public class FolderNode
        {
            public string Name { get; set; } = "";
            public string FullPath { get; set; } = "";
            public List<FolderNode> Children { get; set; } = new();
            public bool ChildrenLoaded { get; set; } = false;
            public bool HasChildren { get; set; } = false; // Pre-compute this
        }

        private HashSet<TreeItemData<FolderNode>> _folderTree = new HashSet<TreeItemData<FolderNode>>();

        private List<TreeItemData<FolderNode>> BuildTreeItemsList(List<FolderNode> nodes)
        {
            return nodes.Select(n => new TreeItemData<FolderNode>
            {
                Value = n,
                Text = n.Name,
                Icon = Icons.Material.Filled.Folder,
                Expanded = false,
                Children = BuildTreeItemsList(n.Children)  // Recursive call
            }).ToList();
        }


        private async Task OnFolderSelected(TreeItemData<FolderNode> item)
        {
            var node = item.Value;

            if (node == null)
                return;

            if (node.FullPath == BaseLoraPath)
                _selectedFolder = "All";
            else
                _selectedFolder = Path.GetRelativePath(BaseLoraPath, node.FullPath);

            RebuildFiltered();

            if (_virtualize != null)
                await _virtualize.RefreshDataAsync();

            await InvokeAsync(StateHasChanged);
        }

        private List<FolderNode> GetImmediateSubfolders(string parentPath)
        {
            try
            {
                return Directory.GetDirectories(parentPath)
                    .Select(dir => new FolderNode
                    {
                        Name = Path.GetFileName(dir),
                        FullPath = dir,
                        Children = new List<FolderNode>(),
                        ChildrenLoaded = false,
                        HasChildren = Directory.EnumerateDirectories(dir).Any() // stop at the first subdir instead of materializing all
                    })
                    .OrderBy(f => f.Name)
                    .ToList();
            }
            catch
            {
                return new List<FolderNode>();
            }
        }

        private List<TreeItemData<FolderNode>> BuildTreeItemsListLazy(List<FolderNode> nodes)
        {
            return nodes.Select(n => new TreeItemData<FolderNode>
            {
                Value = n,
                Text = n.Name,
                Icon = Icons.Material.Filled.Folder,
                Expanded = false,
                Children = n.ChildrenLoaded
                    ? BuildTreeItemsListLazy(n.Children)
                    : new List<TreeItemData<FolderNode>>() // Empty initially
            }).ToList();
        }

        private async Task OnNodeExpanded(TreeItemData<FolderNode> item, bool expanded)
        {
            // Update the expanded state
            item.Expanded = expanded;

            if (!expanded || item.Value == null || item.Value.ChildrenLoaded)
            {
                await InvokeAsync(StateHasChanged);
                return;
            }

            // Lazy load children
            var node = item.Value;
            node.Children = GetImmediateSubfolders(node.FullPath);
            node.ChildrenLoaded = true;

            // Rebuild the tree item's children
            item.Children = BuildTreeItemsListLazy(node.Children);

            await InvokeAsync(StateHasChanged);
        }

        private List<TreeItemData<FolderNode>>? GetTreeItemChildren(TreeItemData<FolderNode> item)
        {
            if (item.Value == null)
                return null;

            // If children are loaded, return them (or null if empty)
            if (item.Value.ChildrenLoaded)
            {
                return item.Children?.Any() == true ? item.Children : null;
            }

            // If folder has children but not loaded yet, return a dummy item to show expand arrow
            if (item.Value.HasChildren)
            {
                // Return a single placeholder item
                return new List<TreeItemData<FolderNode>>
                {
                    new TreeItemData<FolderNode>
                    {
                        Value = new FolderNode { Name = "Loading...", FullPath = "" },
                        Text = "Loading...",
                        Icon = Icons.Material.Filled.HourglassEmpty
                    }
                };
            }

            // No children
            return null;
        }

    }
}
