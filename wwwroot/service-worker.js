// Minimal service worker: just enough to make the app installable as a PWA.
//
// SimpleDiffusion is a Blazor *Server* app — the UI lives over a SignalR (WebSocket) connection,
// so there is nothing useful to cache for offline use. We therefore deliberately do NOT intercept
// requests with caching; the fetch handler is a pure network pass-through. Its presence (plus the
// manifest + icons) satisfies the browser's installability criteria without breaking the live
// connection.

self.addEventListener('install', (event) => {
    self.skipWaiting();
});

self.addEventListener('activate', (event) => {
    event.waitUntil(self.clients.claim());
});

self.addEventListener('fetch', (event) => {
    // Network pass-through. No caching (Blazor Server can't run offline anyway).
    event.respondWith(fetch(event.request));
});
