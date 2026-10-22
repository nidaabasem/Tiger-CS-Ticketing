// Polling behaviour of the Collections load monitor and of receivables.js built on it:
// one chained loop, no overlapping requests, stale answers discarded, bounded, ends when nothing is loading, cleaned up on stop / navigation.
import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { JSDOM } from 'jsdom';

const js = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'src', 'TigerCS.Web', 'wwwroot', 'js');
const Monitor = createRequire(import.meta.url)(join(js, 'receivables-load-monitor.js'));

/** A manual clock + timer queue so tests never wait in real time. */
function clock() {
    let t = 0, id = 0; const timers = new Map();
    return {
        now: () => t,
        setTimer: (fn, ms) => { const k = ++id; timers.set(k, { fn, at: t + ms }); return k; },
        clearTimer: k => timers.delete(k),
        pending: () => timers.size,
        /** Runs the next due timer (advancing time); returns false when none is pending. */
        async step() {
            if (!timers.size) return false;
            const [k, v] = [...timers.entries()].sort((a, b) => a[1].at - b[1].at)[0];
            timers.delete(k); t = Math.max(t, v.at); v.fn();
            for (let i = 0; i < 5; i++) await Promise.resolve();   // let the awaited fetch settle
            return true;
        }
    };
}
const tick = async () => { for (let i = 0; i < 10; i++) await Promise.resolve(); };

function harness(answers, extra = {}) {
    const c = clock(); const events = []; let active = 0, maxActive = 0, calls = 0; const signals = [];
    const monitor = Monitor.create({
        now: c.now, setTimer: c.setTimer, clearTimer: c.clearTimer, ...extra,
        fetchStatus: async signal => {
            calls++; active++; maxActive = Math.max(maxActive, active); signals.push(signal);
            try { const a = answers.length > 1 ? answers.shift() : answers[0]; if (a instanceof Error) throw a; return typeof a === 'function' ? await a() : a; }
            finally { active--; }
        },
        onStatus: s => events.push(['status', s.active]),
        onDone: s => events.push(['done']),
        onGiveUp: (s, reason) => events.push(['giveup', reason])
    });
    return { c, monitor, events, stats: () => ({ calls, maxActive }), signals };
}
const busy = { available: true, active: true, companies: [] };
const idle = { available: true, active: false, companies: [] };

test('polls while a refresh runs and stops, once, when the server says nothing is loading', async () => {
    const h = harness([busy, busy, idle]);
    h.monitor.start();
    while (await h.c.step());
    assert.deepEqual(h.events.filter(e => e[0] !== 'status'), [['done']]);
    assert.equal(h.stats().calls, 3);
    assert.equal(h.c.pending(), 0, 'no timer is left running');
    assert.equal(h.monitor.isRunning(), false);
});

test('requests never overlap: the next poll is scheduled only after the previous answer', async () => {
    let release; const slow = () => new Promise(r => { release = () => r(busy); });
    const h = harness([slow, busy, idle]);
    h.monitor.start();
    await h.c.step();                       // first poll starts and is still waiting
    assert.equal(h.c.pending(), 0, 'nothing else is scheduled while a request is in flight');
    assert.equal(h.stats().maxActive, 1);
    release(); await tick();
    while (await h.c.step());
    assert.equal(h.stats().maxActive, 1);
});

test('a bounded number of polls: gives up with a reason instead of polling forever', async () => {
    const h = harness([busy], { maxPolls: 5, maxMs: 1e9 });
    h.monitor.start();
    while (await h.c.step());
    assert.equal(h.stats().calls, 5);
    assert.deepEqual(h.events.filter(e => e[0] !== 'status'), [['giveup', 'timeout']]);
    assert.equal(h.c.pending(), 0);
});

test('a bounded duration: gives up when the elapsed time passes maxMs', async () => {
    const h = harness([busy], { maxPolls: 1000, maxMs: 60000, delays: [20000] });
    h.monitor.start();
    while (await h.c.step());
    assert.deepEqual(h.events.filter(e => e[0] !== 'status'), [['giveup', 'timeout']]);
    assert.ok(h.stats().calls <= 4);
});

test('repeated failures end the loop with an error reason; one failure does not', async () => {
    const h = harness([new Error('x'), busy, idle]);
    h.monitor.start();
    while (await h.c.step());
    assert.deepEqual(h.events.filter(e => e[0] !== 'status'), [['done']]);
    const bad = harness([new Error('x')], { maxErrors: 3 });
    bad.monitor.start();
    while (await bad.c.step());
    assert.deepEqual(bad.events, [['giveup', 'error']]);
    assert.equal(bad.stats().calls, 3);
});

test('restart supersedes the old loop: its late answer is discarded and its request aborted', async () => {
    let release; const slow = () => new Promise(r => { release = () => r(idle); });
    const h = harness([slow, busy], { createController: () => ({ signal: { aborted: false }, abort() { this.signal.aborted = true; } }) });
    h.monitor.start();
    await h.c.step();                       // old loop in flight
    const oldSignal = h.signals[0];
    h.monitor.start();                      // user changed a filter / pressed Retry
    assert.equal(oldSignal.aborted, true);
    release(); await tick();
    assert.deepEqual(h.events, [], 'the stale "idle" answer must not finish the new loop');
    assert.equal(h.monitor.isRunning(), true);
});

test('stop() cancels everything and no callback fires afterwards', async () => {
    const h = harness([busy]);
    h.monitor.start(); h.monitor.stop();
    assert.equal(h.c.pending(), 0);
    assert.equal(await h.c.step(), false);
    assert.deepEqual(h.events, []);
});

test('a hidden tab neither polls nor counts', async () => {
    let hidden = true;
    const h = harness([idle], { isHidden: () => hidden });
    h.monitor.start();
    for (let i = 0; i < 4; i++) await h.c.step();
    assert.equal(h.stats().calls, 0);
    hidden = false;
    while (await h.c.step());
    assert.deepEqual(h.events.filter(e => e[0] !== 'status'), [['done']]);
});

test('after a retry request the monitor waits for the refresh to start instead of concluding it is done', async () => {
    const h = harness([idle, idle, busy, idle], { graceMs: 30000 });
    h.monitor.start({ expectActive: true });
    while (await h.c.step());
    assert.equal(h.stats().calls, 4, 'kept looking until the refresh had been seen running and had ended');
    assert.deepEqual(h.events.filter(e => e[0] !== 'status'), [['done']]);
    const never = harness([idle], { graceMs: 10000, delays: [4000] });
    never.monitor.start({ expectActive: true });
    while (await never.c.step());
    assert.deepEqual(never.events.filter(e => e[0] !== 'status'), [['done']], 'after the grace period "not running" is final');
});

// ---------------------------------------------------------------- receivables.js on the Receivables page

const PAGE = `<!doctype html><html><body><div class="receivables-workspace">
<form data-results-form method="get" action="/Collections/Receivables"><input name="minTotal" value="100"/><button type="submit">Apply</button><span class="receivables-loading" hidden></span></form>
<div data-results data-results-url="/Collections/Receivables"><div data-results-loading>Loading…</div></div></div></body></html>`;
const RUNNING = '<div data-load-running data-poll-active>Loading data for Tiger Group Dubai… <span data-company-state="4" data-state="Loading">x</span></div><table data-list-marker></table>';
const DONE = '<table data-list-marker></table>';

const windows = []; after(() => windows.forEach(w => w.close()));
async function boot({ results, status }) {
    const dom = new JSDOM(PAGE, { url: 'https://tiger.test/Collections/Receivables', runScripts: 'outside-only', pretendToBeVisual: true });
    const w = dom.window; windows.push(w);
    const timers = []; const calls = { results: 0, status: 0 };
    w.setTimeout = (fn, ms) => { timers.push({ fn, ms }); return timers.length; }; w.clearTimeout = () => {};
    w.fetch = async url => {
        const u = String(url);
        if (u.includes('handler=Status')) { calls.status++; const body = status(calls.status); return { ok: true, status: 200, json: async () => body }; }
        calls.results++; const html = results(calls.results);
        return { ok: true, status: 200, redirected: false, url: u, text: async () => html };
    };
    w.eval(readFileSync(join(js, 'receivables-load-monitor.js'), 'utf8')); w.eval(readFileSync(join(js, 'receivables.js'), 'utf8'));
    const settle = async () => { for (let i = 0; i < 30; i++) await new Promise(r => setImmediate(r)); };
    await settle();
    // Runs the monitor's timers (the page's own watchdog is a long timer and is skipped).
    const pump = async (n = 50) => { for (let i = 0; i < n; i++) { const idx = timers.findIndex(t => t.ms < 60000); if (idx < 0) break; timers.splice(idx, 1)[0].fn(); await settle(); } };
    return { w, calls, pump, settle, q: s => w.document.querySelector(s) };
}

test('Receivables page: while Dubai loads, only the tiny status is polled; the list is re-read exactly once when it finishes', async () => {
    const ctx = await boot({ results: n => (n === 1 ? RUNNING : DONE), status: n => ({ available: true, active: n < 3, companies: [] }) });
    assert.equal(ctx.calls.results, 1);
    await ctx.pump();
    assert.equal(ctx.calls.status, 3);
    assert.equal(ctx.calls.results, 2, 'one initial read + one final read; polling never re-fetched the list');
    assert.equal(ctx.q('[data-load-running]'), null);
});

test('Receivables page: a refresh that never ends stops polling and says so, keeping the list', async () => {
    const ctx = await boot({ results: () => RUNNING, status: () => ({ available: true, active: true, companies: [] }) });
    await ctx.pump(200);
    assert.ok(ctx.calls.status <= 60, `bounded: ${ctx.calls.status} status polls`);
    assert.equal(ctx.calls.results, 1, 'the list was not re-requested');
    assert.ok(ctx.q('[data-poll-stopped]'), 'a stop notice replaces the endless "Loading data"');
    assert.equal(ctx.q('[data-load-running]'), null);
    assert.ok(ctx.q('[data-list-marker]'), 'the loaded list stays on screen');
    assert.ok(ctx.q('[data-poll-resume]'), 'there is a way to look again');
});

test('Receivables page: a failed company is final - nothing is polled at all', async () => {
    const ctx = await boot({ results: () => '<div data-load-problem>Could not load data for Tiger Group Dubai.</div><table data-list-marker></table>', status: () => ({ available: true, active: false }) });
    await ctx.pump();
    assert.equal(ctx.calls.status, 0);
    assert.equal(ctx.calls.results, 1);
});

test('Receivables page: editing a filter (preview invalidated) cancels the watch; no further polls and no obsolete list reload', async () => {
    const ctx = await boot({ results: () => RUNNING, status: () => ({ available: true, active: true, companies: [] }) });
    await ctx.pump(2);
    const before = ctx.calls.status;
    ctx.w.dispatchEvent(new ctx.w.CustomEvent('campaign-preview-invalidated'));
    await ctx.pump(50);
    assert.equal(ctx.calls.status, before);
    assert.equal(ctx.calls.results, 1);
});

test('Receivables page: Retry waits for the refresh to start instead of concluding "nothing is loading"', async () => {
    let statusCalls = 0;
    const ctx = await boot({
        results: n => (n === 1 ? '<div data-load-problem><form data-load-form action="/Collections/Receivables?handler=LoadCoverage" method="post"><input name="companyId" value="4"/><button type="submit" data-load-button>Retry</button></form></div><table data-list-marker></table>' : RUNNING),
        status: () => { statusCalls++; return { available: true, active: statusCalls >= 3, companies: [] }; }
    });
    const form = ctx.q('[data-load-form]');
    form.dispatchEvent(new ctx.w.Event('submit', { bubbles: true, cancelable: true }));
    await ctx.settle();
    await ctx.pump(3);
    assert.ok(statusCalls >= 3, 'idle answers right after the retry were tolerated (the job had not started yet)');
    assert.ok(ctx.q('[data-load-running]') || statusCalls >= 3);
});
