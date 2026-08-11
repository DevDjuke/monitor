(() => {
    'use strict';

    const root = document.getElementById('run-live-root');
    if (!root) return;

    const runId = root.dataset.runId;
    if (!runId) return;

    const elements = {
        name: document.getElementById('run-live-name'),
        connection: document.getElementById('run-live-connection'),
        status: document.getElementById('run-live-status'),
        duration: document.getElementById('run-live-duration'),
        model: document.getElementById('run-live-model'),
        inputTokens: document.getElementById('run-live-input-tokens'),
        outputTokens: document.getElementById('run-live-output-tokens'),
        cost: document.getElementById('run-live-cost'),
        trigger: document.getElementById('run-live-trigger'),
        updateBanner: document.getElementById('run-live-update-banner'),
        updateCount: document.getElementById('run-live-update-count'),
        timeline: document.getElementById('run-live-timeline'),
        timelineEmpty: document.getElementById('run-live-timeline-empty'),
        logCount: document.getElementById('run-live-log-count'),
        spanCount: document.getElementById('run-live-span-count'),
        trace: document.getElementById('run-live-trace'),
        traceEmpty: document.getElementById('run-live-trace-empty'),
        traceCount: document.getElementById('run-live-trace-count'),
        errorPanel: document.getElementById('run-live-error-panel'),
        error: document.getElementById('run-live-error'),
        outputPanel: document.getElementById('run-live-output-panel'),
        output: document.getElementById('run-live-output')
    };

    const numberFormat = new Intl.NumberFormat();
    const timeFormat = new Intl.DateTimeFormat(undefined, {
        hour: '2-digit',
        minute: '2-digit',
        second: '2-digit',
        fractionalSecondDigits: 3
    });

    let mode = root.dataset.runStatus === 'Running' ? 'live' : 'frozen';
    let transport = 'connecting';
    let pendingChanges = 0;
    let refreshTimer = null;
    let safetyTimer = null;
    let requestVersion = 0;
    let connection = null;

    function sameRun(value) {
        return String(value || '').toLowerCase() === runId.toLowerCase();
    }

    function normaliseError(error) {
        return error instanceof Error ? error.message : String(error);
    }

    function formatDuration(startedAt, completedAt) {
        const start = Date.parse(startedAt);
        const end = completedAt ? Date.parse(completedAt) : Date.now();
        if (!Number.isFinite(start) || !Number.isFinite(end)) return '—';

        const ms = Math.max(0, end - start);
        if (!completedAt && ms < 1000) return 'running';
        if (ms < 1000) return `${Math.round(ms)} ms`;
        if (ms < 60_000) return `${(ms / 1000).toFixed(ms < 10_000 ? 1 : 0)} s`;
        if (ms < 3_600_000) return `${Math.floor(ms / 60_000)}m ${Math.floor((ms % 60_000) / 1000)}s`;
        return `${Math.floor(ms / 3_600_000)}h ${Math.floor((ms % 3_600_000) / 60_000)}m`;
    }

    function updateDurations() {
        for (const element of document.querySelectorAll('[data-live-duration]')) {
            const startedAt = element.dataset.startedAt;
            if (!startedAt) continue;
            element.textContent = formatDuration(startedAt, element.dataset.completedAt || null);
        }
    }

    function setMode(nextMode) {
        mode = nextMode;
        syncConnectionLabel();
        syncSafetyTimer();
    }

    function setTransport(nextTransport) {
        transport = nextTransport;
        syncConnectionLabel();
    }

    function syncConnectionLabel() {
        if (!elements.connection) return;

        elements.connection.classList.remove('connected', 'connecting', 'reconnecting', 'offline', 'frozen');

        let className;
        let text;
        if (transport === 'offline') {
            className = 'offline';
            text = mode === 'live' ? 'Live disconnected' : 'Historical · offline';
        } else if (transport === 'reconnecting') {
            className = 'reconnecting';
            text = mode === 'live' ? 'Reconnecting' : 'Historical · reconnecting';
        } else if (mode === 'frozen') {
            className = 'frozen';
            text = 'Historical · frozen';
        } else if (transport === 'connected') {
            className = 'connected';
            text = 'Live';
        } else {
            className = 'connecting';
            text = 'Connecting';
        }

        elements.connection.classList.add(className);
        const label = elements.connection.querySelector('strong');
        if (label) label.textContent = text;
    }

    function syncSafetyTimer() {
        window.clearInterval(safetyTimer);
        safetyTimer = null;
        if (mode !== 'live') return;

        safetyTimer = window.setInterval(() => {
            if (document.visibilityState === 'visible') {
                queueSnapshotRefresh(0);
            }
        }, 5000);
    }

    function showPendingChange() {
        pendingChanges += 1;
        if (!elements.updateBanner || !elements.updateCount) return;
        elements.updateCount.textContent = pendingChanges === 1
            ? '1 update available'
            : `${pendingChanges} updates available`;
        elements.updateBanner.hidden = false;
    }

    function showReconnectUncertainty() {
        pendingChanges = Math.max(1, pendingChanges);
        if (!elements.updateBanner || !elements.updateCount) return;
        elements.updateCount.textContent = 'Connection restored · refresh to verify';
        elements.updateBanner.hidden = false;
    }

    function clearPendingChanges() {
        pendingChanges = 0;
        if (elements.updateBanner) elements.updateBanner.hidden = true;
    }

    async function fetchSnapshot() {
        const response = await fetch(`/api/runs/${encodeURIComponent(runId)}`, {
            headers: { 'Accept': 'application/json' },
            credentials: 'same-origin'
        });

        if (response.status === 401) {
            window.location.assign(`/account/login?returnUrl=${encodeURIComponent(window.location.pathname)}`);
            throw new Error('Authentication required.');
        }

        if (!response.ok) {
            const message = await response.text();
            throw new Error(message || `Run snapshot failed with ${response.status}.`);
        }

        return response.json();
    }

    function queueSnapshotRefresh(delay = 100) {
        if (mode !== 'live') {
            showPendingChange();
            return;
        }

        window.clearTimeout(refreshTimer);
        refreshTimer = window.setTimeout(() => refreshSnapshot(false), delay);
    }

    async function refreshSnapshot(forceFrozen) {
        if (mode === 'frozen' && !forceFrozen) {
            showPendingChange();
            return;
        }

        const version = ++requestVersion;
        root.classList.add('is-reconciling');
        try {
            const snapshot = await fetchSnapshot();
            if (version !== requestVersion) return;
            renderSnapshot(snapshot);
            clearPendingChanges();
        } catch (error) {
            if (version === requestVersion) {
                console.warn('Run live snapshot could not be reconciled.', error);
            }
        } finally {
            if (version === requestVersion) root.classList.remove('is-reconciling');
        }
    }

    function renderSnapshot(snapshot) {
        root.dataset.runStatus = snapshot.status;
        root.dataset.runStartedAt = snapshot.startedAt;
        root.dataset.runCompletedAt = snapshot.completedAt || '';

        if (elements.name) elements.name.textContent = snapshot.name;
        if (elements.status) {
            elements.status.textContent = snapshot.status;
            elements.status.className = `run-status ${String(snapshot.status).toLowerCase()}`;
        }
        if (elements.duration) {
            elements.duration.dataset.startedAt = snapshot.startedAt;
            elements.duration.dataset.completedAt = snapshot.completedAt || '';
            elements.duration.textContent = formatDuration(snapshot.startedAt, snapshot.completedAt);
        }
        if (elements.model) elements.model.textContent = snapshot.model || '—';
        if (elements.inputTokens) elements.inputTokens.textContent = numberFormat.format(snapshot.inputTokens || 0);
        if (elements.outputTokens) elements.outputTokens.textContent = numberFormat.format(snapshot.outputTokens || 0);
        if (elements.cost) elements.cost.textContent = `$${Number(snapshot.costUsd || 0).toFixed(4)}`;
        if (elements.trigger) elements.trigger.textContent = snapshot.trigger || '—';

        const spans = snapshot.spans || [];
        const logs = snapshot.logs || [];
        reconcileTrace(spans);
        reconcileTimeline(spans, logs);
        if (elements.spanCount) elements.spanCount.textContent = String(spans.length);
        if (elements.traceCount) elements.traceCount.textContent = String(spans.length);
        if (elements.logCount) elements.logCount.textContent = String(logs.length);

        syncPayload(elements.errorPanel, elements.error, snapshot.error);
        syncPayload(elements.outputPanel, elements.output, snapshot.outputJson);

        setMode(snapshot.status === 'Running' ? 'live' : 'frozen');
        updateDurations();
    }

    function syncPayload(panel, pre, value) {
        if (!panel || !pre) return;
        const hasValue = Boolean(value && String(value).trim());
        panel.hidden = !hasValue;
        pre.textContent = hasValue ? String(value) : '';
    }

    function reconcileTimeline(spans, logs) {
        if (!elements.timeline || !elements.timelineEmpty) return;

        const items = [];
        for (const span of spans) {
            items.push({
                key: `span:${span.id}`,
                type: 'span',
                timestamp: span.startedAt,
                kind: 'SPAN',
                title: span.name,
                subtitle: span.kind,
                detail: span.errorType || (span.httpStatusCode ? `HTTP ${span.httpStatusCode}` : null),
                propertiesJson: span.attributesJson,
                cssClass: `span-${String(span.status).toLowerCase()}`,
                startedAt: span.startedAt,
                completedAt: span.completedAt
            });
        }

        for (const log of logs) {
            items.push({
                key: `log:${log.id}`,
                type: 'log',
                timestamp: log.timestamp,
                kind: String(log.level || 'Log').toUpperCase(),
                title: log.message || '(empty log record)',
                subtitle: log.eventName || log.source || 'log event',
                detail: log.exceptionType || null,
                propertiesJson: log.propertiesJson,
                cssClass: `log-${String(log.level || 'unspecified').toLowerCase()}`
            });
        }

        items.sort((left, right) => {
            const timeDelta = Date.parse(left.timestamp) - Date.parse(right.timestamp);
            if (timeDelta !== 0) return timeDelta;
            if (left.type !== right.type) return left.type === 'span' ? -1 : 1;
            return left.key.localeCompare(right.key);
        });

        const existing = new Map();
        for (const child of [...elements.timeline.children]) {
            if (child.dataset.timelineKey) existing.set(child.dataset.timelineKey, child);
            else child.remove();
        }

        const desired = new Set();
        for (const item of items) {
            desired.add(item.key);
            const row = existing.get(item.key) || document.createElement('div');
            updateTimelineRow(row, item);
            elements.timeline.append(row);
        }

        for (const [key, row] of existing) {
            if (!desired.has(key)) row.remove();
        }

        const empty = items.length === 0;
        elements.timeline.hidden = empty;
        elements.timelineEmpty.hidden = !empty;
    }

    function updateTimelineRow(row, item) {
        row.dataset.timelineKey = item.key;
        row.className = `timeline-row ${item.cssClass}`;

        const time = document.createElement('span');
        time.className = 'timeline-time';
        const parsed = new Date(item.timestamp);
        time.textContent = Number.isNaN(parsed.getTime()) ? item.timestamp : timeFormat.format(parsed);

        const kind = document.createElement('span');
        kind.className = 'timeline-kind';
        kind.textContent = item.kind;

        const content = document.createElement('div');
        content.className = 'timeline-content';
        const title = document.createElement('strong');
        title.textContent = item.title;
        content.append(title);

        if (item.subtitle) {
            const subtitle = document.createElement('small');
            subtitle.textContent = item.subtitle;
            content.append(subtitle);
        }

        if (item.propertiesJson) {
            const details = document.createElement('details');
            const summary = document.createElement('summary');
            summary.textContent = 'Properties';
            const pre = document.createElement('pre');
            pre.textContent = item.propertiesJson;
            details.append(summary, pre);
            content.append(details);
        }

        const detail = document.createElement('span');
        detail.className = 'timeline-detail';
        if (item.type === 'span') {
            detail.dataset.liveDuration = '';
            detail.dataset.startedAt = item.startedAt;
            detail.dataset.completedAt = item.completedAt || '';
            detail.textContent = formatDuration(item.startedAt, item.completedAt);
        } else {
            detail.textContent = item.detail || '';
        }

        row.replaceChildren(time, kind, content, detail);
    }

    function reconcileTrace(spans) {
        if (!elements.trace || !elements.traceEmpty) return;

        const ordered = [...spans].sort((left, right) => {
            const timeDelta = Date.parse(left.startedAt) - Date.parse(right.startedAt);
            return timeDelta !== 0 ? timeDelta : String(left.id).localeCompare(String(right.id));
        });
        const byId = new Map(ordered.map(span => [String(span.id).toLowerCase(), span]));

        const existing = new Map();
        for (const child of [...elements.trace.children]) {
            if (child.dataset.spanId) existing.set(child.dataset.spanId.toLowerCase(), child);
            else child.remove();
        }

        const desired = new Set();
        for (const span of ordered) {
            const key = String(span.id).toLowerCase();
            desired.add(key);
            const row = existing.get(key) || document.createElement('div');
            updateTraceRow(row, span, spanDepth(span, byId));
            elements.trace.append(row);
        }

        for (const [key, row] of existing) {
            if (!desired.has(key)) row.remove();
        }

        const empty = ordered.length === 0;
        elements.trace.hidden = empty;
        elements.traceEmpty.hidden = !empty;
    }

    function spanDepth(span, byId) {
        let depth = 0;
        let parentId = span.parentSpanId ? String(span.parentSpanId).toLowerCase() : null;
        const visited = new Set([String(span.id).toLowerCase()]);

        while (parentId && byId.has(parentId) && depth < 20 && !visited.has(parentId)) {
            visited.add(parentId);
            depth += 1;
            const parent = byId.get(parentId);
            parentId = parent?.parentSpanId ? String(parent.parentSpanId).toLowerCase() : null;
        }

        return depth;
    }

    function updateTraceRow(row, span, depth) {
        row.className = 'trace-row run-live-trace-row';
        row.dataset.spanId = span.id;
        row.dataset.parentSpanId = span.parentSpanId || '';
        row.style.setProperty('--trace-depth', String(depth));

        const line = document.createElement('span');
        line.className = 'trace-line';

        const status = document.createElement('span');
        status.className = `status-dot ${String(span.status).toLowerCase()}`;

        const kind = document.createElement('span');
        kind.className = 'trace-kind';
        kind.textContent = span.kind;

        const main = document.createElement('div');
        main.className = 'run-live-trace-main';
        const name = document.createElement('strong');
        name.textContent = span.name;
        main.append(name);

        const diagnostic = span.errorType || (span.httpStatusCode ? `HTTP ${span.httpStatusCode}` : span.error);
        if (diagnostic) {
            const detail = document.createElement('small');
            detail.className = 'run-live-trace-error';
            detail.textContent = diagnostic;
            main.append(detail);
        }

        const duration = document.createElement('span');
        duration.className = 'trace-duration';
        duration.dataset.liveDuration = '';
        duration.dataset.startedAt = span.startedAt;
        duration.dataset.completedAt = span.completedAt || '';
        duration.textContent = formatDuration(span.startedAt, span.completedAt);

        row.replaceChildren(line, status, kind, main, duration);
    }

    async function startSignalR() {
        if (!window.signalR) {
            setTransport('offline');
            return;
        }

        connection = new signalR.HubConnectionBuilder()
            .withUrl('/hubs/monitor')
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .build();

        connection.on('RunDetailChanged', event => {
            if (!sameRun(event.runId)) return;
            if (mode === 'live') queueSnapshotRefresh();
            else showPendingChange();
        });

        // Coarse RunChanged remains useful for compatibility and OTLP trace batches.
        connection.on('RunChanged', event => {
            if (!sameRun(event.runId)) return;
            if (mode === 'live') queueSnapshotRefresh();
            else showPendingChange();
        });

        connection.onreconnecting(() => setTransport('reconnecting'));
        connection.onreconnected(async () => {
            setTransport('connected');
            try {
                await connection.invoke('WatchRun', runId);
                if (mode === 'live') queueSnapshotRefresh(0);
                else showReconnectUncertainty();
            } catch (error) {
                console.warn('Run live group could not be restored.', error);
            }
        });
        connection.onclose(() => {
            setTransport('offline');
            window.setTimeout(connect, 5000);
        });

        async function connect() {
            if (connection.state !== signalR.HubConnectionState.Disconnected) return;
            try {
                setTransport('connecting');
                await connection.start();
                await connection.invoke('WatchRun', runId);
                setTransport('connected');
                if (mode === 'live') queueSnapshotRefresh(0);
            } catch (error) {
                setTransport('offline');
                console.warn('Run live SignalR connection failed.', error);
                window.setTimeout(connect, 5000);
            }
        }

        await connect();
    }

    elements.updateBanner?.addEventListener('click', () => refreshSnapshot(true));

    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'visible' && mode === 'live') {
            queueSnapshotRefresh(0);
        }
    });

    window.setInterval(updateDurations, 500);
    updateDurations();
    syncSafetyTimer();
    syncConnectionLabel();
    startSignalR();
})();
