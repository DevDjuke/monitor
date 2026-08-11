(() => {
    'use strict';

    const elements = {
        panel: document.getElementById('runs-panel'),
        loading: document.getElementById('runs-loading'),
        empty: document.getElementById('runs-empty'),
        tableWrap: document.getElementById('runs-table-wrap'),
        body: document.getElementById('runs-body'),
        pagination: document.getElementById('runs-pagination'),
        pageLabel: document.getElementById('runs-page-label'),
        newer: document.getElementById('runs-newer'),
        older: document.getElementById('runs-older'),
        newBanner: document.getElementById('runs-new-banner'),
        newCount: document.getElementById('runs-new-count'),
        liveState: document.getElementById('runs-live-state'),
        search: document.getElementById('runs-search'),
        component: document.getElementById('runs-component'),
        status: document.getElementById('runs-status'),
        environment: document.getElementById('runs-environment'),
        model: document.getElementById('runs-model'),
        from: document.getElementById('runs-from'),
        to: document.getElementById('runs-to'),
        pageSize: document.getElementById('runs-page-size'),
        clear: document.getElementById('runs-clear')
    };

    if (!elements.panel) return;

    const state = {
        cursor: null,
        cursorHistory: [],
        nextCursor: null,
        pendingChanges: 0,
        requestVersion: 0,
        refreshTimer: null,
        searchTimer: null
    };

    const numberFormat = new Intl.NumberFormat();
    const startedFormat = new Intl.DateTimeFormat(undefined, {
        year: 'numeric',
        month: 'short',
        day: '2-digit',
        hour: '2-digit',
        minute: '2-digit',
        second: '2-digit'
    });

    function getFilters() {
        return {
            search: elements.search.value.trim(),
            componentId: elements.component.value,
            status: elements.status.value,
            environment: elements.environment.value,
            model: elements.model.value,
            from: elements.from.value,
            to: elements.to.value,
            pageSize: Number(elements.pageSize.value) || 50
        };
    }

    function readUrlFilters() {
        const params = new URLSearchParams(window.location.search);
        return {
            search: params.get('search') || '',
            componentId: params.get('componentId') || '',
            status: params.get('status') || '',
            environment: params.get('environment') || '',
            model: params.get('model') || '',
            from: params.get('from') || '',
            to: params.get('to') || '',
            pageSize: Number(params.get('pageSize')) || 50
        };
    }

    function applyUrlFilters(filters) {
        elements.search.value = filters.search;
        elements.status.value = [...elements.status.options].some(option => option.value === filters.status)
            ? filters.status
            : '';
        elements.from.value = filters.from;
        elements.to.value = filters.to;
        elements.pageSize.value = ['25', '50', '100'].includes(String(filters.pageSize))
            ? String(filters.pageSize)
            : '50';

        for (const [select, value] of [
            [elements.component, filters.componentId],
            [elements.environment, filters.environment],
            [elements.model, filters.model]
        ]) {
            select.value = [...select.options].some(option => option.value === value) ? value : '';
        }
    }

    function syncBrowserUrl() {
        const filters = getFilters();
        const params = new URLSearchParams();
        if (filters.search) params.set('search', filters.search);
        if (filters.componentId) params.set('componentId', filters.componentId);
        if (filters.status) params.set('status', filters.status);
        if (filters.environment) params.set('environment', filters.environment);
        if (filters.model) params.set('model', filters.model);
        if (filters.from) params.set('from', filters.from);
        if (filters.to) params.set('to', filters.to);
        if (filters.pageSize !== 50) params.set('pageSize', String(filters.pageSize));

        const query = params.toString();
        window.history.replaceState(null, '', query ? `/runs?${query}` : '/runs');
        document.dispatchEvent(new CustomEvent('monitor:saved-view-url-changed'));
    }

    function localDayIso(value, addDay) {
        if (!value) return null;
        const parts = value.split('-').map(Number);
        if (parts.length !== 3 || parts.some(Number.isNaN)) return null;
        const date = new Date(parts[0], parts[1] - 1, parts[2] + (addDay ? 1 : 0), 0, 0, 0, 0);
        return date.toISOString();
    }

    function buildQueryUrl() {
        const filters = getFilters();
        const params = new URLSearchParams();
        params.set('pageSize', String(filters.pageSize));

        if (state.cursor) params.set('before', state.cursor);
        if (filters.componentId) params.set('componentId', filters.componentId);
        if (filters.status) params.set('status', filters.status);
        if (filters.environment) params.set('environment', filters.environment);
        if (filters.model) params.set('model', filters.model);
        if (filters.search) params.set('search', filters.search);

        const from = localDayIso(filters.from, false);
        const to = localDayIso(filters.to, true);
        if (from) params.set('from', from);
        if (to) params.set('to', to);

        return `/api/runs/query?${params.toString()}`;
    }

    async function fetchJson(url) {
        const response = await fetch(url, {
            headers: { 'Accept': 'application/json' },
            credentials: 'same-origin'
        });

        if (response.status === 401) {
            window.location.assign('/account/login?returnUrl=%2Fruns');
            throw new Error('Authentication required.');
        }

        if (!response.ok) {
            const text = await response.text();
            throw new Error(text || `Request failed with ${response.status}.`);
        }

        return response.json();
    }

    async function loadOptions() {
        try {
            const options = await fetchJson('/api/runs/options');
            fillSelect(elements.component, options.components, item => item.id, item => `${item.name} · ${item.environment}`);
            fillSelect(elements.environment, options.environments, item => item, item => item);
            fillSelect(elements.model, options.models, item => item, item => item);
        } catch (error) {
            console.warn('Run filter options could not be loaded.', error);
        }
    }

    function fillSelect(select, items, valueSelector, labelSelector) {
        const current = select.value;
        const first = select.options[0];
        select.replaceChildren(first);

        for (const item of items ?? []) {
            const option = document.createElement('option');
            option.value = String(valueSelector(item));
            option.textContent = labelSelector(item);
            select.append(option);
        }

        if ([...select.options].some(option => option.value === current)) {
            select.value = current;
        }
    }

    function ensureOption(select, value, label) {
        if (!value) return;
        const normalized = String(value);
        if ([...select.options].some(option => option.value === normalized)) return;

        const option = document.createElement('option');
        option.value = normalized;
        option.textContent = label;
        select.append(option);
    }

    async function loadRuns({ quiet = false } = {}) {
        const version = ++state.requestVersion;
        elements.panel.setAttribute('aria-busy', 'true');
        elements.panel.classList.add('is-refreshing');

        if (!quiet && elements.body.children.length === 0) {
            elements.loading.hidden = false;
            elements.loading.textContent = 'Loading runs…';
            elements.empty.hidden = true;
            elements.tableWrap.hidden = true;
            elements.pagination.hidden = true;
        }

        try {
            const result = await fetchJson(buildQueryUrl());
            if (version !== state.requestVersion) return;

            state.nextCursor = result.nextCursor ? String(result.nextCursor) : null;
            renderRows(result.items ?? []);
            updatePagination();
            clearPendingChanges();
        } catch (error) {
            if (version !== state.requestVersion) return;
            elements.loading.hidden = false;
            elements.loading.textContent = `Could not load runs: ${normaliseError(error)}`;
            if (elements.body.children.length === 0) {
                elements.tableWrap.hidden = true;
                elements.pagination.hidden = true;
            }
        } finally {
            if (version === state.requestVersion) {
                elements.panel.setAttribute('aria-busy', 'false');
                elements.panel.classList.remove('is-refreshing');
            }
        }
    }

    function renderRows(items) {
        elements.body.replaceChildren();
        elements.loading.hidden = true;

        if (items.length === 0) {
            elements.empty.hidden = false;
            elements.tableWrap.hidden = true;
            elements.pagination.hidden = state.cursorHistory.length === 0;
            return;
        }

        elements.empty.hidden = true;
        elements.tableWrap.hidden = false;
        elements.pagination.hidden = false;

        const fragment = document.createDocumentFragment();
        for (const run of items) {
            fragment.append(createRunRow(run));
        }
        elements.body.append(fragment);
    }

    function createRunRow(run) {
        const row = document.createElement('tr');
        row.dataset.runId = run.id;

        const statusCell = document.createElement('td');
        const status = document.createElement('span');
        status.className = `run-status ${String(run.status).toLowerCase()}`;
        status.textContent = run.status;
        statusCell.append(status);
        row.append(statusCell);

        const runCell = document.createElement('td');
        const runLink = document.createElement('a');
        runLink.className = 'table-link';
        runLink.href = `/run/${encodeURIComponent(run.id)}`;
        runLink.textContent = run.name;
        runCell.append(runLink);
        row.append(runCell);

        row.append(textCell(run.component));
        row.append(textCell(run.environment));
        row.append(textCell(run.model || '—'));

        const startedCell = textCell(startedFormat.format(new Date(run.startedAt)));
        startedCell.title = run.startedAt;
        row.append(startedCell);

        row.append(textCell(formatDuration(run.startedAt, run.completedAt)));
        row.append(textCell(numberFormat.format(run.tokens ?? 0)));
        row.append(textCell(`$${Number(run.costUsd ?? 0).toFixed(4)}`));
        return row;
    }

    function textCell(value) {
        const cell = document.createElement('td');
        cell.textContent = value;
        return cell;
    }

    function formatDuration(startedAt, completedAt) {
        if (!completedAt) return 'running';
        const ms = Math.max(0, new Date(completedAt).getTime() - new Date(startedAt).getTime());
        if (ms < 1000) return `${Math.round(ms)} ms`;
        if (ms < 60_000) return `${(ms / 1000).toFixed(ms < 10_000 ? 1 : 0)} s`;
        if (ms < 3_600_000) return `${Math.floor(ms / 60_000)}m ${Math.floor((ms % 60_000) / 1000)}s`;
        return `${Math.floor(ms / 3_600_000)}h ${Math.floor((ms % 3_600_000) / 60_000)}m`;
    }

    function updatePagination() {
        elements.pageLabel.textContent = `Page ${state.cursorHistory.length + 1}`;
        elements.newer.disabled = state.cursorHistory.length === 0;
        elements.older.disabled = !state.nextCursor;
    }

    function resetPagination() {
        state.cursor = null;
        state.cursorHistory = [];
        state.nextCursor = null;
        clearPendingChanges();
    }

    function filtersChanged() {
        resetPagination();
        syncBrowserUrl();
        loadRuns();
    }

    function clearPendingChanges() {
        state.pendingChanges = 0;
        elements.newBanner.hidden = true;
    }

    function showPendingChange() {
        state.pendingChanges += 1;
        const suffix = state.pendingChanges === 1 ? 'update available' : 'updates available';
        elements.newCount.textContent = `${state.pendingChanges} ${suffix}`;
        elements.newBanner.hidden = false;
    }

    function eventMatchesStructuredFilters(event) {
        const filters = getFilters();
        if (filters.componentId && event.componentId !== filters.componentId) return false;
        if (filters.status && event.status !== filters.status) return false;
        if (filters.environment && event.environment !== filters.environment) return false;
        if (filters.model && event.model !== filters.model) return false;

        const started = new Date(event.startedAt).getTime();
        const from = localDayIso(filters.from, false);
        const to = localDayIso(filters.to, true);
        if (from && started < new Date(from).getTime()) return false;
        if (to && started >= new Date(to).getTime()) return false;
        return true;
    }

    function handleRunChanged(event) {
        ensureOption(elements.component, event.componentId, `${event.component} · ${event.environment}`);
        ensureOption(elements.environment, event.environment, event.environment);
        ensureOption(elements.model, event.model, event.model);

        const visible = [...elements.body.querySelectorAll('tr')]
            .some(row => row.dataset.runId === event.runId);

        if (visible) {
            queueRefresh();
            return;
        }

        if (!eventMatchesStructuredFilters(event)) return;

        // Free-text search is intentionally not evaluated against the SignalR payload.
        // The server query is authoritative and also searches fields such as ExternalId.
        if (!state.cursor) {
            queueRefresh();
        } else {
            showPendingChange();
        }
    }

    function queueRefresh() {
        window.clearTimeout(state.refreshTimer);
        state.refreshTimer = window.setTimeout(() => loadRuns({ quiet: true }), 140);
    }

    function setLiveState(mode, text) {
        elements.liveState.classList.remove('connected', 'reconnecting', 'offline');
        elements.liveState.classList.add(mode);
        elements.liveState.querySelector('strong').textContent = text;
    }

    async function startSignalR() {
        if (!window.signalR) {
            setLiveState('offline', 'Live unavailable');
            return;
        }

        const connection = new signalR.HubConnectionBuilder()
            .withUrl('/hubs/monitor')
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .build();

        connection.on('RunChanged', handleRunChanged);
        connection.onreconnecting(() => setLiveState('reconnecting', 'Reconnecting'));
        connection.onreconnected(() => {
            setLiveState('connected', 'Live');
            if (!state.cursor) queueRefresh();
            else showPendingChange();
        });
        connection.onclose(() => {
            setLiveState('offline', 'Disconnected');
            window.setTimeout(connect, 5000);
        });

        async function connect() {
            if (connection.state !== signalR.HubConnectionState.Disconnected) return;
            try {
                await connection.start();
                setLiveState('connected', 'Live');
            } catch (error) {
                setLiveState('offline', 'Disconnected');
                console.warn('SignalR connection failed.', error);
                window.setTimeout(connect, 5000);
            }
        }

        await connect();
    }

    function normaliseError(error) {
        return error instanceof Error ? error.message : String(error);
    }

    elements.older.addEventListener('click', () => {
        if (!state.nextCursor) return;
        state.cursorHistory.push(state.cursor);
        state.cursor = state.nextCursor;
        loadRuns();
    });

    elements.newer.addEventListener('click', () => {
        if (state.cursorHistory.length === 0) return;
        state.cursor = state.cursorHistory.pop() ?? null;
        loadRuns();
    });

    elements.newBanner.addEventListener('click', () => {
        resetPagination();
        loadRuns();
    });

    elements.search.addEventListener('input', () => {
        window.clearTimeout(state.searchTimer);
        state.searchTimer = window.setTimeout(filtersChanged, 280);
    });

    for (const element of [
        elements.component,
        elements.status,
        elements.environment,
        elements.model,
        elements.from,
        elements.to,
        elements.pageSize
    ]) {
        element.addEventListener('change', filtersChanged);
    }

    elements.clear.addEventListener('click', () => {
        elements.search.value = '';
        elements.component.value = '';
        elements.status.value = '';
        elements.environment.value = '';
        elements.model.value = '';
        elements.from.value = '';
        elements.to.value = '';
        resetPagination();
        syncBrowserUrl();
        loadRuns();
    });

    async function initialise() {
        const urlFilters = readUrlFilters();
        await loadOptions();
        applyUrlFilters(urlFilters);
        await loadRuns();
        startSignalR();
    }

    initialise();
})();
