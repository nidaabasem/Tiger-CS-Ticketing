(() => {
    const page = document.querySelector('[data-review-page]');
    if (!page) return;

    // Ticked record keys survive paging (session only; the server re-checks every key at confirmation).
    const KEY = 'collections-review-selection';
    const load = () => { try { return new Set(JSON.parse(sessionStorage.getItem(KEY) || '[]')); } catch { return new Set(); } };
    const save = set => { try { sessionStorage.setItem(KEY, JSON.stringify([...set])); } catch { /* storage unavailable */ } };
    const selected = load();
    const boxes = [...page.querySelectorAll('.review-select')];
    const counters = page.querySelectorAll('[data-selected-count]');
    const hidden = page.querySelector('[data-selected-keys]');
    const refresh = () => {
        counters.forEach(c => { c.textContent = String(selected.size); });
        if (hidden) hidden.value = [...selected].join(',');
    };
    boxes.forEach(box => {
        box.checked = selected.has(box.dataset.key);
        box.addEventListener('change', () => { box.checked ? selected.add(box.dataset.key) : selected.delete(box.dataset.key); save(selected); refresh(); });
    });
    const all = page.querySelector('[data-select-page]');
    if (all) all.addEventListener('change', () => boxes.forEach(b => { b.checked = all.checked; b.dispatchEvent(new Event('change')); }));
    const clear = page.querySelector('[data-clear-selection]');
    if (clear) clear.addEventListener('click', () => { selected.clear(); save(selected); boxes.forEach(b => { b.checked = false; }); refresh(); });
    refresh();

    // A confirmed send clears the ticks; a double click cannot submit twice (and the idempotency key would replay it anyway).
    const confirmForm = page.querySelector('[data-confirm-form]');
    if (confirmForm) confirmForm.addEventListener('submit', event => {
        const button = confirmForm.querySelector('[data-confirm-button]');
        if (button && button.disabled) { event.preventDefault(); return; }
        if (button) { button.disabled = true; button.textContent = 'Sending…'; }
        selected.clear(); save(selected);
    });

    // Refresh job progress.
    const run = page.querySelector('[data-run-active="true"]');
    if (run) {
        const bar = run.querySelector('[data-run-progress]');
        const phase = run.querySelector('[data-run-phase]');
        const poll = async () => {
            try {
                const response = await fetch('/Collections/Review?handler=Run', { headers: { Accept: 'application/json' }, cache: 'no-store' });
                const state = await response.json();
                if (bar) bar.value = state.progressPercent;
                if (phase) phase.textContent = state.phase + '…';
                if (!state.active) { window.location.reload(); return; }
            } catch { /* retry on the next tick */ }
            window.setTimeout(poll, 3000);
        };
        window.setTimeout(poll, 2000);
    }
})();
