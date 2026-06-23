window.loraGridMeasure = {
    getWidth: (el) => {
        try {
            if (!el) return 0;
            const r = el.getBoundingClientRect();
            return r ? r.width : 0;
        } catch (e) { return 0; }
    }
};