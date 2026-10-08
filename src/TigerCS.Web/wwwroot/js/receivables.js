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
