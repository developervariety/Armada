// Armada Dashboard - WebSocket connection and message handling
// This module is loaded via <script> tag and attaches to window.ArmadaModules

window.ArmadaModules = window.ArmadaModules || {};

window.ArmadaModules.websocket = {
    connectWebSocket() {
        try {
            let WS_PROTOCOL = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
            let wsUrl = WS_PROTOCOL + '//' + window.location.host + '/ws';
            console.log('Connecting WebSocket to', wsUrl);
            this.ws = new WebSocket(wsUrl);
            this.ws.onopen = () => {
                this.wsConnected = true;
                this.connected = true;
                this.wsStreamReady = false;
                this.wsPendingStreamId = this.wsStreamId || null;
                this.wsPendingCursor = Number.isFinite(this.wsCursor) ? this.wsCursor : null;
                console.log('WebSocket connected');
                const subscribe = { Route: 'subscribe' };
                if (this.wsStreamId && Number.isFinite(this.wsCursor)) {
                    subscribe.streamId = this.wsStreamId;
                    subscribe.cursor = this.wsCursor;
                }
                this.ws.send(JSON.stringify(subscribe));
            };
            this.ws.onmessage = (evt) => {
                try {
                    const data = this.toCamel(JSON.parse(evt.data));
                    const isControl = ['event.gap', 'status.snapshot', 'stream.ready'].includes(data.type);
                    if (!isControl && data.streamId && Number.isFinite(data.cursor)) {
                        const sameStream = this.wsPendingStreamId === data.streamId;
                        if (sameStream && Number.isFinite(this.wsPendingCursor) && data.cursor > this.wsPendingCursor + 1) {
                            console.warn('WebSocket event gap detected; reconnecting for authoritative state');
                            this.ws?.close();
                            return;
                        }
                        this.wsPendingStreamId = data.streamId;
                        this.wsPendingCursor = sameStream ? Math.max(this.wsPendingCursor || 0, data.cursor) : data.cursor;
                        if (this.wsStreamReady) {
                            this.wsStreamId = this.wsPendingStreamId;
                            this.wsCursor = this.wsPendingCursor;
                        }
                    }
                    this.handleWsMessage(data);
                } catch (e) { }
            };
            this.ws.onclose = () => {
                this.wsConnected = false;
                this.connected = this.apiConnected;
                setTimeout(() => this.connectWebSocket(), 3000);
            };
            this.ws.onerror = (e) => {
                console.warn('WebSocket error:', e);
                this.wsConnected = false;
                this.connected = this.apiConnected;
            };
        } catch (e) {
            console.warn('WebSocket connection failed:', e);
            this.wsConnected = false;
            this.connected = this.apiConnected;
        }
    },

    handleWsMessage(data) {
        if (data.type === 'event.gap') {
            this.wsStreamReady = false;
            console.warn('WebSocket replay gap:', data.data || data.message || 'unknown');
            this.refresh();
            return;
        }
        if (data.type === 'stream.ready') {
            const validBoundary = data.streamId && data.streamId === this.wsPendingStreamId &&
                Number.isFinite(data.cursor) && data.cursor === this.wsPendingCursor;
            if (!validBoundary) {
                console.warn('Invalid WebSocket ready boundary; reconnecting');
                this.ws?.close();
                return;
            }
            this.wsStreamId = this.wsPendingStreamId;
            this.wsCursor = this.wsPendingCursor;
            this.wsStreamReady = true;
            return;
        }
        if (data.type === 'status.snapshot') {
            if (data.streamId && Number.isFinite(data.cursor)) {
                this.wsPendingStreamId = data.streamId;
                this.wsPendingCursor = data.cursor;
            }
            this.status = data.data?.status || data.data || this.status;
            // A snapshot is the authoritative reconnect boundary. Refresh the current
            // view so entity lists cannot keep state that changed while disconnected.
            this.refresh();
            return;
        }
        if (data.type && (
            data.type.includes('changed') || data.type.includes('mission') ||
            data.type.includes('captain') || data.type.includes('voyage') ||
            data.type.includes('signal') || data.type.includes('merge') ||
            data.type.includes('event')
        )) {
            this.refresh();
            // Refresh current view data
            if (this.view === 'signals') this.loadSignals();
            if (this.view === 'events') this.loadEvents();
            if (this.view === 'merge-queue') this.loadMergeQueue();
            if (this.view === 'missions') this.loadMissions();
        }

        // Toast notifications for mission state changes
        if (data.type === 'mission.changed' && data.data) {
            let m = data.data;
            if (m.status) {
                this._notifyStateChange('Mission', m.id, m.title, m.status);
            }
        }

        // Toast notifications for voyage state changes
        if (data.type === 'voyage.changed' && data.data) {
            let v = data.data;
            if (v.status) {
                this._notifyStateChange('Voyage', v.id, v.title, v.status);
            }
        }

        // Captain state changes
        if (data.type === 'captain.changed' && data.data) {
            let c = data.data;
            if (c.id && c.state) {
                this._notifyStateChange('Captain', c.id, c.name || c.id, c.state);
            }
        }
    },
};
