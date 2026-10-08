(() => {
    const form = document.querySelector('[data-receivables-filter]');
    if (!form) return;
    const loading = form.querySelector('.receivables-loading');
    const timeout = form.querySelector('.receivables-timeout');
    const button = form.querySelector('button[type=submit]');
    // Longer than the API request budget (command timeout + 30 s) but shorter than the Web client's 180 s timeout.
    const WATCHDOG_MS = 170000;
    let watchdog = 0;
    const idle = () => {
        window.clearTimeout(watchdog);
        form.removeAttribute('aria-busy');
        loading.hidden = true;
        button.disabled = false;
    };
    form.addEventListener('submit', () => {
        // A new request starts: drop the previous error/timeout message and show progress.
        document.querySelectorAll('[data-campaign-error]').forEach(e => { e.hidden = true; });
        if (timeout) timeout.hidden = true;
        form.setAttribute('aria-busy', 'true');
        loading.hidden = false;
        button.disabled = true;
        window.clearTimeout(watchdog);
        watchdog = window.setTimeout(() => {
            // The navigation never produced a response: stop it so the form is usable again.
            window.stop();
            idle();
            if (timeout) timeout.hidden = false;
        }, WATCHDOG_MS);
    });
    // Back/forward cache restores the old DOM; a stopped or failed navigation must not leave it busy.
    window.addEventListener('pageshow', idle);
    window.addEventListener('pagehide', () => window.clearTimeout(watchdog));
})();

// Reporting month picker: mirrors the <input type="month"> into the year/month query fields.
document.addEventListener('DOMContentLoaded', function () {
    var input = document.querySelector('[data-month-input]');
    if (!input) return;
    var year = document.querySelector('[data-year-field]');
    var month = document.querySelector('[data-month-field]');
    input.addEventListener('change', function () {
        var m = /^(\d{4})-(\d{2})$/.exec(input.value);
        year.value = m ? String(parseInt(m[1], 10)) : '';
        month.value = m ? String(parseInt(m[2], 10)) : '';
    });
});
