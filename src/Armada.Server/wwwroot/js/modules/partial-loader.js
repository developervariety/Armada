// Armada Dashboard - Partial view loader
// This module is loaded via <script> tag and attaches to window.ArmadaModules
//
// Alpine.js 3.x has a built-in MutationObserver that auto-initializes Alpine
// directives on newly added DOM elements. We MUST NOT call Alpine.initTree()
// explicitly, as that causes double-initialization which corrupts x-for
// templates and breaks reactivity.
//
// We defer HTML injection with setTimeout(0) so it runs AFTER Alpine has
// processed the reactive x-show changes triggered by navigate().

window.ArmadaModules = window.ArmadaModules || {};

window.ArmadaModules.partialLoader = {
    _partialCache: {},

    async loadModalsPartial() {
        let container = document.getElementById('modals-container');
        if (!container) return;

        try {
            let response = await fetch('/dashboard/views/modals.html');
            if (!response.ok) return;
            container.innerHTML = await response.text();
        } catch (e) { /* network error */ }
    },

    async loadViewPartial(viewName) {
        let container = document.getElementById('view-' + viewName) || document.getElementById('view-container');
        if (!container) return;

        // Already loaded — Alpine's MutationObserver initialized it on first inject
        if (container.dataset.partialLoaded === viewName) return;

        // Fetch (or use cache)
        let html = this._partialCache[viewName];
        if (html === undefined) {
            try {
                let response = await fetch('/dashboard/views/' + viewName + '.html');
                if (!response.ok) { this._partialCache[viewName] = ''; return; }
                html = await response.text();
                this._partialCache[viewName] = html;
            } catch (e) { return; }
        }
        if (!html) return;

        // Yield to the event loop so Alpine's reactive effects (x-show toggles
        // from navigate setting this.view) complete before we inject HTML.
        // This ensures the container is visible when MutationObserver processes
        // the new elements.
        await new Promise(resolve => setTimeout(resolve, 0));

        // Inject HTML — Alpine's MutationObserver auto-initializes directives.
        // Do NOT call Alpine.initTree() — it causes double-init that corrupts
        // x-for templates and breaks the component.
        container.innerHTML = html;
        container.dataset.partialLoaded = viewName;
    }
};
