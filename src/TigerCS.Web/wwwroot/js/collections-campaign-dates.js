/*
  Keeps the campaign From/To dates in step with the stage and preview date UNTIL the user edits them.
  Same rules as the server (CollectionsCampaignPolicy.DefaultDateTo and the preview): From = 1 January of the preview year;
  To = last day of the preview month for the current-month and follow-up stages, otherwise the preview date.
*/
(() => {
    document.addEventListener('DOMContentLoaded', () => {
        const stage = document.querySelector('[data-campaign-stage]');
        const date = document.querySelector('[data-campaign-date]');
        const from = document.querySelector('[data-campaign-from]');
        const to = document.querySelector('[data-campaign-to]');
        if (!stage || !date || !from || !to) return;
        const pad = n => String(n).padStart(2, '0');
        const defaults = () => {
            const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(date.value);
            if (!m) return null;
            const y = +m[1], mo = +m[2];
            const wholeMonth = stage.value === 'CurrentMonthReminder' || stage.value === 'FollowUpReminder';
            const last = new Date(Date.UTC(y, mo, 0)).getUTCDate();
            return { from: `${y}-01-01`, to: wholeMonth ? `${y}-${pad(mo)}-${pad(last)}` : date.value };
        };
        // A field is "unedited" while it still holds the default the page was rendered with (or the last one we wrote).
        const track = input => { input.dataset.auto = input.value === (input.dataset.default || '') || input.value === '' ? 'true' : 'false'; };
        track(from); track(to);
        [from, to].forEach(i => i.addEventListener('input', () => { i.dataset.auto = 'false'; }));
        const apply = () => {
            const d = defaults(); if (!d) return;
            if (from.dataset.auto === 'true') from.value = d.from;
            if (to.dataset.auto === 'true') to.value = d.to;
        };
        stage.addEventListener('change', apply);
        date.addEventListener('change', apply);
    });
})();
