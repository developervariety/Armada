// Armada Dashboard - Partial view loader
// This module is loaded via <script> tag and attaches to window.ArmadaModules

window.ArmadaModules = window.ArmadaModules || {};

window.ArmadaModules.partialLoader = {
    _partialCache: {},
    _loadedViews: {},

    /// <summary>
    /// Inject HTML into a container and let Alpine initialize the new elements.
    /// Uses Alpine.mutateDom to suppress the MutationObserver during injection,
    /// then explicitly initializes each child via Alpine.initTree.
    /// </summary>
    _injectAndInit(container, html) {
        // Suppress Alpine's MutationObserver during DOM mutation to avoid
        // double-initialization (observer auto-init + explicit initTree).
        if (typeof Alpine !== 'undefined' && Alpine.mutateDom) {
            Alpine.mutateDom(() => {
                container.innerHTML = html;
            });
        } else {
            container.innerHTML = html;
        }

        // Explicitly initialize each new child within the parent x-data scope.
        // Use Array.from to snapshot the live HTMLCollection.
        try {
            let children = Array.from(container.children);
            for (let i = 0; i < children.length; i++) {
                Alpine.initTree(children[i]);
            }
        } catch (e) {
            // Log but do not clear the container -- partial HTML is still
            // visible even without Alpine reactivity, which is better than blank.
            console.warn('[Armada] Alpine.initTree failed for partial:', e);
        }
    },

    async loadModalsPartial() {
        let container = document.getElementById('modals-container');
        if (!container) return;

        let url = '/dashboard/views/modals.html';
        try {
            let response = await fetch(url);
            if (!response.ok) return;
            let html = await response.text();
            this._injectAndInit(container, html);
        } catch (e) {
            // Network error -- modals will not be available
        }
    },

    async loadViewPartial(viewName) {
        // Look for a view-specific container first, then fall back to generic container
        let container = document.getElementById('view-' + viewName) || document.getElementById('view-container');
        if (!container) return;

        // Skip if this view's partial has already been loaded into this container
        if (this._loadedViews[viewName]) return;

        // Check cache
        let cached = this._partialCache[viewName];
        if (cached !== undefined) {
            if (cached === '') return; // No partial exists for this view
            this._injectAndInit(container, cached);
            this._loadedViews[viewName] = true;
            return;
        }

        let url = '/dashboard/views/' + viewName + '.html';
        try {
            let response = await fetch(url);
            if (!response.ok) {
                // No partial for this view -- that is fine, not all views have partials yet
                this._partialCache[viewName] = '';
                return;
            }
            let html = await response.text();
            this._partialCache[viewName] = html;
            this._injectAndInit(container, html);
            this._loadedViews[viewName] = true;
        } catch (e) {
            // Network error -- leave container as-is rather than clearing it
            console.warn('[Armada] Failed to load partial:', viewName, e);
        }
    }
};
