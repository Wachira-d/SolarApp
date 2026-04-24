// SolarApp service worker - basic offline shell + runtime cache
const CACHE = 'solarapp-v1';
const PRECACHE = [
    '/',
    '/css/site.css',
    '/js/site.js',
    '/manifest.webmanifest',
    '/favicon.ico'
];

self.addEventListener('install', (event) => {
    event.waitUntil(
        caches.open(CACHE).then((cache) => cache.addAll(PRECACHE)).catch(() => undefined)
    );
    self.skipWaiting();
});

self.addEventListener('activate', (event) => {
    event.waitUntil(
        caches.keys().then((keys) =>
            Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k)))
        )
    );
    self.clients.claim();
});

self.addEventListener('fetch', (event) => {
    const req = event.request;
    // Only GET and same-origin
    if (req.method !== 'GET') return;
    const url = new URL(req.url);
    if (url.origin !== self.location.origin) return;

    // Network-first for HTML pages (so users see fresh data).
    const isHtml = req.headers.get('accept') && req.headers.get('accept').includes('text/html');

    if (isHtml) {
        event.respondWith(
            fetch(req)
                .then((res) => {
                    const copy = res.clone();
                    caches.open(CACHE).then((c) => c.put(req, copy)).catch(() => undefined);
                    return res;
                })
                .catch(() => caches.match(req).then((r) => r || caches.match('/')))
        );
        return;
    }

    // Cache-first for static assets
    event.respondWith(
        caches.match(req).then((cached) => {
            if (cached) return cached;
            return fetch(req).then((res) => {
                if (res.ok) {
                    const copy = res.clone();
                    caches.open(CACHE).then((c) => c.put(req, copy)).catch(() => undefined);
                }
                return res;
            });
        })
    );
});
