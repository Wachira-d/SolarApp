// ═══════════════════════════════════════════════════════════
// SolarApp client-side helpers
// ═══════════════════════════════════════════════════════════

// ─── Theme (dark/light) ───
(function () {
    const KEY = 'solarapp.theme';
    const root = document.documentElement;

    function apply(theme) {
        if (theme === 'dark') root.setAttribute('data-theme', 'dark');
        else root.removeAttribute('data-theme');
    }

    function current() {
        const saved = localStorage.getItem(KEY);
        if (saved) return saved;
        return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    }

    // Expose toggle globally
    window.toggleTheme = function () {
        const next = root.getAttribute('data-theme') === 'dark' ? 'light' : 'dark';
        localStorage.setItem(KEY, next);
        apply(next);
    };

    // Initial apply (runs at script load)
    apply(current());
})();

// ─── Toast helper ───
window.showToast = function (message, type) {
    type = type || 'success';
    const containerId = 'appToastContainer';
    let container = document.getElementById(containerId);
    if (!container) {
        container = document.createElement('div');
        container.id = containerId;
        container.className = 'toast-container position-fixed top-0 end-0 p-3';
        container.style.zIndex = '1080';
        document.body.appendChild(container);
    }
    const el = document.createElement('div');
    el.className = `toast align-items-center text-bg-${type} border-0 show`;
    el.setAttribute('role', 'alert');
    el.innerHTML = `<div class="d-flex"><div class="toast-body">${message}</div>
        <button type="button" class="btn-close btn-close-white me-2 m-auto" onclick="this.closest('.toast').remove()"></button></div>`;
    container.appendChild(el);
    setTimeout(() => el.remove(), 5000);
};

// ─── Auto-dismiss stale alerts ───
document.addEventListener('DOMContentLoaded', function () {
    document.querySelectorAll('.alert-success, .alert-info').forEach(function (a) {
        if (a.classList.contains('alert-permanent')) return;
        setTimeout(() => {
            a.style.transition = 'opacity 0.5s';
            a.style.opacity = '0';
            setTimeout(() => a.remove(), 500);
        }, 6000);
    });
});

// ─── PWA service worker registration ───
if ('serviceWorker' in navigator && window.location.protocol === 'https:') {
    window.addEventListener('load', function () {
        navigator.serviceWorker.register('/sw.js').catch(function (err) {
            console.warn('SW registration failed', err);
        });
    });
}
