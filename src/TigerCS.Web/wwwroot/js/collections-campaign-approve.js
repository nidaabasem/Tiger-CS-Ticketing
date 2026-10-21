/*
  Collections Campaigns: select customers, then Approve & Send. Ticked customers survive paging and the asynchronous results reload (session only; the server
  re-checks every ticked customer at approval and again before upload). Also polls the approval-data refresh and a running send, so neither can sit on "Queued".
*/
(() => {
    const KEY = 'collections-campaign-selection';
    const load = () => { try { return new Set(JSON.parse(sessionStorage.getItem(KEY) || '[]')); } catch { return new Set(); } };
    const save = set => { try { sessionStorage.setItem(KEY, JSON.stringify([...set])); } catch { /* storage unavailable */ } };
    const selected = load();

    const sync = () => {
        const form = document.querySelector('[data-approve-form]');
        document.querySelectorAll('.campaign-select').forEach(box => { box.checked = selected.has(box.dataset.unitKey); });
        document.querySelectorAll('[data-selected-count]').forEach(c => { c.textContent = String(selected.size); });
        const hidden = document.querySelector('[data-selected-units]');
        if (hidden) hidden.value = [...selected].join(',');
        const all = document.querySelector('[data-select-page]');
        if (all) { const boxes = [...document.querySelectorAll('.campaign-select')]; all.checked = boxes.length > 0 && boxes.every(b => b.checked); }
        if (form) {
            const button = form.querySelector('[data-approve-button]');
            const mode = form.querySelector('input[name="selectionMode"]:checked');
            if (button) button.disabled = !!mode && mode.value === 'SelectedUnits' && selected.size === 0;
        }
    };

    document.addEventListener('change', event => {
        const t = event.target;
        if (t.classList && t.classList.contains('campaign-select')) { t.checked ? selected.add(t.dataset.unitKey) : selected.delete(t.dataset.unitKey); save(selected); sync(); }
        else if (t.matches && t.matches('[data-select-page]')) { document.querySelectorAll('.campaign-select').forEach(b => { b.checked = t.checked; t.checked ? selected.add(b.dataset.unitKey) : selected.delete(b.dataset.unitKey); }); save(selected); sync(); }
        else if (t.matches && t.matches('input[name="selectionMode"]')) sync();
    });
    document.addEventListener('click', event => {
        if (event.target.closest && event.target.closest('[data-clear-selection]')) { selected.clear(); save(selected); sync(); }
    });
    // A new preview (filters changed) starts a new selection; paging keeps it.
    const filter = document.querySelector('[data-results-form]');
    if (filter) filter.addEventListener('submit', () => { selected.clear(); save(selected); });
    // A double click cannot submit twice (the idempotency key would replay it anyway).
    document.addEventListener('submit', event => {
        const form = event.target;
        if (form.matches && form.matches('[data-confirm-form]')) {
            const button = form.querySelector('[data-confirm-button]');
            if (button && button.disabled) { event.preventDefault(); return; }
            if (button && event.submitter === button) { button.disabled = true; button.textContent = 'Sending…'; selected.clear(); save(selected); }
        }
    });
    const results = document.querySelector('[data-results]');
    if (results) new MutationObserver(sync).observe(results, { childList: true, subtree: true });
    sync();

    // Approval-data refresh: poll until it ends, then reload so a failure (with its reason and Retry) or the finished data appears.
    const run = document.querySelector('[data-run-active="true"]');
    if (run) {
        const bar = run.querySelector('[data-run-progress]');
        const phase = run.querySelector('[data-run-phase]');
        const poll = async () => {
            try {
                const response = await fetch('/Collections/Campaigns?handler=Run', { headers: { Accept: 'application/json' }, cache: 'no-store' });
                const state = await response.json();
                if (bar) bar.value = state.progressPercent;
                if (phase) phase.textContent = state.phase + '…';
                if (!state.active) { window.location.reload(); return; }
            } catch { /* retry on the next tick */ }
            window.setTimeout(poll, 3000);
        };
        window.setTimeout(poll, 2000);
    }

    // A running send: poll its status and reload when it ends.
    const send = document.querySelector('[data-dispatch-active="true"]');
    if (send) {
        const id = send.dataset.dispatchId;
        const poll = async () => {
            try {
                const response = await fetch('/Collections/Campaigns?handler=Send&dispatch=' + encodeURIComponent(id), { headers: { Accept: 'application/json' }, cache: 'no-store' });
                const state = await response.json();
                if (!state.active) { window.location.reload(); return; }
            } catch { /* retry on the next tick */ }
            window.setTimeout(poll, 4000);
        };
        window.setTimeout(poll, 3000);
    }
})();
