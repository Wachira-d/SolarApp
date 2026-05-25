// ────────────────────────────────────────────────────────────────────
// My — customer portal client-side helpers
// Theme persistence, animated gauges, chart helpers, pull-to-refresh,
// live data polling.  No build step — vanilla ES6, runs anywhere.
// ────────────────────────────────────────────────────────────────────

const MyPortal = (() => {
    const THEME_KEY = 'my.theme';        // accent palette
    const MODE_KEY  = 'my.mode';         // light / dark / auto

    // ── Theme: persist + apply to <html> as data-attrs so CSS picks up
    function applyTheme(theme) {
        if (theme && theme !== 'green') {
            document.documentElement.setAttribute('data-my-theme', theme);
        } else {
            document.documentElement.removeAttribute('data-my-theme');
        }
        localStorage.setItem(THEME_KEY, theme || 'green');
    }
    function applyMode(mode) {
        const effective = mode === 'auto'
            ? (window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')
            : mode;
        if (effective === 'dark') {
            document.documentElement.setAttribute('data-my-mode', 'dark');
        } else {
            document.documentElement.removeAttribute('data-my-mode');
        }
        localStorage.setItem(MODE_KEY, mode || 'auto');
    }
    function restoreTheme() {
        applyTheme(localStorage.getItem(THEME_KEY) || 'green');
        applyMode(localStorage.getItem(MODE_KEY)  || 'auto');
    }

    // ── Gauge: SVG arc that animates to a fraction (0..1)
    function setGauge(el, fraction) {
        const fill = el.querySelector('.fill');
        if (!fill) return;
        const r = fill.getAttribute('r');
        const c = 2 * Math.PI * r;
        const f = Math.max(0, Math.min(1, fraction));
        fill.setAttribute('stroke-dasharray', c);
        fill.setAttribute('stroke-dashoffset', c * (1 - f));
    }

    // ── Number counter animation (eases to target)
    function animateNumber(el, to, decimals = 1) {
        const from = parseFloat(el.dataset.from || '0');
        const start = performance.now();
        const dur = 700;
        function tick(t) {
            const p = Math.min(1, (t - start) / dur);
            // easeOutQuart
            const eased = 1 - Math.pow(1 - p, 4);
            const v = from + (to - from) * eased;
            el.textContent = v.toFixed(decimals);
            if (p < 1) requestAnimationFrame(tick);
            else el.dataset.from = to;
        }
        requestAnimationFrame(tick);
    }

    // ── Chart.js helper: theme-aware line chart
    function renderLineChart(canvas, labels, datasets, opts = {}) {
        if (!canvas || typeof Chart === 'undefined') return null;
        const css = getComputedStyle(document.documentElement);
        const accent = css.getPropertyValue('--my-accent').trim() || '#2fc774';
        const text = css.getPropertyValue('--my-text-muted').trim() || '#888';
        return new Chart(canvas, {
            type: 'line',
            data: {
                labels,
                datasets: datasets.map(d => ({
                    tension: 0.35,
                    fill: true,
                    borderColor: d.color || accent,
                    backgroundColor: hexToRgba(d.color || accent, 0.12),
                    pointRadius: 0,
                    pointHoverRadius: 5,
                    borderWidth: 2.5,
                    ...d,
                })),
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                interaction: { mode: 'index', intersect: false },
                plugins: {
                    legend: { display: datasets.length > 1, labels: { color: text } },
                    tooltip: { intersect: false, mode: 'index' },
                },
                scales: {
                    x: { grid: { display: false }, ticks: { color: text } },
                    y: { beginAtZero: true, grid: { color: 'rgba(127,127,127,0.08)' }, ticks: { color: text } },
                },
                ...opts,
            },
        });
    }
    function hexToRgba(hex, a) {
        if (!hex.startsWith('#')) return hex;
        const h = hex.replace('#', '');
        const r = parseInt(h.slice(0, 2), 16);
        const g = parseInt(h.slice(2, 4), 16);
        const b = parseInt(h.slice(4, 6), 16);
        return `rgba(${r},${g},${b},${a})`;
    }

    // ── Live polling: refresh a JSON endpoint every N seconds
    function poll(url, intervalMs, callback) {
        async function tick() {
            try {
                const r = await fetch(url, { headers: { 'Accept': 'application/json' } });
                if (r.ok) callback(await r.json());
            } catch (e) { /* swallow — keep polling */ }
        }
        tick();
        return setInterval(tick, intervalMs);
    }

    return {
        applyTheme, applyMode, restoreTheme,
        setGauge, animateNumber,
        renderLineChart, poll,
    };
})();

// Restore the customer's chosen theme as early as possible to avoid
// a flash of default colours.  This runs before DOMContentLoaded so the
// CSS variables are already in place when the first paint happens.
MyPortal.restoreTheme();

// Follow system dark-mode changes when the user picked "auto"
window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
    if ((localStorage.getItem('my.mode') || 'auto') === 'auto') {
        MyPortal.applyMode('auto');
    }
});
