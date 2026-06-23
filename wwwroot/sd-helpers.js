window.downloadUrl = (url, fileName) => {
    // Browsers that ignore the <a download> attribute (notably Firefox on Android) derive the saved
    // name from the URL instead — which for "/gallery/file", "/results/file" or "/gallery/zip/<id>"
    // is just "file" / the guid. Pass the name to our own endpoints via ?dl= so they can emit a
    // Content-Disposition filename those browsers honour. Only for same-origin (relative) URLs.
    let href = url;
    if (fileName && url && url.startsWith('/')) {
        href += (url.includes('?') ? '&' : '?') + 'dl=' + encodeURIComponent(fileName);
    }
    const link = document.createElement('a');
    link.href = href;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
};

// Download raw base64 as-is, with no canvas re-encode — for saving a PNG that's already a PNG.
// (downloadConvertedFile re-encodes via a canvas, which is brutally slow and can exceed canvas size
// limits for very large images like 16k upscales.)
window.sdDownloadBase64 = (data, mime, fileName) => {
    try {
        let b64 = data || "";
        const c = b64.indexOf(',');
        if (b64.startsWith('data:') && c >= 0) b64 = b64.substring(c + 1);
        const bin = atob(b64);
        const bytes = new Uint8Array(bin.length);
        for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
        const url = URL.createObjectURL(new Blob([bytes], { type: mime }));
        const link = document.createElement('a');
        link.download = fileName;
        link.href = url;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
        setTimeout(() => URL.revokeObjectURL(url), 10000);
    } catch (e) { console.error('sdDownloadBase64 failed', e); }
};

window.downloadConvertedFile = async (fileName, contentType, imgData) => {
    const img = new Image();
    if (imgData.startsWith('/') || imgData.startsWith('http') || imgData.startsWith('data:')) {
        img.src = imgData;
    } else {
        img.src = "data:image/png;base64," + imgData;
    }
    await img.decode();
    const canvas = document.createElement('canvas');
    canvas.width = img.width;
    canvas.height = img.height;
    const ctx = canvas.getContext('2d');
    if (contentType === 'image/jpeg') {
        ctx.fillStyle = "#FFFFFF";
        ctx.fillRect(0, 0, canvas.width, canvas.height);
    }
    ctx.drawImage(img, 0, 0);
    const dataUrl = canvas.toDataURL(contentType, 0.9);
    const link = document.createElement('a');
    link.download = fileName;
    link.href = dataUrl;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
};

// Native share sheet (mobile). Converts the image to a File and hands it to the OS via the Web
// Share API, removing the download-then-share dance. canShareFiles() gates the UI button.
window.sdShare = {
    canShareFiles: () => {
        try {
            if (!navigator.canShare) return false;
            const probe = new File([new Blob([new Uint8Array(1)], { type: 'image/png' })], 'x.png', { type: 'image/png' });
            return navigator.canShare({ files: [probe] });
        } catch { return false; }
    },

    shareImage: async (data, fileName) => {
        try {
            const src = (data && (data.startsWith('data:') || data.startsWith('/') || data.startsWith('http')))
                ? data
                : ('data:image/png;base64,' + data);
            const blob = await (await fetch(src)).blob();
            const file = new File([blob], fileName || 'image.png', { type: blob.type || 'image/png' });
            if (navigator.canShare && navigator.canShare({ files: [file] })) {
                await navigator.share({ files: [file] });
                return true;
            }
        } catch { /* user cancelled or unsupported */ }
        return false;
    }
};

window.promptHelper = {
    getCursorInfo: function (element) {
        if (!element) return null;
        const el = element.querySelector('textarea') || element.querySelector('input');
        if (!el) return null;

        const text = el.value;
        const pos = el.selectionStart;
        const delimiters = [' ', ',', '(', ')', '\n', '\r', '<', '>'];

        let start = pos;
        while (start > 0 && !delimiters.includes(text[start - 1])) {
            start--;
        }

        let end = pos;
        while (end < text.length && !delimiters.includes(text[end])) {
            end++;
        }

        const rawWord = text.substring(start, end);

        let isLoraContext = false;
        let searchTerm = rawWord;

        if (rawWord.toLowerCase().startsWith("lora:")) {
            isLoraContext = true;
            searchTerm = rawWord.substring(5);
        }

        return {
            word: searchTerm.trim(),
            start: start,
            end: end,
            isLoraContext: isLoraContext
        };
    },

    scrollSuggestionIntoView: (id) => {
        const el = document.getElementById(id);
        if (!el) return;
        el.scrollIntoView({ block: "nearest" });
    },

    bindAutocompleteKeys: (containerEl, dotnetRef) => {
        if (!containerEl || !dotnetRef) return;
        const ta = containerEl.querySelector("textarea, input");
        if (!ta) return;

        if (ta.__acHandler) {
            ta.removeEventListener("keydown", ta.__acHandler, true);
        }

        const handler = (e) => {
            if (containerEl.getAttribute("data-ac-open") !== "1") return;

            // Tab should NOT autocomplete: let it move focus to the next element natively, just
            // close the popup on the way out.
            if (e.key === "Tab") {
                dotnetRef.invokeMethodAsync("OnAutocompleteKey", "Escape");
                return; // no preventDefault -> native Tab proceeds
            }

            const keys = ["ArrowUp", "ArrowDown", "Enter", "Escape"];
            if (!keys.includes(e.key)) return;

            // Only hijack Enter when there is actually a suggestion to apply. Otherwise
            // (stale-open state, empty list) let the keystroke through so the textarea still
            // inserts a newline instead of silently swallowing it.
            if (e.key === "Enter" &&
                parseInt(containerEl.getAttribute("data-ac-count"), 10) <= 0) {
                return;
            }

            e.preventDefault();
            dotnetRef.invokeMethodAsync("OnAutocompleteKey", e.key);
        };

        ta.__acHandler = handler;
        ta.addEventListener("keydown", handler, true);
    },

    unbindAutocompleteKeys: (containerEl) => {
        if (!containerEl) return;
        const ta = containerEl.querySelector("textarea, input");
        if (ta && ta.__acHandler) {
            ta.removeEventListener("keydown", ta.__acHandler, true);
            delete ta.__acHandler;
        }
    },

    setTextAreaValue: (containerEl, value, caretIndex) => {
        if (!containerEl) return;
        const ta = containerEl.querySelector("textarea, input");
        if (!ta) return;

        ta.value = value;
        ta.dispatchEvent(new Event("input", { bubbles: true }));

        const clamp = () => {
            if (typeof caretIndex !== "number") return;
            const i = Math.max(0, Math.min(caretIndex, ta.value.length));
            try { ta.setSelectionRange(i, i); } catch { }
        };

        try { ta.focus({ preventScroll: true }); }
        catch { ta.focus(); }

        clamp();
        requestAnimationFrame(clamp);
        setTimeout(clamp, 0);
        setTimeout(clamp, 50);
        setTimeout(clamp, 150);
    }
};

// Mobile helpers: device detection and docking an element just above the soft keyboard
// using the VisualViewport API (so the suggestion ribbon is never hidden by the keyboard).
window.sdMobile = {
    isMobile: () => {
        try {
            // Touch-primary devices (phones/tablets) use the keyboard ribbon; everything else
            // (incl. narrow desktop windows) keeps the popover.
            return window.matchMedia('(pointer: coarse)').matches;
        } catch { return false; }
    },

    _kbHandler: null,

    startKeyboardDock: () => {
        const vv = window.visualViewport;
        if (!vv) return;

        const update = () => {
            // Dock the ribbon just above the keyboard, following scroll and the address bar
            // showing/hiding. This never closes the ribbon — closing is handled only by blur /
            // tapping a different word — so scrolling can't dismiss it.
            const offset = Math.max(0, window.innerHeight - vv.height - vv.offsetTop);
            document.documentElement.style.setProperty('--sd-kb-offset', offset + 'px');
            document.documentElement.classList.toggle('sd-keyboard-open', offset > 100);
        };

        if (window.sdMobile._kbHandler) {
            vv.removeEventListener('resize', window.sdMobile._kbHandler);
            vv.removeEventListener('scroll', window.sdMobile._kbHandler);
        }
        window.sdMobile._kbHandler = update;
        vv.addEventListener('resize', update);
        vv.addEventListener('scroll', update);
        update();
    },

    stopKeyboardDock: () => {
        document.documentElement.style.setProperty('--sd-kb-offset', '0px');
        document.documentElement.classList.remove('sd-keyboard-open');
        const vv = window.visualViewport;
        if (vv && window.sdMobile._kbHandler) {
            vv.removeEventListener('resize', window.sdMobile._kbHandler);
            vv.removeEventListener('scroll', window.sdMobile._kbHandler);
            window.sdMobile._kbHandler = null;
        }
    },

    // The suggestion ribbon is built directly under <body> (not inside the component tree)
    // so no ancestor transform (e.g. MudTabs) can clip or mis-position the fixed element.
    showRibbon: (items, dotnetRef) => {
        let el = document.getElementById('sd-ribbon');
        if (!el) {
            el = document.createElement('div');
            el.id = 'sd-ribbon';
            el.className = 'sd-suggestion-ribbon';
            document.body.appendChild(el);
            window.sdMobile._bindRibbon(el);
        }

        el.__sdRef = dotnetRef;
        el.innerHTML = '';
        (items || []).forEach((it, i) => {
            const chip = document.createElement('div');
            chip.className = 'sd-ribbon-chip';
            chip.dataset.index = i;

            const dot = document.createElement('span');
            dot.className = 'sd-ribbon-dot';
            dot.style.background = it.color || '#888';

            const label = document.createElement('span');
            label.className = 'sd-ribbon-label';
            label.textContent = it.text || '';

            const count = document.createElement('span');
            count.className = 'sd-ribbon-count';
            count.textContent = it.count || '';

            chip.appendChild(dot);
            chip.appendChild(label);
            chip.appendChild(count);
            el.appendChild(chip);
        });

        el.style.display = 'flex';
        el.scrollLeft = 0;
        window.sdMobile.startKeyboardDock();
    },

    // Drag the ribbon to scroll; a tap (no movement) applies the chip. preventDefault keeps the
    // textarea focused so the keyboard stays up either way.
    _bindRibbon: (el) => {
        const st = { active: false, dragging: false, startX: 0, startScroll: 0, pid: null, idx: -1, samples: [], vx: 0, raf: null };
        const DRAG = 6;
        const WINDOW = 100; // ms of recent movement used to measure release velocity
        const STALE = 70;   // ms; if the last move was older than this, treat as a held finger

        const stopMomentum = () => {
            if (st.raf) { cancelAnimationFrame(st.raf); st.raf = null; }
        };

        const prune = (now) => {
            while (st.samples.length > 2 && now - st.samples[0].t > WINDOW) st.samples.shift();
        };

        // Velocity over the recent window (px/ms), robust to a noisy last sample. Returns 0 if
        // the finger was held still before release.
        const measureVelocity = (now) => {
            prune(now);
            if (st.samples.length < 2) return 0;
            const last = st.samples[st.samples.length - 1];
            const first = st.samples[0];
            const span = last.t - first.t;
            if (span <= 0 || (now - last.t) > STALE) return 0;
            return (last.x - first.x) / span;
        };

        const startMomentum = () => {
            const maxV = 2.5;                                // px per ms (~2500 px/s) launch cap
            let v = Math.max(-maxV, Math.min(maxV, st.vx));  // px per ms
            const decay = 0.004;                             // per ms (exponential, ~170ms half-life)
            let last = performance.now();
            const step = (now) => {
                const dt = Math.min(now - last, 32);         // clamp dt so a stall can't cause a jump
                last = now;
                if (Math.abs(v) < 0.02) { st.raf = null; return; }
                const before = el.scrollLeft;
                el.scrollLeft -= v * dt;                      // distance = speed x time (framerate independent)
                v *= Math.exp(-decay * dt);                  // smooth, time-based deceleration
                if (el.scrollLeft === before) { st.raf = null; return; } // hit an edge
                st.raf = requestAnimationFrame(step);
            };
            st.raf = requestAnimationFrame(step);
        };

        const onMove = (e) => {
            if (!st.active || e.pointerId !== st.pid) return;
            const dx = e.clientX - st.startX;
            if (Math.abs(dx) > DRAG) st.dragging = true;
            if (st.dragging) {
                el.scrollLeft = st.startScroll - dx;
                const now = performance.now();
                st.samples.push({ t: now, x: e.clientX });
                prune(now);
                e.preventDefault();
            }
        };

        const onEnd = (e) => {
            if (!st.active || (st.pid !== null && e.pointerId !== st.pid)) return;
            st.active = false;
            window.removeEventListener('pointermove', onMove, true);
            window.removeEventListener('pointerup', onEnd, true);
            window.removeEventListener('pointercancel', onEnd, true);

            if (st.dragging) {
                st.vx = measureVelocity(performance.now());
                if (Math.abs(st.vx) > 0.05) startMomentum();
            }
            else if (st.idx >= 0 && el.__sdRef) {
                el.__sdRef.invokeMethodAsync('ApplyRibbonTag', st.idx);
            }
            st.dragging = false;
            st.idx = -1;
            st.pid = null;
        };

        el.addEventListener('pointerdown', (e) => {
            stopMomentum();
            const chip = e.target.closest ? e.target.closest('.sd-ribbon-chip') : null;
            st.active = true;
            st.dragging = false;
            st.startX = e.clientX;
            st.startScroll = el.scrollLeft;
            st.pid = e.pointerId;
            st.idx = chip ? parseInt(chip.dataset.index, 10) : -1;
            st.samples = [{ t: performance.now(), x: e.clientX }];
            st.vx = 0;
            e.preventDefault();

            window.addEventListener('pointermove', onMove, true);
            window.addEventListener('pointerup', onEnd, true);
            window.addEventListener('pointercancel', onEnd, true);
        });
    },

    hideRibbon: () => {
        const el = document.getElementById('sd-ribbon');
        if (el) el.style.display = 'none';
    }
};

// Auto-scroll controller for the server console. Keeps the log pinned to the bottom while
// "armed"; un-arms (and notifies .NET to uncheck the box) when the user scrolls up; re-pins
// when the element becomes visible again (tab switch).
window.sdConsole = {
    attach: (el) => {
        if (!el || el.__sdConsole) return;
        const state = { armed: true };
        el.__sdConsole = state;

        const pin = () => { el.scrollTop = el.scrollHeight; };

        // New output -> pin to bottom while armed (controlled only by the checkbox).
        state.mo = new MutationObserver(() => { if (state.armed) pin(); });
        state.mo.observe(el, { childList: true, subtree: true, characterData: true });

        // Became visible again (e.g. tab switch) -> pin if armed.
        state.io = new IntersectionObserver((entries) => {
            for (const e of entries) if (e.isIntersecting && state.armed) pin();
        });
        state.io.observe(el);

        pin();
    },

    detach: (el) => {
        const s = el && el.__sdConsole;
        if (!s) return;
        try { s.mo.disconnect(); } catch { }
        try { s.io.disconnect(); } catch { }
        delete el.__sdConsole;
    },

    pin: (el) => {
        const s = el && el.__sdConsole;
        if (!s) return;
        s.armed = true;
        el.scrollTop = el.scrollHeight;
        requestAnimationFrame(() => { el.scrollTop = el.scrollHeight; });
    },

    unpin: (el) => {
        const s = el && el.__sdConsole;
        if (s) s.armed = false;
    }
};

window.sdHelpers = {
    getImageDimensionsFromBase64: (base64, mime) => {
        return new Promise((resolve, reject) => {
            const img = new Image();
            img.onload = () => resolve({ width: img.naturalWidth, height: img.naturalHeight });
            img.onerror = () => reject("Failed to load image");
            img.src = `data:${mime};base64,${base64}`;
        });
    },
    pauseAllDialogVideos: () => {
        const videos = document.querySelectorAll('.gallery-full-override video');
        videos.forEach(v => {
            if (typeof v.pause === 'function') v.pause();
        });
    },
    playActiveDialogVideo: () => {
        const videos = document.querySelectorAll('.gallery-full-override video');
        videos.forEach(v => {
            if (typeof v.play === 'function') {
                v.play().catch(err => {});
            }
        });
    }
};

// Pointer-based drag & drop for the Rearrange Tags board. Uses Pointer Events so it works
// uniformly across mouse, touch, and pen on Chrome, Firefox, Safari and mobile (HTML5 DnD
// does not start in Firefox without setData, and does not fire at all on touch).
window.sdRearrange = {
    init: function (container, dotnetRef) {
        if (!container) return;
        if (container.__sdRearrange) this.detach(container);

        const DRAG_THRESHOLD = 6; // px before a press becomes a drag (so taps/clicks still work)

        const state = {
            dotnetRef,
            pointerId: null,
            srcEl: null,
            dragId: null,
            startX: 0,
            startY: 0,
            dragging: false,
            ghost: null,
            currentSlot: null,
            hiddenSlots: []
        };

        const setSlot = (slot) => {
            if (slot === state.currentSlot) return;
            if (state.currentSlot) state.currentSlot.classList.remove("slot-over");
            state.currentSlot = slot;
            if (slot) slot.classList.add("slot-over");
        };

        const startDrag = () => {
            state.dragging = true;
            container.classList.add("js-dragging");

            const src = state.srcEl;
            if (src) {
                src.classList.add("drag-source");
                const ghost = src.cloneNode(true);
                ghost.classList.add("rearrange-ghost");
                ghost.style.width = src.offsetWidth + "px";
                document.body.appendChild(ghost);
                state.ghost = ghost;

                // Hide slots that would be a no-op for this item: the ones immediately
                // before/after it (dropping there wouldn't move it), plus any slots inside
                // it (a group can't be dropped into itself).
                const hide = (el) => {
                    if (el && el.classList && el.classList.contains("rearrange-slot")) {
                        el.classList.add("slot-hidden");
                        state.hiddenSlots.push(el);
                    }
                };
                hide(src.previousElementSibling);
                hide(src.nextElementSibling);
                src.querySelectorAll(".rearrange-slot").forEach((s) => {
                    s.classList.add("slot-hidden");
                    state.hiddenSlots.push(s);
                });
            }
        };

        const moveGhost = (x, y) => {
            if (state.ghost) {
                state.ghost.style.left = (x + 12) + "px";
                state.ghost.style.top = (y + 12) + "px";
            }
        };

        const onMove = (e) => {
            if (e.pointerId !== state.pointerId) return;

            if (!state.dragging) {
                const dist = Math.hypot(e.clientX - state.startX, e.clientY - state.startY);
                if (dist < DRAG_THRESHOLD) return;
                startDrag();
            }

            e.preventDefault();
            moveGhost(e.clientX, e.clientY);

            const el = document.elementFromPoint(e.clientX, e.clientY);
            const slot = (el && el.closest) ? el.closest(".rearrange-slot[data-slot-parent]") : null;
            setSlot(slot && container.contains(slot) ? slot : null);
        };

        const cleanup = () => {
            document.removeEventListener("pointermove", onMove, true);
            document.removeEventListener("pointerup", onUp, true);
            document.removeEventListener("pointercancel", onUp, true);

            if (state.ghost) { state.ghost.remove(); state.ghost = null; }
            if (state.srcEl) state.srcEl.classList.remove("drag-source");
            state.hiddenSlots.forEach((s) => s.classList.remove("slot-hidden"));
            state.hiddenSlots = [];
            setSlot(null);
            container.classList.remove("js-dragging");

            const result = { dragging: state.dragging, slot: state.currentSlot, dragId: state.dragId };
            state.pointerId = null;
            state.srcEl = null;
            state.dragId = null;
            state.dragging = false;
            state.currentSlot = null;
            return result;
        };

        const onUp = (e) => {
            if (e.pointerId !== state.pointerId) return;

            const dragId = state.dragId;
            const dragging = state.dragging;
            const slot = state.currentSlot;
            cleanup();

            if (dragging && slot && dragId) {
                const parentId = slot.getAttribute("data-slot-parent");
                const index = parseInt(slot.getAttribute("data-slot-index"), 10);
                if (parentId && !isNaN(index)) {
                    window.sdHaptic && window.sdHaptic.buzz(20);
                    state.dotnetRef.invokeMethodAsync("DropNode", dragId, parentId, index);
                }
            }
        };

        const onDown = (e) => {
            if (e.pointerType === "mouse" && e.button !== 0) return;
            if (state.pointerId !== null) return; // already tracking a gesture

            const handle = e.target.closest ? e.target.closest("[data-drag-id]") : null;
            if (!handle || !container.contains(handle)) return;
            // Let the +/- weight buttons keep their own click behaviour.
            if (e.target.closest(".rearrange-mod")) return;

            state.pointerId = e.pointerId;
            state.srcEl = handle;
            state.dragId = handle.getAttribute("data-drag-id");
            state.startX = e.clientX;
            state.startY = e.clientY;
            state.dragging = false;
            state.currentSlot = null;

            document.addEventListener("pointermove", onMove, true);
            document.addEventListener("pointerup", onUp, true);
            document.addEventListener("pointercancel", onUp, true);
        };

        container.__sdRearrange = { onDown };
        container.addEventListener("pointerdown", onDown, true);
    },

    detach: function (container) {
        if (!container || !container.__sdRearrange) return;
        container.removeEventListener("pointerdown", container.__sdRearrange.onDown, true);
        delete container.__sdRearrange;
    }
};

// Preview videos must never play sound. Any <video class="civ-novol"> is force-muted (and
// kept muted even if something tries to unmute it). Best-effort audio detection adds a
// data-has-audio attribute so the UI can show a subtle speaker icon.
window.civVideo = {
    hasAudio: (v) => {
        try {
            return !!(v.mozHasAudio
                || (typeof v.webkitAudioDecodedByteCount === 'number' && v.webkitAudioDecodedByteCount > 0)
                || (v.audioTracks && v.audioTracks.length > 0));
        } catch { return false; }
    },
    setMuted: (v, muted) => {
        try { v.muted = muted; v.volume = muted ? 0 : 1; } catch { }
    },
    _lock: (v) => {
        if (v.__civLocked) return;
        v.__civLocked = true;
        try { v.muted = true; v.volume = 0; } catch { }
        v.addEventListener('volumechange', () => { if (!v.muted) { try { v.muted = true; } catch { } } });
        const flag = () => { if (window.civVideo.hasAudio(v)) v.setAttribute('data-has-audio', '1'); };
        v.addEventListener('loadeddata', flag);
        v.addEventListener('playing', flag);
    },
    _scan: (root) => {
        const scope = root && root.querySelectorAll ? root : document;
        scope.querySelectorAll('video.civ-novol').forEach(window.civVideo._lock);
    },
    init: () => {
        if (window.civVideo.__init) return;
        window.civVideo.__init = true;
        window.civVideo._scan(document);
        const obs = new MutationObserver((muts) => {
            for (const m of muts) for (const n of m.addedNodes) {
                if (n.nodeType !== 1) continue;
                if (n.matches && n.matches('video.civ-novol')) window.civVideo._lock(n);
                window.civVideo._scan(n);
            }
        });
        obs.observe(document.documentElement, { childList: true, subtree: true });
    }
};
window.civVideo.init();

// Toggles the global "reveal NSFW blur on hover" class on <html> so component CSS can gate it.
window.civNsfw = {
    setHover: (on) => {
        try { document.documentElement.classList.toggle('nsfw-hover', !!on); } catch { }
    }
};

// Audio control for the single-media gallery lightbox (the one place audio is allowed).
// The mute preference is persisted in localStorage so it survives across visits/devices.
window.civGallery = {
    _key: 'civ-gallery-muted',
    _active: () => document.querySelector('.gallery-full-override video'),
    getPref: () => {
        try { return localStorage.getItem(window.civGallery._key) !== '0'; } // default: muted
        catch { return true; }
    },
    _setPref: (m) => { try { localStorage.setItem(window.civGallery._key, m ? '1' : '0'); } catch { } },
    applyPref: () => {
        const v = window.civGallery._active();
        const m = window.civGallery.getPref();
        if (v) { try { v.muted = m; v.volume = m ? 0 : 1; } catch { } }
        return m;
    },
    toggleMute: () => {
        const v = window.civGallery._active();
        const m = v ? !v.muted : !window.civGallery.getPref();
        if (v) { try { v.muted = m; v.volume = m ? 0 : 1; } catch { } }
        window.civGallery._setPref(m);
        return m;
    },
    isMuted: () => { const v = window.civGallery._active(); return v ? v.muted : window.civGallery.getPref(); },
    hasAudio: () => { const v = window.civGallery._active(); return v ? window.civVideo.hasAudio(v) : false; }
};

// Bridge for the model-details HTML shown inside the LoRA dialog's sandboxed iframe.
// The iframe posts a {type:'civitai-open-image'} message when an image is clicked; we
// forward it to the .NET dialog so it can open the in-app gallery viewer.
window.civsfzGallery = {
    _ref: null,
    _handler: null,
    _keyHandler: null,
    register: (dotnetRef) => {
        window.civsfzGallery.unregister();
        window.civsfzGallery._ref = dotnetRef;

        // Image clicks from the sandboxed details iframe.
        const handler = (e) => {
            const d = e && e.data;
            if (!d || d.type !== 'civitai-open-image') return;
            if (window.civsfzGallery._ref) {
                window.civsfzGallery._ref.invokeMethodAsync('OnHtmlImageClicked', d.src || '', d.index | 0);
            }
        };
        window.civsfzGallery._handler = handler;
        window.addEventListener('message', handler);

        // Left/Right navigate the details preview gallery — reliably, regardless of which
        // control was last clicked. Skips text inputs, open dropdowns, and the fullscreen
        // viewer (which handles its own arrows).
        const keyHandler = (e) => {
            if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;
            const a = document.activeElement;
            const tag = a ? a.tagName : '';
            if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || (a && a.isContentEditable)) return;
            if (document.querySelector('.gallery-full-override')) return; // fullscreen gallery open
            if (document.querySelector('.mud-popover-open')) return;      // a menu/select is open
            if (!window.civsfzGallery._ref) return;
            e.preventDefault();
            window.civsfzGallery._ref.invokeMethodAsync('OnGalleryArrow', e.key === 'ArrowRight' ? 1 : -1);
        };
        window.civsfzGallery._keyHandler = keyHandler;
        window.addEventListener('keydown', keyHandler);
    },
    unregister: () => {
        if (window.civsfzGallery._handler) {
            window.removeEventListener('message', window.civsfzGallery._handler);
            window.civsfzGallery._handler = null;
        }
        if (window.civsfzGallery._keyHandler) {
            window.removeEventListener('keydown', window.civsfzGallery._keyHandler);
            window.civsfzGallery._keyHandler = null;
        }
        window.civsfzGallery._ref = null;
    }
};

// Mobile: make the hardware Back button close the topmost open dialog (like Escape) instead of
// navigating away. We push a history "guard" entry whenever a MudBlazor dialog is open; Back then
// pops it and we dispatch Escape to the dialog. Touch / coarse-pointer devices only.
window.sdBackClose = {
    init: function () {
        if (window.__sdBack) return;
        try { if (!window.matchMedia('(pointer: coarse)').matches) return; } catch { return; }
        window.__sdBack = true;

        const dialogs = () => document.querySelectorAll('.mud-dialog');
        const hasDialog = () => dialogs().length > 0;
        let guarded = false, ignorePop = false;

        const pushGuard = () => {
            if (guarded) return;
            guarded = true;
            try { history.pushState({ sdDialog: 1 }, ''); } catch { guarded = false; }
        };

        // Keep exactly one guard entry on the history stack while any dialog is open.
        const obs = new MutationObserver(() => {
            if (hasDialog()) {
                pushGuard();
            } else if (guarded) {
                // Closed by some other means (button / backdrop / Escape) — drop our guard entry.
                guarded = false; ignorePop = true;
                try { history.back(); } catch { ignorePop = false; }
            }
        });
        obs.observe(document.documentElement, { childList: true, subtree: true });

        window.addEventListener('popstate', () => {
            if (ignorePop) { ignorePop = false; return; }
            guarded = false; // our guard entry was consumed by this Back press
            const open = dialogs();
            if (open.length > 0) {
                // Re-arm the guard SYNCHRONOUSLY before closing, so a rapid second Back (or a nested
                // dialog still underneath) stays guarded without waiting for the async MutationObserver
                // — that gap is how a Back press occasionally fell through and navigated the page away.
                pushGuard();
                const top = open[open.length - 1];
                top.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', code: 'Escape', bubbles: true, cancelable: true }));
            }
        });
    }
};
window.sdBackClose.init();

// Optional mobile haptics. Enabled state is pushed from UiPreferences so callers don't need to
// know the setting — they just call sdHaptic.buzz(ms).
//
// IMPORTANT (Firefox/Android, incl. Iceraven): the Vibration API only fires when called *inside*
// a user-gesture task. Chrome keeps a "sticky" activation so a vibrate() after a Blazor SignalR
// round-trip still works, but Firefox does not — a vibrate() dispatched from a server callback is
// silently ignored. So the real tick is produced by a delegated `pointerdown` listener (which runs
// in the gesture), and the C# `buzz()` calls become best-effort echoes that de-dupe against it.
window.sdHaptic = {
    _on: false,
    _last: 0,                                   // timestamp of the most recent real vibration
    _bound: false,

    setEnabled: (v) => {
        window.sdHaptic._on = !!v;
        window.sdHaptic._bindDelegate();        // attach once; it self-gates on _on
    },

    _fire: (ms) => {
        try {
            if (navigator.vibrate) {
                // Floor the duration: most phone vibration motors can't produce a *felt* pulse below
                // ~20ms, so a 10-15ms tick fires but is imperceptible. Clamp everything up.
                navigator.vibrate(Math.max(ms || 0, 25));
                window.sdHaptic._last = (performance && performance.now) ? performance.now() : Date.now();
            }
        } catch { }
    },

    // Called from .NET after a click/gesture. If the delegated pointerdown already buzzed for this
    // same interaction (within 500ms), skip — otherwise (e.g. a swipe with no button pointerdown)
    // fire it ourselves.
    buzz: (ms) => {
        if (!window.sdHaptic._on) return;
        const now = (performance && performance.now) ? performance.now() : Date.now();
        if (now - window.sdHaptic._last < 500) return;
        window.sdHaptic._fire(ms || 20);
    },

    // One global, capturing pointerdown listener fires a short tick for interactive controls,
    // guaranteeing the vibrate() happens within the gesture (the only thing Firefox accepts).
    _bindDelegate: () => {
        if (window.sdHaptic._bound) return;
        window.sdHaptic._bound = true;
        const SEL = 'button, [role="button"], .mud-button-root, .mud-icon-button, .sd-bn-item, ' +
                    '.sd-bn-fab, .mud-tab, .mud-list-item-clickable, .mud-chip, .sd-ribbon-chip, ' +
                    '.meta-peek, .meta-grabber, a.mud-nav-link';
        document.addEventListener('pointerdown', (e) => {
            // Ungated, in-gesture test pulse (Settings "Test" button) — works even on Firefox, which
            // only honours vibrate() inside the gesture, and regardless of the enabled toggle.
            if (e.target && e.target.closest && e.target.closest('[data-haptic-test]')) {
                window.sdHaptic._fire(300);
                return;
            }
            if (!window.sdHaptic._on) return;
            const t = e.target && e.target.closest ? e.target.closest(SEL) : null;
            if (!t) return;
            if (t.disabled || t.getAttribute('aria-disabled') === 'true' || t.classList.contains('mud-disabled')) return;
            window.sdHaptic._fire(30);
        }, true);
    }
};

// Close every open MudBlazor dialog (dispatch Escape to each). Used when sending an image to
// Img2Img so the user lands on the tab cleanly, not behind a leftover viewer/metadata dialog.
window.sdCloseDialogs = () => {
    document.querySelectorAll('.mud-dialog').forEach(d => {
        d.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', code: 'Escape', bubbles: true, cancelable: true }));
    });
};

// Ctrl/Cmd+Enter triggers generation on the active tab (handled in Home via GenerateHotkey).
window.sdHotkeys = {
    _ref: null,
    _handler: null,
    register: (dotnetRef) => {
        window.sdHotkeys.unregister();
        window.sdHotkeys._ref = dotnetRef;
        const h = (e) => {
            if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
                e.preventDefault();
                if (window.sdHotkeys._ref) window.sdHotkeys._ref.invokeMethodAsync('GenerateHotkey');
            }
        };
        window.sdHotkeys._handler = h;
        window.addEventListener('keydown', h);
    },
    unregister: () => {
        if (window.sdHotkeys._handler) {
            window.removeEventListener('keydown', window.sdHotkeys._handler);
            window.sdHotkeys._handler = null;
        }
        window.sdHotkeys._ref = null;
    }
};

// Fullscreen lightbox gestures (pan / pinch-zoom / swipe / double-tap / wheel) handled entirely
// in JS. Blazor Server would otherwise send every pointermove over SignalR and re-render per event,
// which makes panning crawl on mobile. We mutate the DOM transform directly via rAF and only call
// back to .NET on a committed swipe (Navigate) and when zoom crosses 1.0 (SetZoomed, for the icon).
window.sdLightbox = {
    init: function (root, dotnetRef, swipeUp, swipeDown, backdrop) {
        if (!root) return;
        if (root.__sdLightbox) this.destroy(root);

        const st = {
            dotnetRef,
            pointers: new Map(),
            scale: 1, panX: 0, panY: 0, startPanX: 0, startPanY: 0,
            dragX: 0, dragging: false, startX: 0, startY: 0,
            pinchDist: 0, startScale: 1, activeId: null,
            lastTap: 0, raf: null, zoomed: false,
            moved: false, multiTouch: false, didDoubleTap: false, tapTimer: null,
            chromeHidden: false, zoomRequested: false, onControl: false,
            swipeUp: swipeUp || 'none', swipeDown: swipeDown || 'none'
        };
        root.__sdLightbox = st;

        // Let the page hide the mobile bottom nav while the fullscreen gallery is open.
        try { document.documentElement.classList.add('sd-lightbox-open'); } catch { }

        const track = () => root.querySelector('.drag-track');
        const media = () => root.querySelector('.media-container');

        const apply = () => {
            st.raf = null;
            const t = track(), m = media();
            if (t) t.style.transform = `translateX(${st.dragX}px)`;
            if (m) m.style.transform = `scale(${st.scale}) translate(${st.panX / st.scale}px, ${st.panY / st.scale}px)`;
        };
        const schedule = () => { if (!st.raf) st.raf = requestAnimationFrame(apply); };

        const setTransition = (on) => {
            const t = track(), m = media();
            if (t) t.style.transition = on ? 'transform 180ms ease-out' : 'none';
            if (m) m.style.transition = on ? 'transform 200ms ease-out' : 'none';
        };

        // Pan bounds are per-axis: how far the scaled image overflows the viewport on each side. This
        // lets tall images pan fully top↔bottom and wide images fully left↔right (a single fixed cap
        // couldn't reach the far edge of a long image). offset* is the unscaled fit size (transform
        // doesn't affect it); the gesture root is the viewport.
        const clampPan = () => {
            const m = media();
            let maxX = 300 * (st.scale - 1), maxY = maxX; // fallback if the media isn't measurable yet
            if (m && m.offsetWidth > 0) {
                maxX = Math.max(0, (m.offsetWidth * st.scale - root.clientWidth) / 2);
                maxY = Math.max(0, (m.offsetHeight * st.scale - root.clientHeight) / 2);
            }
            st.panX = Math.max(-maxX, Math.min(maxX, st.panX));
            st.panY = Math.max(-maxY, Math.min(maxY, st.panY));
        };

        // ---- Phase 2: crisp-on-zoom detail layer ----
        const zoomLayer = () => root.querySelector('.zoom-detail-layer');

        // Ask .NET for a higher-res tier of the current image and fade it in once loaded (if still
        // zoomed). Lazy + once per slide; re-zooming after a zoom-out just re-shows the cached layer.
        const requestZoomDetail = () => {
            const z = zoomLayer();
            if (!z) return;
            if (st.zoomRequested) { if (z.getAttribute('src') && st.zoomed) z.classList.add('visible'); return; }
            st.zoomRequested = true;
            let edge = 2560;
            try { edge = window.sdImageOpt.zoomWidth(); } catch { }
            Promise.resolve(st.dotnetRef.invokeMethodAsync('RequestZoomSrc', edge)).then((url) => {
                if (!url || !st.zoomed) return;            // null tier, or zoomed back out before it arrived
                const el = zoomLayer();
                if (!el) return;
                el.onload = () => { if (st.zoomed) el.classList.add('visible'); };
                el.src = url;
            }).catch(() => { });
        };
        const hideZoomDetail = () => { const z = zoomLayer(); if (z) z.classList.remove('visible'); };
        const clearZoomDetail = () => {
            st.zoomRequested = false;
            const z = zoomLayer();
            if (z) { z.classList.remove('visible'); z.removeAttribute('src'); }
        };
        // Exposed on st so the out-of-closure methods (toggleZoom / reset) can drive them too.
        st.requestZoomDetail = requestZoomDetail;
        st.hideZoomDetail = hideZoomDetail;
        st.clearZoomDetail = clearZoomDetail;

        const setZoomed = (z) => {
            if (st.zoomed === z) return;
            st.zoomed = z;
            if (z) requestZoomDetail(); else hideZoomDetail();
            try { st.dotnetRef.invokeMethodAsync('SetZoomed', z); } catch { }
        };

        st.onDown = (e) => {
            // Ignore non-primary buttons (right / middle click) so they never pan, zoom, swipe, or
            // toggle the chrome — and so the browser's native context menu (Copy/Save image) is left
            // alone. The pointer is intentionally not tracked, so onMove/onUp skip it too.
            if (e.button > 0) return;

            // A new interaction starts: cancel a pending single-tap (so a second tap, a drag, or a
            // pinch never resolves into "toggle UI").
            if (st.tapTimer) { clearTimeout(st.tapTimer); st.tapTimer = null; }

            st.pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });

            if (st.pointers.size === 1) {
                st.dragging = true; setTransition(false);
                st.startX = e.clientX; st.startY = e.clientY;
                st.startPanX = st.panX; st.startPanY = st.panY;
                st.dragX = 0; st.activeId = e.pointerId;
                st.moved = false; st.multiTouch = false; st.didDoubleTap = false;

                // Did this gesture start on a UI control (e.g. the carousel's prev/next arrows, which
                // sit inside the gesture root)? If so it's a button press, not a tap on the image — so
                // it must NOT toggle the chrome / fullscreen.
                st.onControl = !!(e.target && e.target.closest &&
                    e.target.closest('button, a, [role="button"], .mud-button-root'));

                const now = performance.now();
                if (now - st.lastTap < 300) { window.sdLightbox.toggleZoom(root); st.lastTap = 0; st.didDoubleTap = true; }
                else st.lastTap = now;
            } else if (st.pointers.size === 2) {
                st.dragging = false; // suspend single-finger drag during pinch
                st.multiTouch = true; // a two-finger gesture is never a tap
                const p = [...st.pointers.values()];
                st.pinchDist = Math.hypot(p[0].x - p[1].x, p[0].y - p[1].y);
                st.startScale = st.scale;
            }
        };

        st.onMove = (e) => {
            if (!st.pointers.has(e.pointerId)) return;
            st.pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
            e.preventDefault();

            if (st.pointers.size === 2) {
                const p = [...st.pointers.values()];
                const dist = Math.hypot(p[0].x - p[1].x, p[0].y - p[1].y);
                if (st.pinchDist > 10) {
                    st.scale = Math.max(1, Math.min(5, st.startScale * (dist / st.pinchDist)));
                    if (st.scale === 1) { st.panX = 0; st.panY = 0; }
                    clampPan(); setZoomed(st.scale > 1); schedule();
                }
            } else if (st.dragging && e.pointerId === st.activeId) {
                // Past a small slop the gesture is a pan/swipe, not a tap.
                if (Math.hypot(e.clientX - st.startX, e.clientY - st.startY) > 8) st.moved = true;
                if (st.scale > 1) {
                    st.panX = st.startPanX + (e.clientX - st.startX);
                    st.panY = st.startPanY + (e.clientY - st.startY);
                    clampPan(); schedule();
                } else {
                    let d = e.clientX - st.startX;
                    const max = 320; // resistance past the edges
                    if (d > max) d = max + (d - max) * 0.2;
                    if (d < -max) d = -max + (d + max) * 0.2;
                    st.dragX = d; schedule();
                }
            }
        };

        st.onUp = (e) => {
            // A pointer we never tracked (e.g. a right-click skipped in onDown) must not run the
            // tap/swipe resolution below using stale state.
            if (!st.pointers.has(e.pointerId)) return;
            st.pointers.delete(e.pointerId);
            if (st.pointers.size < 2) st.pinchDist = 0;

            let navigated = false;
            if (e.pointerId === st.activeId || st.pointers.size === 0) {
                if (st.dragging) {
                    st.dragging = false; setTransition(true);
                    if (st.scale === 1) {
                        // A mostly-vertical swipe (when not zoomed) runs the user's configured up/down
                        // action (close / metadata / nothing). Otherwise a big horizontal swipe navigates.
                        const dx = e.clientX - st.startX, dy = e.clientY - st.startY;
                        const VT = 80;
                        if (Math.abs(dy) > Math.abs(dx) && Math.abs(dy) > VT) {
                            st.dragX = 0; schedule();
                            const action = dy < 0 ? st.swipeUp : st.swipeDown;
                            if (action && action !== 'none') {
                                navigated = true; // not a tap → don't toggle the chrome
                                try { st.dotnetRef.invokeMethodAsync('OnSwipeAction', action); } catch { }
                            }
                        } else {
                            const threshold = 120;
                            let dir = 0;
                            if (st.dragX > threshold) dir = -1;
                            else if (st.dragX < -threshold) dir = 1;
                            st.dragX = 0; schedule();
                            if (dir !== 0) { navigated = true; try { st.dotnetRef.invokeMethodAsync('Navigate', dir); } catch { } }
                        }
                    }
                }
                st.activeId = null;
            }

            // Single, clean tap (no pan, no swipe-navigate, no pinch, not a double-tap) once all
            // fingers are up → toggle the UI chrome. Delayed so a second tap (double-tap zoom) wins.
            if (st.pointers.size === 0 && e.type !== 'pointercancel'
                && !st.moved && !st.multiTouch && !st.didDoubleTap && !navigated && !st.onControl) {
                if (st.tapTimer) clearTimeout(st.tapTimer);
                st.tapTimer = setTimeout(() => {
                    st.tapTimer = null;
                    st.chromeHidden = !st.chromeHidden;
                    // Toggle the in-app chrome first so it can never be blocked by a fullscreen error.
                    try { st.dotnetRef.invokeMethodAsync('ToggleChrome'); } catch { }
                    // Then drive browser fullscreen here in JS (the tap's transient activation is
                    // still valid; a .NET round-trip would lose it and the request would be denied).
                    try {
                        if (st.chromeHidden) window.sdLightbox._enterFullscreen();
                        else window.sdLightbox._exitFullscreen();
                    } catch { }
                }, 300);
            }
        };

        st.onWheel = (e) => {
            e.preventDefault();
            if (e.deltaY < 0) st.scale = Math.min(5, st.scale + 0.25);
            else { st.scale = Math.max(1, st.scale - 0.25); if (st.scale === 1) { st.panX = 0; st.panY = 0; } }
            clampPan(); setZoomed(st.scale > 1);
            setTransition(true); schedule();
            setTimeout(() => setTransition(false), 220);
        };

        root.addEventListener('pointerdown', st.onDown);
        root.addEventListener('pointermove', st.onMove, { passive: false });
        root.addEventListener('pointerup', st.onUp);
        root.addEventListener('pointercancel', st.onUp);
        root.addEventListener('wheel', st.onWheel, { passive: false });

        // The metadata backdrop sits ABOVE the gesture root and eats pointer events while the sheet is
        // open, so the configurable swipe wouldn't fire there. Detect a vertical swipe on the backdrop
        // and run the same action; a clean tap still falls through to its close-on-click. (A swipe sets a
        // flag and a capturing click handler swallows the synthetic click so it doesn't also close.)
        if (backdrop) {
            st.backdrop = backdrop;
            let bx = 0, by = 0, bid = null, bswiped = false;
            st.bdDown = (e) => { bx = e.clientX; by = e.clientY; bid = e.pointerId; };
            st.bdUp = (e) => {
                if (e.pointerId !== bid) return; bid = null;
                const dx = e.clientX - bx, dy = e.clientY - by;
                if (Math.abs(dy) > Math.abs(dx) && Math.abs(dy) > 80) {
                    bswiped = true;
                    const action = dy < 0 ? st.swipeUp : st.swipeDown;
                    if (action && action !== 'none') { try { st.dotnetRef.invokeMethodAsync('OnSwipeAction', action); } catch { } }
                }
            };
            st.bdClick = (e) => { if (bswiped) { e.stopPropagation(); e.preventDefault(); bswiped = false; } };
            backdrop.addEventListener('pointerdown', st.bdDown);
            backdrop.addEventListener('pointerup', st.bdUp);
            backdrop.addEventListener('click', st.bdClick, true);
        }
    },

    // Browser fullscreen (hides the address/status bar on mobile). Best-effort: unsupported on iOS
    // Safari for non-video elements, and only succeeds while a user gesture's activation is valid.
    _enterFullscreen: function () {
        try {
            const el = document.documentElement;
            if (document.fullscreenElement || document.webkitFullscreenElement) return;
            const fn = el.requestFullscreen || el.webkitRequestFullscreen || el.mozRequestFullScreen || el.msRequestFullscreen;
            if (fn) { const r = fn.call(el); if (r && r.catch) r.catch(() => { }); }
        } catch { }
    },

    _exitFullscreen: function () {
        try {
            if (!(document.fullscreenElement || document.webkitFullscreenElement)) return;
            const fn = document.exitFullscreen || document.webkitExitFullscreen || document.mozCancelFullScreen || document.msExitFullscreen;
            if (fn) { const r = fn.call(document); if (r && r.catch) r.catch(() => { }); }
        } catch { }
    },

    toggleZoom: function (root) {
        const st = root && root.__sdLightbox;
        if (!st) return;
        if (st.scale > 1) { st.scale = 1; } else { st.scale = 2.5; }
        st.panX = 0; st.panY = 0; st.dragX = 0;
        const m = root.querySelector('.media-container');
        if (m) { m.style.transition = 'transform 200ms ease-out'; m.style.transform = `scale(${st.scale})`; }
        if (st.zoomed !== (st.scale > 1)) {
            st.zoomed = st.scale > 1;
            try { st.dotnetRef.invokeMethodAsync('SetZoomed', st.zoomed); } catch { }
        }
        if (st.scale > 1) { if (st.requestZoomDetail) st.requestZoomDetail(); }
        else if (st.hideZoomDetail) st.hideZoomDetail();
    },

    // After the active slide changes, clear transforms so the new image starts centered/unzoomed.
    reset: function (root) {
        const st = root && root.__sdLightbox;
        if (!st) return;
        st.scale = 1; st.panX = 0; st.panY = 0; st.dragX = 0; st.pointers.clear();
        const t = root.querySelector('.drag-track'), m = root.querySelector('.media-container');
        if (t) { t.style.transition = 'none'; t.style.transform = 'translateX(0px)'; }
        if (m) { m.style.transition = 'none'; m.style.transform = 'scale(1)'; }
        if (st.zoomed) { st.zoomed = false; try { st.dotnetRef.invokeMethodAsync('SetZoomed', false); } catch { } }
        if (st.clearZoomDetail) st.clearZoomDetail(); // drop the previous slide's detail overlay
    },

    destroy: function (root) {
        const st = root && root.__sdLightbox;
        if (!st) return;
        root.removeEventListener('pointerdown', st.onDown);
        root.removeEventListener('pointermove', st.onMove);
        root.removeEventListener('pointerup', st.onUp);
        root.removeEventListener('pointercancel', st.onUp);
        root.removeEventListener('wheel', st.onWheel);
        if (st.backdrop) {
            st.backdrop.removeEventListener('pointerdown', st.bdDown);
            st.backdrop.removeEventListener('pointerup', st.bdUp);
            st.backdrop.removeEventListener('click', st.bdClick, true);
        }
        if (st.raf) cancelAnimationFrame(st.raf);
        if (st.tapTimer) clearTimeout(st.tapTimer);
        window.sdLightbox._exitFullscreen(); // leave fullscreen when the viewer closes
        try { document.documentElement.classList.remove('sd-lightbox-open'); } catch { }
        delete root.__sdLightbox;
    }
};

// Helpers for the fullscreen viewer's image optimization (server does the actual downscaling).
window.sdImageOpt = {
    // The long-edge pixel size worth fetching/decoding for the fullscreen viewer on THIS device.
    // Based on the viewport times the (DPR-capped) pixel density: a phone's fit view needs ~1200-1700px,
    // not 2560 — sending more just wastes transfer time over wifi. Capped at 2048 for desktops.
    tierWidth: function () {
        try {
            const dpr = Math.min(window.devicePixelRatio || 1, 2);
            const longEdge = Math.max(window.innerWidth || 0, window.innerHeight || 0) * dpr;
            const v = Math.round(longEdge);
            return Math.max(768, Math.min(2048, v || 2048));
        } catch { return 1600; }
    },

    // The long-edge size for the on-zoom detail tier (phase 2). Bigger than the fit tier for real
    // detail, but gated by device memory so a weak phone never builds a texture it can't handle.
    zoomWidth: function () {
        try {
            const dpr = Math.min(window.devicePixelRatio || 1, 3);
            const base = Math.max(window.innerWidth || 0, window.innerHeight || 0) * dpr;
            const cap = (navigator.deviceMemory || 4) < 4 ? 2560 : 4096; // constrained vs capable
            return Math.max(1024, Math.min(cap, Math.round(base * 2)));
        } catch { return 2560; }
    }
};

// Inpaint mask painter. A transparent canvas overlays the init image; the user brushes white strokes
// (shown translucently via CSS), and getMask() exports a black-background / white-strokes PNG at the
// image's native resolution — exactly what A1111/Forge expects as the inpaint mask.
window.sdMask = {
    init: function (canvas, img, brush, color, dotnetRef) {
        if (!canvas || !img) return;
        if (canvas.__mask) this.destroy(canvas);
        const st = {
            img, dotnetRef: dotnetRef || null,
            brush: brush || 30, drawing: false, mode: 'brush', last: null,
            w: Math.max(1, Math.round(img.clientWidth)), h: Math.max(1, Math.round(img.clientHeight)),
            layers: [], activeId: 0, nextId: 1,
            undo: [], redo: [], histLimit: 50,   // mask snapshots compress small, so 50 is cheap
            padOn: false, padNative: 32, nativeW: 0, nativeH: 0
        };
        canvas.__mask = st;
        st.ctx = canvas.getContext('2d', { willReadFrequently: true });

        // Padding-preview overlay: a sibling canvas above the mask that only ever draws the
        // "only masked" crop outline — kept separate so it never pollutes the exported mask.
        const pad = document.createElement('canvas');
        pad.style.cssText = 'position:absolute;top:0;left:0;width:100%;height:100%;pointer-events:none;z-index:4;';
        (canvas.parentNode || document.body).appendChild(pad);
        st.padCanvas = pad;

        // The on-screen canvas is a COMPOSITE of the layers; strokes land on the active layer's own
        // offscreen canvas. Keep the display + every layer in sync with the image's displayed size,
        // rescaling content so a layout nudge (e.g. opening the viewer) never wipes the mask.
        const fit = () => {
            const w = Math.round(img.clientWidth), h = Math.round(img.clientHeight);
            if (w <= 0 || h <= 0) return;
            if (canvas.width !== w || canvas.height !== h) {
                for (const l of st.layers) {
                    let prev = null;
                    if (l.canvas.width > 0 && l.canvas.height > 0) {
                        prev = document.createElement('canvas'); prev.width = l.canvas.width; prev.height = l.canvas.height;
                        try { prev.getContext('2d').drawImage(l.canvas, 0, 0); } catch { prev = null; }
                    }
                    l.canvas.width = w; l.canvas.height = h;
                    if (prev) { try { l.canvas.getContext('2d').drawImage(prev, 0, 0, w, h); } catch { } }
                }
                canvas.width = w; canvas.height = h; st.w = w; st.h = h;
                pad.width = w; pad.height = h;
                window.sdMask._composite(canvas);
                window.sdMask._refreshPad(canvas);
            }
        };
        st.fit = fit;

        // Hover ring that shows the brush size, coloured to the active layer.
        const ring = document.createElement('div');
        ring.style.cssText = 'position:fixed;border-radius:50%;pointer-events:none;z-index:9999;display:none;' +
            'transform:translate(-50%,-50%);box-shadow:0 0 0 1px rgba(0,0,0,0.6);';
        document.body.appendChild(ring);
        st.ring = ring;
        const activeColor = () => { const l = window.sdMask._active(st); return l ? l.color : '#ff3b30'; };
        const styleRing = () => { ring.style.border = st.mode === 'eraser' ? '2px dashed #ff5252' : ('2px solid ' + activeColor()); };
        st.styleRing = styleRing;
        const showRing = (e) => {
            const r = canvas.getBoundingClientRect();
            const d = st.brush * (r.width / (canvas.width || 1));
            ring.style.width = d + 'px'; ring.style.height = d + 'px';
            ring.style.left = e.clientX + 'px'; ring.style.top = e.clientY + 'px';
            ring.style.display = 'block';
        };
        st.showRing = showRing;

        const pt = (e) => {
            const r = canvas.getBoundingClientRect();
            return { x: (e.clientX - r.left) * (canvas.width / r.width), y: (e.clientY - r.top) * (canvas.height / r.height) };
        };
        // Draw onto the ACTIVE layer (with its colour), then recomposite to the display.
        const drawStep = (a, b) => {
            const l = window.sdMask._active(st); if (!l) return;
            const c = l.canvas.getContext('2d');
            c.lineCap = 'round'; c.lineJoin = 'round'; c.strokeStyle = l.color; c.fillStyle = l.color;
            c.globalCompositeOperation = st.mode === 'eraser' ? 'destination-out' : 'source-over';
            if (!b) { c.beginPath(); c.arc(a.x, a.y, st.brush / 2, 0, Math.PI * 2); c.fill(); }
            else { c.lineWidth = st.brush; c.beginPath(); c.moveTo(a.x, a.y); c.lineTo(b.x, b.y); c.stroke(); }
            window.sdMask._composite(canvas);
        };

        st.down = (e) => {
            if (st.mode === 'cursor' || e.button > 0) return;
            if (!window.sdMask._active(st)) return;
            window.sdMask._pushUndo(st);   // global snapshot before the stroke
            st.drawing = true; st.last = pt(e); drawStep(st.last, null);
            try { canvas.setPointerCapture(e.pointerId); } catch { } e.preventDefault();
        };
        st.move = (e) => { showRing(e); if (!st.drawing) return; const p = pt(e); drawStep(st.last, p); st.last = p; e.preventDefault(); };
        st.up = () => { if (st.drawing) { st.drawing = false; window.sdMask._refreshPad(canvas); } };
        st.enter = (e) => { if (st.mode !== 'cursor') showRing(e); };
        st.leave = () => { ring.style.display = 'none'; };
        // Alt + wheel resizes the brush (and reports the new size back to .NET so the slider tracks it).
        st.wheel = (e) => {
            if (!e.altKey) return;
            e.preventDefault();
            st.brush = Math.max(5, Math.min(200, st.brush + (e.deltaY < 0 ? 4 : -4)));
            showRing(e);
            if (st.dotnetRef) { try { st.dotnetRef.invokeMethodAsync('OnBrushSizeChangedJs', st.brush); } catch { } }
        };
        // Ctrl/Cmd+Z undo, Ctrl/Cmd+Y or Ctrl/Cmd+Shift+Z redo — ignored while typing in a field.
        st.key = (e) => {
            if (!canvas.isConnected) return;
            const t = e.target;
            if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) return;
            if (!(e.ctrlKey || e.metaKey)) return;
            const z = (e.key === 'z' || e.key === 'Z'), y = (e.key === 'y' || e.key === 'Y');
            if (z && !e.shiftKey) { e.preventDefault(); window.sdMask.undo(canvas); }
            else if (y || (z && e.shiftKey)) { e.preventDefault(); window.sdMask.redo(canvas); }
        };

        canvas.addEventListener('pointerdown', st.down);
        canvas.addEventListener('pointermove', st.move);
        canvas.addEventListener('pointerup', st.up);
        canvas.addEventListener('pointercancel', st.up);
        canvas.addEventListener('pointerenter', st.enter);
        canvas.addEventListener('pointerleave', st.leave);
        canvas.addEventListener('wheel', st.wheel, { passive: false });
        window.addEventListener('keydown', st.key);
        window.addEventListener('resize', fit);

        // Seed the first layer, size everything to the image, paint and report to .NET.
        st.layers.push(window.sdMask._makeLayer(st, color || '#ff3b30'));
        st.activeId = st.layers[0].id;
        if (img.complete) fit(); else img.addEventListener('load', fit);
        styleRing();
        window.sdMask._composite(canvas);
        window.sdMask._notify(st);
    },

    // --- Layers ---
    _makeLayer: function (st, color, name) {
        const c = document.createElement('canvas');
        c.width = Math.max(1, st.w); c.height = Math.max(1, st.h);
        const id = st.nextId++;
        return { id, name: name || ('Layer ' + id), color: color || '#ff3b30', visible: true, canvas: c };
    },
    _active: function (st) { return st.layers.find(l => l.id === st.activeId) || null; },
    _composite: function (canvas) {
        const st = canvas && canvas.__mask; if (!st) return;
        const ctx = st.ctx; ctx.clearRect(0, 0, canvas.width, canvas.height);
        for (const l of st.layers) if (l.visible) { try { ctx.drawImage(l.canvas, 0, 0); } catch { } }
    },
    _notify: function (st) {
        if (!st.dotnetRef) return;
        const meta = st.layers.map(l => ({ id: l.id, name: l.name, color: l.color, visible: l.visible }));
        try { st.dotnetRef.invokeMethodAsync('OnLayersChanged', meta, st.activeId); } catch { }
    },

    addLayer: function (canvas, color, name) {
        const st = canvas && canvas.__mask; if (!st) return;
        window.sdMask._pushUndo(st);
        const l = window.sdMask._makeLayer(st, color || '#3b82f6', name);
        st.layers.push(l); st.activeId = l.id;
        if (st.styleRing) st.styleRing();
        window.sdMask._composite(canvas); window.sdMask._notify(st);
    },
    deleteLayer: function (canvas, id) {
        const st = canvas && canvas.__mask; if (!st || st.layers.length <= 1) return;
        const idx = st.layers.findIndex(l => l.id === id); if (idx < 0) return;
        window.sdMask._pushUndo(st);
        st.layers.splice(idx, 1);
        if (st.activeId === id) st.activeId = st.layers[Math.min(idx, st.layers.length - 1)].id;
        if (st.styleRing) st.styleRing();
        window.sdMask._composite(canvas); window.sdMask._refreshPad(canvas); window.sdMask._notify(st);
    },
    setActive: function (canvas, id) {
        const st = canvas && canvas.__mask; if (!st) return;
        if (st.layers.some(l => l.id === id)) { st.activeId = id; if (st.styleRing) st.styleRing(); window.sdMask._notify(st); }
    },
    renameLayer: function (canvas, id, name) {
        const st = canvas && canvas.__mask; if (!st) return;
        const l = st.layers.find(x => x.id === id); if (l) { l.name = name || l.name; window.sdMask._notify(st); }
    },
    setLayerColor: function (canvas, id, color) {
        const st = canvas && canvas.__mask; if (!st) return;
        const l = st.layers.find(x => x.id === id); if (!l) return;
        l.color = color || l.color;
        try { // recolour the layer's existing pixels, keeping their shape/alpha
            const c = l.canvas.getContext('2d');
            c.globalCompositeOperation = 'source-in'; c.fillStyle = l.color; c.fillRect(0, 0, l.canvas.width, l.canvas.height);
            c.globalCompositeOperation = 'source-over';
        } catch { }
        window.sdMask._composite(canvas);
        if (st.activeId === id && st.styleRing) st.styleRing();
        window.sdMask._notify(st);
    },
    setLayerVisible: function (canvas, id, vis) {
        const st = canvas && canvas.__mask; if (!st) return;
        const l = st.layers.find(x => x.id === id); if (!l) return;
        l.visible = !!vis;
        window.sdMask._composite(canvas); window.sdMask._refreshPad(canvas); window.sdMask._notify(st);
    },
    moveLayer: function (canvas, id, dir) {
        const st = canvas && canvas.__mask; if (!st) return;
        const idx = st.layers.findIndex(l => l.id === id); if (idx < 0) return;
        const to = idx + (dir > 0 ? 1 : -1);
        if (to < 0 || to >= st.layers.length) return;
        window.sdMask._pushUndo(st);
        const [l] = st.layers.splice(idx, 1); st.layers.splice(to, 0, l);
        window.sdMask._composite(canvas); window.sdMask._notify(st);
    },

    // --- Import a mask as a new layer ---
    _hexRgb: function (hex) {
        hex = (hex || '#ff3b30').replace('#', '');
        if (hex.length === 3) hex = hex.split('').map(c => c + c).join('');
        const n = parseInt(hex, 16) || 0;
        return { r: (n >> 16) & 255, g: (n >> 8) & 255, b: n & 255 };
    },
    // Build a new layer from an image. useAlpha=true derives the mask from the source's transparency
    // (transparent → masked by default); otherwise from luminance (white → masked). `invert` flips it.
    // Resolves false on error or when useAlpha is asked of a fully-opaque image (nothing to mask).
    _buildLayerFrom: function (canvas, dataUrl, name, useAlpha, invert, colored) {
        return new Promise((resolve) => {
            const st = canvas && canvas.__mask; if (!st) { resolve(false); return; }
            const img = new Image();
            img.onload = () => {
                try {
                    const l = window.sdMask._makeLayer(st, (window.sdMask._active(st) || {}).color || '#ff3b30', name);
                    const lc = l.canvas;
                    if (colored) {
                        // Inpaint-sketch: keep the uploaded image's own colours — draw it straight in
                        // rather than recolouring it to the active layer's single colour.
                        lc.getContext('2d').drawImage(img, 0, 0, lc.width, lc.height);
                    }
                    else {
                        const tmp = document.createElement('canvas'); tmp.width = lc.width; tmp.height = lc.height;
                        const tc = tmp.getContext('2d', { willReadFrequently: true });
                        tc.drawImage(img, 0, 0, lc.width, lc.height);
                        const idata = tc.getImageData(0, 0, lc.width, lc.height);
                        const d = idata.data, col = window.sdMask._hexRgb(l.color);
                        let anyTransparent = false;
                        for (let i = 0; i < d.length; i += 4) {
                            let a;
                            if (useAlpha) {
                                if (d[i + 3] < 255) anyTransparent = true;
                                a = invert ? d[i + 3] : (255 - d[i + 3]);   // default: transparent → masked
                            } else {
                                const lum = d[i + 3] === 0 ? 0 : (d[i] * 0.299 + d[i + 1] * 0.587 + d[i + 2] * 0.114);
                                a = invert ? (255 - lum) : lum;             // default: white → masked
                            }
                            d[i] = col.r; d[i + 1] = col.g; d[i + 2] = col.b; d[i + 3] = Math.round(a);
                        }
                        if (useAlpha && !anyTransparent) { resolve(false); return; }
                        lc.getContext('2d').putImageData(idata, 0, 0);
                    }
                    window.sdMask._pushUndo(st);
                    st.layers.push(l); st.activeId = l.id;
                    if (st.styleRing) st.styleRing();
                    window.sdMask._composite(canvas); window.sdMask._refreshPad(canvas); window.sdMask._notify(st);
                    resolve(true);
                } catch { resolve(false); }
            };
            img.onerror = () => resolve(false);
            img.src = dataUrl;
        });
    },
    importLayer: function (canvas, dataUrl, name, invert) { return window.sdMask._buildLayerFrom(canvas, dataUrl, name || 'Imported', false, invert, false); },
    importSketch: function (canvas, dataUrl, name) { return window.sdMask._buildLayerFrom(canvas, dataUrl, name || 'Sketch', false, false, true); },
    maskFromAlpha: function (canvas, dataUrl, name, invert) { return window.sdMask._buildLayerFrom(canvas, dataUrl, name || 'From alpha', true, invert); },

    // Invert a layer's mask: painted ↔ unpainted (alpha = 255 - alpha), filled with the layer's colour.
    invertLayer: function (canvas, id) {
        const st = canvas && canvas.__mask; if (!st) return;
        const l = st.layers.find(x => x.id === id) || window.sdMask._active(st); if (!l) return;
        try {
            window.sdMask._pushUndo(st);
            const c = l.canvas.getContext('2d', { willReadFrequently: true });
            const idata = c.getImageData(0, 0, l.canvas.width, l.canvas.height);
            const d = idata.data, col = window.sdMask._hexRgb(l.color);
            for (let i = 0; i < d.length; i += 4) { d[i] = col.r; d[i + 1] = col.g; d[i + 2] = col.b; d[i + 3] = 255 - d[i + 3]; }
            c.putImageData(idata, 0, 0);
            window.sdMask._composite(canvas); window.sdMask._refreshPad(canvas); window.sdMask._notify(st);
        } catch { }
    },

    // --- Global undo/redo: snapshots of every layer's pixels + metadata, restored as a whole ---
    _snap: function (st) {
        return {
            activeId: st.activeId,
            layers: st.layers.map(l => ({ id: l.id, name: l.name, color: l.color, visible: l.visible, data: l.canvas.toDataURL('image/png') }))
        };
    },
    _pushUndo: function (st) {
        try { st.undo.push(window.sdMask._snap(st)); if (st.undo.length > st.histLimit) st.undo.shift(); st.redo.length = 0; } catch { }
    },
    _restore: async function (canvas, snap) {
        const st = canvas && canvas.__mask; if (!st) return;
        const load = (src) => new Promise(res => { const i = new Image(); i.onload = () => res(i); i.onerror = () => res(null); i.src = src; });
        const layers = [];
        for (const ls of snap.layers) {
            const c = document.createElement('canvas'); c.width = Math.max(1, st.w); c.height = Math.max(1, st.h);
            if (ls.data) { const im = await load(ls.data); if (im) { try { c.getContext('2d').drawImage(im, 0, 0, c.width, c.height); } catch { } } }
            layers.push({ id: ls.id, name: ls.name, color: ls.color, visible: ls.visible, canvas: c });
        }
        st.layers = layers;
        st.activeId = snap.activeId;
        if (!window.sdMask._active(st) && layers.length) st.activeId = layers[0].id;
        window.sdMask._composite(canvas); window.sdMask._refreshPad(canvas);
        if (st.styleRing) st.styleRing();
        window.sdMask._notify(st);
    },
    undo: async function (canvas) {
        const st = canvas && canvas.__mask; if (!st || !st.undo.length) return;
        st.redo.push(window.sdMask._snap(st)); if (st.redo.length > st.histLimit) st.redo.shift();
        await window.sdMask._restore(canvas, st.undo.pop());
    },
    redo: async function (canvas) {
        const st = canvas && canvas.__mask; if (!st || !st.redo.length) return;
        st.undo.push(window.sdMask._snap(st)); if (st.undo.length > st.histLimit) st.undo.shift();
        await window.sdMask._restore(canvas, st.redo.pop());
    },

    setBrush: function (canvas, size) { if (canvas && canvas.__mask) canvas.__mask.brush = size; },
    // Back-compat: the .NET colour picker recolours the ACTIVE layer.
    setColor: function (canvas, color) {
        const st = canvas && canvas.__mask; if (!st) return;
        window.sdMask.setLayerColor(canvas, st.activeId, color);
    },
    // mode: 'brush' | 'eraser' | 'cursor'. In 'cursor' the canvas ignores pointer events entirely.
    setMode: function (canvas, mode) {
        const st = canvas && canvas.__mask;
        if (!st) return;
        st.mode = mode;
        canvas.style.pointerEvents = (mode === 'cursor') ? 'none' : 'auto';
        if (mode === 'cursor' && st.ring) st.ring.style.display = 'none';
        if (st.styleRing) st.styleRing();
    },
    // Clear the ACTIVE layer (undoable).
    clear: function (canvas) {
        const st = canvas && canvas.__mask; if (!st) return;
        const l = window.sdMask._active(st); if (!l) return;
        window.sdMask._pushUndo(st);
        try { l.canvas.getContext('2d').clearRect(0, 0, l.canvas.width, l.canvas.height); } catch { }
        window.sdMask._composite(canvas); window.sdMask._refreshPad(canvas); window.sdMask._notify(st);
    },
    isEmpty: function (canvas) {
        const st = canvas && canvas.__mask; if (!st) return true;
        for (const l of st.layers) {
            if (!l.visible) continue;
            try {
                const d = l.canvas.getContext('2d').getImageData(0, 0, l.canvas.width, l.canvas.height).data;
                for (let i = 3; i < d.length; i += 4) if (d[i] !== 0) return false;
            } catch { }
        }
        return true;
    },

    // --- "Only masked" crop preview (the box the backend will isolate, scale up, and paste back) ---
    _bbox: function (canvas) {
        try {
            const w = canvas.width, h = canvas.height;
            const d = canvas.getContext('2d').getImageData(0, 0, w, h).data;
            let minX = w, minY = h, maxX = -1, maxY = -1;
            for (let y = 0; y < h; y++) {
                for (let x = 0; x < w; x++) {
                    if (d[(y * w + x) * 4 + 3] !== 0) {
                        if (x < minX) minX = x; if (x > maxX) maxX = x;
                        if (y < minY) minY = y; if (y > maxY) maxY = y;
                    }
                }
            }
            return maxX < 0 ? null : { minX, minY, maxX, maxY };
        } catch { return null; }
    },
    setPaddingPreview: function (canvas, on, paddingNative, nativeW, nativeH) {
        const st = canvas && canvas.__mask; if (!st) return;
        st.padOn = !!on; st.padNative = paddingNative || 0; st.nativeW = nativeW || 0; st.nativeH = nativeH || 0;
        window.sdMask._refreshPad(canvas);
    },
    _refreshPad: function (canvas) {
        const st = canvas && canvas.__mask; if (!st || !st.padCanvas) return;
        const p = st.padCanvas, pc = p.getContext('2d');
        pc.clearRect(0, 0, p.width, p.height);
        if (!st.padOn) return;
        const bb = window.sdMask._bbox(canvas);
        if (!bb) return;
        const scale = st.nativeW ? (canvas.width / st.nativeW) : 1;
        const padPx = st.padNative * scale;
        let x = Math.max(0, bb.minX - padPx), y = Math.max(0, bb.minY - padPx);
        let w = Math.min(p.width - x, (bb.maxX - bb.minX) + padPx * 2);
        let h = Math.min(p.height - y, (bb.maxY - bb.minY) + padPx * 2);
        pc.strokeStyle = '#00e5ff'; pc.lineWidth = 2; pc.setLineDash([6, 4]);
        pc.strokeRect(x + 1, y + 1, Math.max(0, w - 2), Math.max(0, h - 2));
    },

    // Mask as raw base64 PNG: black background, white where painted (or inverted), regardless of the
    // brush display color. Capped at maxEdge on the long side — the backend resizes the mask to the init
    // anyway, so a full-res mask just makes toDataURL slow for high-res images.
    getMask: function (canvas, nativeW, nativeH, invert, maxEdge) {
        try {
            const st = canvas && canvas.__mask;
            if (!st || window.sdMask.isEmpty(canvas)) return null; // nothing painted → no mask
            let w = nativeW, h = nativeH;
            if (maxEdge && Math.max(w, h) > maxEdge) {
                const s = maxEdge / Math.max(w, h);
                w = Math.max(1, Math.round(w * s)); h = Math.max(1, Math.round(h * s));
            }
            const out = document.createElement('canvas');
            out.width = w; out.height = h;
            const o = out.getContext('2d');
            for (const l of st.layers) if (l.visible) o.drawImage(l.canvas, 0, 0, w, h); // union of layers
            o.globalCompositeOperation = 'source-in'; // recolor painted pixels to pure white
            o.fillStyle = '#fff'; o.fillRect(0, 0, w, h);
            o.globalCompositeOperation = 'destination-over'; // black behind everything else
            o.fillStyle = '#000'; o.fillRect(0, 0, w, h);
            o.globalCompositeOperation = 'source-over';
            if (invert) {
                o.globalCompositeOperation = 'difference';
                o.fillStyle = '#fff'; o.fillRect(0, 0, w, h);
                o.globalCompositeOperation = 'source-over';
            }
            return out.toDataURL('image/png').split(',').pop();
        } catch { return null; }
    },

    // Inpaint-sketch init: the original image with the painted colour layers composited on top in
    // z-order, at native resolution. Used as init_images[0] so the painted colours steer the region.
    getComposite: function (canvas, img, nativeW, nativeH, maxEdge) {
        try {
            const st = canvas && canvas.__mask;
            if (!st || window.sdMask.isEmpty(canvas)) return null;
            let w = nativeW, h = nativeH;
            if (maxEdge && Math.max(w, h) > maxEdge) {
                const s = maxEdge / Math.max(w, h);
                w = Math.max(1, Math.round(w * s)); h = Math.max(1, Math.round(h * s));
            }
            const out = document.createElement('canvas');
            out.width = w; out.height = h;
            const o = out.getContext('2d');
            o.drawImage(img, 0, 0, w, h);     // original
            for (const l of st.layers) if (l.visible) o.drawImage(l.canvas, 0, 0, w, h); // colour layers on top
            return out.toDataURL('image/png').split(',').pop();
        } catch { return null; }
    },

    // The painted colour layers ON THEIR OWN (no underlying image), transparent elsewhere — the colour
    // version of getMask. Used to save the sketch so it can be downloaded and re-uploaded with colours.
    getColoredMask: function (canvas, nativeW, nativeH, maxEdge) {
        try {
            const st = canvas && canvas.__mask;
            if (!st || window.sdMask.isEmpty(canvas)) return null;
            let w = nativeW, h = nativeH;
            if (maxEdge && Math.max(w, h) > maxEdge) {
                const s = maxEdge / Math.max(w, h);
                w = Math.max(1, Math.round(w * s)); h = Math.max(1, Math.round(h * s));
            }
            const out = document.createElement('canvas');
            out.width = w; out.height = h;
            const o = out.getContext('2d');
            for (const l of st.layers) if (l.visible) o.drawImage(l.canvas, 0, 0, w, h); // colour layers only
            return out.toDataURL('image/png').split(',').pop();
        } catch { return null; }
    },

    // Each painted (non-empty) layer on its own, with its name — so inpaint-sketch can save the layers
    // individually as masks. Returns [{ name, image(base64) }, …].
    getLayerImages: function (canvas, nativeW, nativeH, maxEdge) {
        try {
            const st = canvas && canvas.__mask;
            if (!st) return [];
            let w = nativeW, h = nativeH;
            if (maxEdge && Math.max(w, h) > maxEdge) {
                const s = maxEdge / Math.max(w, h);
                w = Math.max(1, Math.round(w * s)); h = Math.max(1, Math.round(h * s));
            }
            const result = [];
            for (const l of st.layers) {
                if (!l.visible) continue;
                const out = document.createElement('canvas');
                out.width = w; out.height = h;
                const o = out.getContext('2d');
                o.drawImage(l.canvas, 0, 0, w, h);
                // Skip layers with nothing painted on them.
                const d = o.getImageData(0, 0, w, h).data;
                let empty = true;
                for (let i = 3; i < d.length; i += 4) { if (d[i] !== 0) { empty = false; break; } }
                if (empty) continue;
                result.push({ name: l.name || ('Layer ' + (result.length + 1)), image: out.toDataURL('image/png').split(',').pop() });
            }
            return result;
        } catch { return []; }
    },

    // Poor-man's outpainting: place the image on a larger canvas and build a matching mask that is
    // white over the new border (to regenerate) and black over the original. Dimensions are rounded to
    // a multiple of 8 for the latent grid. Returns { image, mask, width, height } (raw base64).
    buildOutpaint: function (b64DataUrl, left, up, right, down, fillStyle) {
        return new Promise((resolve) => {
            const img = new Image();
            img.onload = () => {
                try {
                    const r8 = (v) => Math.max(8, Math.round(v / 8) * 8);
                    const nw = img.naturalWidth, nh = img.naturalHeight;
                    const W = r8(nw + left + right), H = r8(nh + up + down);
                    const ic = document.createElement('canvas'); ic.width = W; ic.height = H;
                    const ix = ic.getContext('2d');
                    // Base fill only covers the few px of rounding slack; the border is then filled by
                    // stretching the image's own edge pixels outward so the new space CONTINUES the image.
                    // A flat fill would make 'original' seeding a single flat color and give 'latent noise'
                    // nothing to build on — which is exactly the "just noise / one color" failure.
                    ix.fillStyle = fillStyle || '#7f7f7f'; ix.fillRect(0, 0, W, H);
                    ix.drawImage(img, left, up, nw, nh);
                    const innerR = left + nw, innerB = up + nh;
                    if (left > 0) ix.drawImage(img, 0, 0, 1, nh, 0, up, left, nh);              // left edge strip
                    if (right > 0) ix.drawImage(img, nw - 1, 0, 1, nh, innerR, up, right, nh);  // right edge strip
                    if (up > 0) ix.drawImage(img, 0, 0, nw, 1, left, 0, nw, up);                // top edge strip
                    if (down > 0) ix.drawImage(img, 0, nh - 1, nw, 1, left, innerB, nw, down);  // bottom edge strip
                    if (left > 0 && up > 0) ix.drawImage(img, 0, 0, 1, 1, 0, 0, left, up);                        // corners
                    if (right > 0 && up > 0) ix.drawImage(img, nw - 1, 0, 1, 1, innerR, 0, right, up);
                    if (left > 0 && down > 0) ix.drawImage(img, 0, nh - 1, 1, 1, 0, innerB, left, down);
                    if (right > 0 && down > 0) ix.drawImage(img, nw - 1, nh - 1, 1, 1, innerR, innerB, right, down);
                    const mc = document.createElement('canvas'); mc.width = W; mc.height = H;
                    const mx = mc.getContext('2d');
                    mx.fillStyle = '#fff'; mx.fillRect(0, 0, W, H);
                    mx.fillStyle = '#000'; mx.fillRect(left, up, nw, nh);
                    resolve({
                        image: ic.toDataURL('image/png').split(',').pop(),
                        mask: mc.toDataURL('image/png').split(',').pop(),
                        width: W, height: H
                    });
                } catch { resolve(null); }
            };
            img.onerror = () => resolve(null);
            img.src = b64DataUrl;
        });
    },

    destroy: function (canvas) {
        const st = canvas && canvas.__mask;
        if (!st) return;
        canvas.removeEventListener('pointerdown', st.down);
        canvas.removeEventListener('pointermove', st.move);
        canvas.removeEventListener('pointerup', st.up);
        canvas.removeEventListener('pointercancel', st.up);
        canvas.removeEventListener('pointerenter', st.enter);
        canvas.removeEventListener('pointerleave', st.leave);
        canvas.removeEventListener('wheel', st.wheel);
        window.removeEventListener('keydown', st.key);
        window.removeEventListener('resize', st.fit);
        if (st.ring && st.ring.parentNode) st.ring.parentNode.removeChild(st.ring);
        if (st.padCanvas && st.padCanvas.parentNode) st.padCanvas.parentNode.removeChild(st.padCanvas);
        delete canvas.__mask;
    }
};

// Drag controller for the fullscreen gallery's metadata bottom sheet. Following the finger by
// mutating transform directly (no SignalR per pointermove); only the committed open/closed result
// is sent back to .NET (SetMetaOpen), which toggles the .open class to settle the animation.
window.sdMetaSheet = {
    init: function (sheet, peek, grabber, dotnetRef) {
        if (!sheet) return;
        if (sheet.__sdSheet) window.sdMetaSheet.destroy(sheet);

        const st = { dotnetRef, dragging: false, moved: false, pid: null, startY: 0, base: 0, h: 1, fromPeek: false };
        sheet.__sdSheet = st;
        const THRESH = 60; // px of travel needed to flip a tap into a swipe decision

        const onMove = (e) => {
            if (!st.dragging || e.pointerId !== st.pid) return;
            const dy = e.clientY - st.startY;
            if (Math.abs(dy) > 4) st.moved = true;
            let y = Math.max(0, Math.min(st.h, st.base + dy));
            sheet.style.transform = `translateY(${y}px)`;
            e.preventDefault();
        };

        const onUp = (e) => {
            if (!st.dragging) return;
            st.dragging = false;
            window.removeEventListener('pointermove', onMove, true);
            window.removeEventListener('pointerup', onUp, true);
            window.removeEventListener('pointercancel', onUp, true);

            const dy = e.clientY - st.startY;
            // From the peek: a tap or upward swipe opens; only a firm downward drag stays closed.
            // From the grabber: a tap or downward swipe closes; only a firm upward drag stays open.
            const wantOpen = st.fromPeek ? (dy < THRESH) : (dy < -THRESH);

            // Settle to the target with a transition, then hand off to the .open class and clear
            // the inline transform so the next drag starts clean.
            sheet.style.transition = 'transform 280ms cubic-bezier(0.22, 1, 0.36, 1)';
            sheet.style.transform = `translateY(${wantOpen ? 0 : st.h}px)`;
            setTimeout(() => { sheet.style.transition = ''; sheet.style.transform = ''; }, 320);

            try { st.dotnetRef.invokeMethodAsync('SetMetaOpen', wantOpen); } catch { }
        };

        const onDown = (fromPeek) => (e) => {
            if (e.pointerType === 'mouse' && e.button !== 0) return;
            st.dragging = true; st.moved = false; st.fromPeek = fromPeek;
            st.pid = e.pointerId;
            st.startY = e.clientY;
            st.h = sheet.offsetHeight || 1;
            st.base = sheet.classList.contains('open') ? 0 : st.h;
            sheet.style.transition = 'none';
            e.preventDefault();
            e.stopPropagation();
            window.addEventListener('pointermove', onMove, true);
            window.addEventListener('pointerup', onUp, true);
            window.addEventListener('pointercancel', onUp, true);
        };

        st.peekDown = onDown(true);
        st.grabDown = onDown(false);
        st.header = sheet.querySelector('.meta-sheet-header');
        if (peek) peek.addEventListener('pointerdown', st.peekDown);
        if (grabber) grabber.addEventListener('pointerdown', st.grabDown);
        if (st.header) st.header.addEventListener('pointerdown', st.grabDown);
        st.peek = peek; st.grabber = grabber; st.onMove = onMove; st.onUp = onUp;
    },

    destroy: function (sheet) {
        const st = sheet && sheet.__sdSheet;
        if (!st) return;
        window.removeEventListener('pointermove', st.onMove, true);
        window.removeEventListener('pointerup', st.onUp, true);
        window.removeEventListener('pointercancel', st.onUp, true);
        if (st.peek) st.peek.removeEventListener('pointerdown', st.peekDown);
        if (st.grabber) st.grabber.removeEventListener('pointerdown', st.grabDown);
        if (st.header) st.header.removeEventListener('pointerdown', st.grabDown);
        delete sheet.__sdSheet;
    }
};

// Left/Right arrow keys page the Civitai browser — but only while that tab is actually visible
// and nothing is in the way (no dialog open, no menu/select open, not typing in a field).
window.civPager = {
    _ref: null,
    _el: null,
    _handler: null,
    register: (dotnetRef, el) => {
        window.civPager.unregister();
        window.civPager._ref = dotnetRef;
        window.civPager._el = el;

        const handler = (e) => {
            if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return;

            // Only when the Civitai tab is on screen (its panel is hidden -> offsetParent is null).
            const root = window.civPager._el;
            if (!root || root.offsetParent === null) return;

            // Don't hijack typing, open menus/selects, or anything behind a dialog.
            const a = document.activeElement;
            const tag = a ? a.tagName : '';
            if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || (a && a.isContentEditable)) return;
            if (document.querySelector('.mud-dialog')) return;       // a dialog is up
            if (document.querySelector('.mud-popover-open')) return;  // a menu/select is open
            if (!window.civPager._ref) return;

            e.preventDefault();
            window.civPager._ref.invokeMethodAsync('OnPagerArrow', e.key === 'ArrowRight' ? 1 : -1);
        };
        window.civPager._handler = handler;
        window.addEventListener('keydown', handler);
    },
    unregister: () => {
        if (window.civPager._handler) {
            window.removeEventListener('keydown', window.civPager._handler);
            window.civPager._handler = null;
        }
        window.civPager._ref = null;
        window.civPager._el = null;
    }
};

window.sdDropHover = {
    attach: (element) => {
        if (!element) return;

        const set = (on) => element.classList.toggle("sd-drop-hover", on);

        const enter = () => set(true);
        const over  = () => set(true);

        const leave = (e) => {
            const to = e.relatedTarget;
            if (!to || !element.contains(to)) set(false);
        };

        const drop = () => set(false);

        element.__sdHover = { enter, over, leave, drop };

        element.addEventListener("dragenter", enter, true);
        element.addEventListener("dragover",  over,  true);
        element.addEventListener("dragleave", leave, true);
        element.addEventListener("drop",      drop,  true);
    },

    detach: (element) => {
        if (!element || !element.__sdHover) return;
        const { enter, over, leave, drop } = element.__sdHover;
        element.removeEventListener("dragenter", enter, true);
        element.removeEventListener("dragover", over, true);
        element.removeEventListener("dragleave", leave, true);
        element.removeEventListener("drop", drop, true);
        delete element.__sdHover;
    }
};
