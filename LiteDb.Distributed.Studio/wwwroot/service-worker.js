const cacheName = "litedb-distributed-studio-v1";
const appShell = [
    "./",
    "./index.html",
    "./css/app.css",
    "./favicon.png",
    "./icon-192.png",
    "./icon-512.png",
    "./manifest.webmanifest"
];

self.addEventListener("install", event => {
    event.waitUntil(
        caches.open(cacheName)
            .then(cache => cache.addAll(appShell))
            .then(() => self.skipWaiting())
    );
});

self.addEventListener("activate", event => {
    event.waitUntil(
        caches.keys()
            .then(keys => Promise.all(keys.filter(key => key !== cacheName).map(key => caches.delete(key))))
            .then(() => self.clients.claim())
    );
});

self.addEventListener("fetch", event => {
    if (event.request.method !== "GET") {
        return;
    }

    const requestUrl = new URL(event.request.url);
    if (requestUrl.origin !== self.location.origin || requestUrl.pathname.startsWith("/api/")) {
        return;
    }

    if (event.request.mode === "navigate") {
        event.respondWith(fetch(event.request).catch(() => caches.match("./index.html")));
        return;
    }

    event.respondWith(
        caches.match(event.request).then(cachedResponse => {
            const networkResponse = fetch(event.request)
                .then(response => {
                    if (response.ok) {
                        const responseCopy = response.clone();
                        caches.open(cacheName).then(cache => cache.put(event.request, responseCopy));
                    }

                    return response;
                })
                .catch(() => cachedResponse);

            return cachedResponse || networkResponse;
        })
    );
});
