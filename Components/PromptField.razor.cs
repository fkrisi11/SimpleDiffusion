using Microsoft.JSInterop;
using System;

namespace SimpleDiffusion.Components
{
    public partial class PromptField : IDisposable
    {
        private DotNetObjectReference<PromptField>? _dotNetRef;
        private bool _keysBound = false;

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                // Needed for both desktop key-nav and the mobile ribbon's tap callback.
                _dotNetRef ??= DotNetObjectReference.Create(this);
            }

            // Ribbon mode (mobile) uses tap-to-insert, so we don't intercept Enter/Tab/arrows —
            // that keeps the soft keyboard's Enter inserting newlines. Mobile detection only
            // resolves after the first render, so bind/unbind reactively rather than once.
            if (UseRibbon)
            {
                if (_keysBound)
                {
                    try { await JS.InvokeVoidAsync("promptHelper.unbindAutocompleteKeys", _container); } catch { }
                    _keysBound = false;
                }
                return;
            }

            if (!_keysBound && _dotNetRef != null)
            {
                await JS.InvokeVoidAsync("promptHelper.bindAutocompleteKeys", _container, _dotNetRef);
                _keysBound = true;
            }
        }

        // Invoked from the body-level mobile suggestion ribbon when a chip is tapped.
        [JSInvokable]
        public Task ApplyRibbonTag(int index)
        {
            return InvokeAsync(async () =>
            {
                if (index >= 0 && index < _suggestions.Count)
                {
                    await ApplyTag(_suggestions[index]);
                }
            });
        }

        public void Dispose()
        {
            try
            {
                _ = JS.InvokeVoidAsync("promptHelper.unbindAutocompleteKeys", _container);
            }
            catch { }
            _dotNetRef?.Dispose();
        }

        [JSInvokable]
        public Task OnAutocompleteKey(string key)
        {
            // Always hop to Blazor renderer context
            return InvokeAsync(async () =>
            {
                if (!_isOpen) return;

                if (key == "ArrowDown")
                {
                    if (_suggestions.Count > 0)
                    {
                        _selectedIndex = (_selectedIndex + 1) % _suggestions.Count;
                        StateHasChanged();
                        await ScrollSelectedIntoView();
                    }
                }
                else if (key == "ArrowUp")
                {
                    if (_suggestions.Count > 0)
                    {
                        _selectedIndex = (_selectedIndex - 1 + _suggestions.Count) % _suggestions.Count;
                        StateHasChanged();
                        await ScrollSelectedIntoView();
                    }
                }
                else if (key == "Enter" || key == "Tab")
                {
                    if (_suggestions.Count > 0 &&
                        _selectedIndex >= 0 &&
                        _selectedIndex < _suggestions.Count)
                    {
                        // Capture selection before awaiting
                        var chosen = _suggestions[_selectedIndex];
                        //Console.WriteLine($"[AC] key={key} open={_isOpen} sel={_selectedIndex} count={_suggestions.Count}");
                        await ApplyTag(chosen);
                    }
                }
                else if (key == "Escape")
                {
                    _isOpen = false;
                    StateHasChanged();
                }
            });
        }

    }
}
