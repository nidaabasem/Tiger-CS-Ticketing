(() => {
    const form = document.querySelector('[data-receivables-filter]');
    if (!form) return;
    form.addEventListener('submit', () => {
        form.setAttribute('aria-busy', 'true');
        form.querySelector('.receivables-loading').hidden = false;
        form.querySelector('button[type=submit]').disabled = true;
    });
    window.addEventListener('pageshow', () => {
        form.removeAttribute('aria-busy');
        form.querySelector('.receivables-loading').hidden = true;
        form.querySelector('button[type=submit]').disabled = false;
    });
})();
