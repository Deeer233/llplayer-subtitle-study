const version = 'review-shell-1';
const shell = ['/', '/app.js', '/style.css'];
self.addEventListener('install', event => {
  event.waitUntil(caches.open(version).then(cache => cache.addAll(shell)).then(() => self.skipWaiting()));
});
self.addEventListener('activate', event => {
  event.waitUntil(caches.keys().then(keys => Promise.all(keys.filter(k => k.startsWith('review-shell-') && k !== version).map(k => caches.delete(k))))
    .then(() => self.clients.claim()));
});
self.addEventListener('fetch', event => {
  const url = new URL(event.request.url);
  if (event.request.method !== 'GET' || url.origin !== self.location.origin || !shell.includes(url.pathname) || url.search) return;
  event.respondWith(caches.open(version).then(cache => cache.match(url.pathname)).then(result => result ?? fetch(event.request)));
});
