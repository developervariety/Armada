// Armada Dashboard - Partial view loader
// This module is loaded via <script> tag and attaches to window.ArmadaModules

window.ArmadaModules = window.ArmadaModules || {};

window.ArmadaModules.partialLoader = {
    _partialCache: {},

    async loadModalsPartial() {
        let container = document.getElementById('modals-container');
        if (!container) return;

        let url = '/dashboard/views/modals.html';
        try {
            let response = await fetch(url);
            if (!response.ok) return;
            let html = await response.text();
            container.innerHTML = html;
            try { Alpine.initTree(container); } catch (e) { console.warn('[Armada] modals initTree error:', e); }
        } catch (e) {
            console.warn('[Armada] Failed to load modals partial:', e);
        }
    },

    async loadViewPartial(viewName) {
        // Look for a view-specific container first, then fall back to generic container
        let container = document.getElementById('view-' + viewName) || document.getElementById('view-container');
        if (!container) return;

        // Skip if already loaded into this container
        if (container.dataset.partialLoaded === viewName) return;

        let html = this._partialCache[viewName];

        // Fetch if not cached
        if (html === undefined) {
            let url = '/dashboard/views/' + viewName + '.html';
            try {
                let response = await fetch(url);
                if (!response.ok) {
                    this._partialCache[viewName] = '';
                    return;
                }
                html = await response.text();
                this._partialCache[viewName] = html;
            } catch (e) {
                console.warn('[Armada] Failed to fetch partial:', viewName, e);
                return;
            }
        }

        // Empty partial means no file exists for this view
        if (!html) return;

        // Inject HTML into the container
        container.innerHTML = html;
        container.dataset.partialLoaded = viewName;

        // Initialize Alpine directives on the new content.
        // IMPORTANT: do NOT clear the container on error -- visible HTML
        // without Alpine reactivity is better than a blank white page.
        try {
            Alpine.initTree(container);
        } catch (e) {
            console.warn('[Armada] initTree error for view "' + viewName + '":', e);
        }
    }
};
