(() => {
    'use strict';

    const root = document.getElementById('command-live-root');
    if (!root) return;

    const state = {
        mode: root.dataset.commandLiveMode === 'historical' ? 'historical' : 'live',
        connection: 'connecting'
    };

    const elements = {
        liveState: document.getElementById('command-live-state'),
        banner: document.getElementById('command-live-banner'),
        bannerTitle: document.getElementById('command-live-banner-title')
    };

    let connection = null;

    function normalise(value) {
        return String(value || '').trim().toLowerCase();
    }

    function sameGuid(left, right) {
        return normalise(left) === normalise(right);
    }

    function setConnection(next) {
        state.connection = next;
        syncLiveState();
    }

    function syncLiveState() {
        if (!elements.liveState) return;
        elements.liveState.classList.remove('connected', 'connecting', 'reconnecting', 'offline', 'frozen');

        let className;
        let text;
        if (state.mode === 'historical') {
            className = state.connection === 'offline' ? 'offline' : 'frozen';
            text = state.connection === 'offline' ? 'Historical · offline' : 'Historical · frozen';
        } else if (state.connection === 'connected') {
            className = 'connected';
            text = 'Live transitions';
        } else if (state.connection === 'reconnecting') {
            className = 'reconnecting';
            text = 'Reconnecting';
        } else if (state.connection === 'offline') {
            className = 'offline';
            text = 'Live disconnected';
        } else {
            className = 'connecting';
            text = 'Connecting';
        }

        elements.liveState.classList.add(className);
        const label = elements.liveState.querySelector('strong');
        if (label) label.textContent = text;
    }

    function showBanner(message) {
        if (!elements.banner || !elements.bannerTitle) return;
        elements.bannerTitle.textContent = message;
        elements.banner.hidden = false;
    }

    function currentWindow() {
        return document.getElementById('Window')?.value || '7d';
    }

    function currentSearch() {
        return document.getElementById('Search')?.value.trim() || '';
    }

    function inCurrentWindow(createdAt) {
        const created = Date.parse(createdAt);
        if (!Number.isFinite(created)) return true;
        const now = Date.now();
        switch (currentWindow()) {
            case '24h': return created >= now - 24 * 60 * 60 * 1000;
            case '7d': return created >= now - 7 * 24 * 60 * 60 * 1000;
            case '30d': return created >= now - 30 * 24 * 60 * 60 * 1000;
            default: return true;
        }
    }

    function matchesStableFilters(event) {
        const componentFilter = root.dataset.commandComponentFilter;
        const typeFilter = root.dataset.commandTypeFilter;
        if (componentFilter && !sameGuid(componentFilter, event.componentId)) return false;
        if (typeFilter && normalise(typeFilter) !== normalise(event.type)) return false;
        if (!inCurrentWindow(event.createdAt)) return false;
        return true;
    }

    function eventMatchesSearch(event) {
        const search = normalise(currentSearch());
        if (!search) return true;
        const haystack = [
            event.component,
            event.environment,
            event.requestedBy,
            event.error,
            event.resultJson,
            event.type,
            event.status
        ].map(normalise).join(' ');
        return haystack.includes(search);
    }

    function eventMatchesStatusFilter(event) {
        const statusFilter = root.dataset.commandStatusFilter;
        return !statusFilter || normalise(statusFilter) === normalise(event.status);
    }

    function mayMatchCurrentTextView(event) {
        // New command notifications can be captured before the Component navigation is loaded.
        // With a free-text filter we therefore prefer one harmless refresh prompt over
        // incorrectly hiding a command that might match after full SQL projection.
        return Boolean(currentSearch()) || eventMatchesSearch(event);
    }

    function handleCommandChanged(event) {
        if (!event || !event.commandId || !matchesStableFilters(event)) return;

        const row = root.querySelector(`[data-command-id="${CSS.escape(String(event.commandId))}"]`);

        if (state.mode === 'historical') {
            if (row || (eventMatchesStatusFilter(event) && mayMatchCurrentTextView(event))) {
                showBanner('Command activity changed outside this frozen history snapshot');
            }
            return;
        }

        if (!row) {
            if (eventMatchesStatusFilter(event) && mayMatchCurrentTextView(event)) {
                showBanner('New command activity may match this view');
            }
            return;
        }

        const previousStatus = row.dataset.commandStatus;
        patchRow(row, event);

        if (!eventMatchesStatusFilter(event)) {
            row.classList.add('command-live-filter-mismatch');
            showBanner(`${event.type} moved from ${previousStatus || 'this view'} to ${event.status}`);
        } else {
            row.classList.remove('command-live-filter-mismatch');
        }
    }

    function patchRow(row, event) {
        row.dataset.commandStatus = event.status;
        row.dataset.commandType = event.type;
        row.dataset.commandComponentId = event.componentId;

        const status = row.querySelector('[data-command-status-label]');
        if (status) {
            status.textContent = event.status;
            status.className = `command-state ${normalise(event.status)}`;
            status.dataset.commandStatusLabel = '';
        }

        const delivery = row.querySelector('[data-command-delivery]');
        if (delivery) {
            const attempts = document.createElement('strong');
            attempts.textContent = `${event.deliveryAttempts} attempt(s)`;
            const children = [attempts];
            if (event.leaseExpiresAt) {
                const lease = document.createElement('small');
                lease.textContent = `lease until ${utcTime(event.leaseExpiresAt)} UTC`;
                children.push(lease);
            }
            delivery.replaceChildren(...children);
        }

        const result = row.querySelector('[data-command-result]');
        if (result) renderResult(result, event);

        const actions = row.querySelector('[data-command-actions]');
        if (actions && isTerminal(event.status)) {
            actions.replaceChildren();
        }

        row.classList.remove('command-live-updated');
        void row.offsetWidth;
        row.classList.add('command-live-updated');
        window.setTimeout(() => row.classList.remove('command-live-updated'), 1600);
    }

    function renderResult(cell, event) {
        if (event.error) {
            const error = document.createElement('span');
            error.className = 'command-error';
            error.textContent = event.error;
            cell.replaceChildren(error);
            return;
        }

        if (event.resultJson) {
            const details = document.createElement('details');
            const summary = document.createElement('summary');
            summary.textContent = 'Result';
            const pre = document.createElement('pre');
            pre.textContent = event.resultJson;
            details.append(summary, pre);
            cell.replaceChildren(details);
            return;
        }

        const value = document.createElement('span');
        value.textContent = event.completedAt ? `${utcDateTime(event.completedAt)} UTC` : '—';
        cell.replaceChildren(value);
    }

    function isTerminal(status) {
        return ['succeeded', 'failed', 'rejected', 'cancelled', 'expired'].includes(normalise(status));
    }

    function utcTime(value) {
        const date = new Date(value);
        if (Number.isNaN(date.getTime())) return String(value);
        return date.toISOString().slice(11, 19);
    }

    function utcDateTime(value) {
        const date = new Date(value);
        if (Number.isNaN(date.getTime())) return String(value);
        return date.toISOString().replace('T', ' ').slice(0, 19);
    }

    async function startSignalR() {
        if (!window.signalR) {
            setConnection('offline');
            return;
        }

        connection = new signalR.HubConnectionBuilder()
            .withUrl('/hubs/monitor')
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .build();

        connection.on('CommandChanged', handleCommandChanged);
        connection.onreconnecting(() => setConnection('reconnecting'));
        connection.onreconnected(async () => {
            setConnection('connected');
            try {
                await connection.invoke('WatchCommands');
                showBanner('Connection restored · refresh to reconcile missed command activity');
            } catch (error) {
                console.warn('Command live group could not be restored.', error);
            }
        });
        connection.onclose(() => {
            setConnection('offline');
            window.setTimeout(connect, 5000);
        });

        async function connect() {
            if (connection.state !== signalR.HubConnectionState.Disconnected) return;
            try {
                setConnection('connecting');
                await connection.start();
                await connection.invoke('WatchCommands');
                setConnection('connected');
            } catch (error) {
                setConnection('offline');
                console.warn('Command live SignalR connection failed.', error);
                window.setTimeout(connect, 5000);
            }
        }

        await connect();
    }

    elements.banner?.addEventListener('click', () => window.location.reload());

    syncLiveState();
    startSignalR();
})();
