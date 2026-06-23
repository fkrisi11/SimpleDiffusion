using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace SimpleDiffusion.Components
{
    public partial class PromptRearrangeBoard : IAsyncDisposable
    {
        [Parameter] public string Prompt { get; set; } = "";
        [Parameter] public EventCallback<string> OnPromptChanged { get; set; }

        private PromptNode _root = new() { IsGroup = true, Id = "root" };
        private string _lastPrompt = "";
        private readonly Dictionary<string, PromptNode> _nodesById = new();

        private ElementReference _containerRef;
        private DotNetObjectReference<PromptRearrangeBoard>? _selfRef;
        private bool _jsInitialized;

        protected override void OnParametersSet()
        {
            if (Prompt != _lastPrompt)
            {
                _root = PromptTokenizer.Parse(Prompt);
                _lastPrompt = Prompt;
                AssignIds();
            }
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (!firstRender || _jsInitialized) return;
            _jsInitialized = true;
            _selfRef = DotNetObjectReference.Create(this);
            await JS.InvokeVoidAsync("sdRearrange.init", _containerRef, _selfRef);
        }

        /// <summary>Give every node a stable id and index them so JS drops can resolve back to a node.</summary>
        private void AssignIds()
        {
            _nodesById.Clear();
            if (string.IsNullOrEmpty(_root.Id)) _root.Id = "root";
            _nodesById[_root.Id] = _root;
            int counter = 0;
            AssignIds(_root, ref counter);
        }

        private void AssignIds(PromptNode node, ref int counter)
        {
            foreach (var child in node.Children)
            {
                child.Id = "n" + counter++;
                _nodesById[child.Id] = child;
                if (child.IsGroup) AssignIds(child, ref counter);
            }
        }

        // --- Drag & drop (invoked from the JS pointer engine) ---

        [JSInvokable]
        public async Task DropNode(string dragId, string parentId, int index)
        {
            if (_nodesById.TryGetValue(dragId, out var node) &&
                _nodesById.TryGetValue(parentId, out var parent))
            {
                await MoveTo(node, parent, index);
                StateHasChanged();
            }
        }

        /// <summary>Move <paramref name="node"/> to <paramref name="newParent"/> at <paramref name="index"/>.</summary>
        private async Task MoveTo(PromptNode node, PromptNode newParent, int index)
        {
            // Can't drop a node into itself or one of its own descendants.
            if (ReferenceEquals(node, newParent) || IsDescendant(node, newParent)) return;

            var oldParent = node.Parent;
            if (oldParent == null) return;

            int oldIndex = oldParent.Children.IndexOf(node);
            if (oldIndex < 0) return;

            oldParent.Children.RemoveAt(oldIndex);

            // If reinserting later within the same parent, account for the removal shift.
            if (ReferenceEquals(oldParent, newParent) && oldIndex < index) index--;
            index = Math.Clamp(index, 0, newParent.Children.Count);

            newParent.Children.Insert(index, node);
            node.Parent = newParent;

            // Drop a group that is now empty.
            if (!ReferenceEquals(oldParent, newParent) && oldParent.Children.Count == 0 && oldParent.Parent != null)
            {
                oldParent.Parent.Children.Remove(oldParent);
            }

            await RebuildPrompt();
        }

        private static bool IsDescendant(PromptNode ancestor, PromptNode maybeChild)
        {
            var p = maybeChild.Parent;
            while (p != null)
            {
                if (ReferenceEquals(p, ancestor)) return true;
                p = p.Parent;
            }
            return false;
        }

        // --- Weight ---

        public async Task AdjustEmphasis(PromptNode node, int delta)
        {
            if (node.IsNetworkTag) return;

            double current = node.ExplicitWeight ?? 1.0;
            double newWeight = Math.Round(current + delta * 0.1, 2);
            newWeight = Math.Clamp(newWeight, 0.0, 2.0);

            ApplyWeight(node, newWeight);
            await RebuildPrompt();
        }

        public async Task OpenWeightDialog(PromptNode node)
        {
            if (node.IsNetworkTag) return;

            double currentWeight = node.ExplicitWeight ?? 1.0;

            var parameters = new DialogParameters<WeightDialog>();
            parameters.Add(x => x.TagName, node.IsGroup ? "group" : node.Text);
            parameters.Add(x => x.CurrentWeight, currentWeight);

            var options = new DialogOptions { CloseOnEscapeKey = true, MaxWidth = MaxWidth.Small, FullWidth = true };
            var dialog = await DialogService.ShowAsync<WeightDialog>("Adjust Tag Weight", parameters, options);
            var result = await dialog.Result;

            if (result != null && !result.Canceled && result.Data is double newWeight)
            {
                ApplyWeight(node, Math.Clamp(newWeight, 0.0, 2.0));
                await RebuildPrompt();
            }
        }

        private static void ApplyWeight(PromptNode node, double newWeight)
        {
            if (Math.Abs(newWeight - 1.0) < 0.001)
            {
                // Back to neutral: drop the weight and any emphasis wrapping (no brackets).
                node.ExplicitWeight = null;
                if (node.ParenDepth < 0) node.ParenDepth = 0;
                if (!node.IsGroup) node.ParenDepth = 0;
            }
            else
            {
                node.ExplicitWeight = newWeight;
                if (node.ParenDepth < 1) node.ParenDepth = 1;
            }
        }

        private async Task RebuildPrompt()
        {
            Prompt = PromptTokenizer.Reconstruct(_root);
            _lastPrompt = Prompt;
            await OnPromptChanged.InvokeAsync(Prompt);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await JS.InvokeVoidAsync("sdRearrange.detach", _containerRef);
            }
            catch { /* circuit/JS may already be gone */ }
            _selfRef?.Dispose();
        }
    }
}
