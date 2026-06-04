// Service Worker for X Network PWA
// Version 1.0.0

const CACHE_NAME = 'xnetwork-cache-v5';
const RUNTIME_CACHE = 'xnetwork-runtime-v5';

// Assets to cache on install
const PRECACHE_ASSETS = [
    '/',
    '/app.css',
    '/manifest.json',
    '/js/animatedUi.js',
    '/js/blazorReconnect.js',
    '/js/statisticsCharts.js',
    '/js/uptimeChart.js',
    '/vendor/auto-animate/index.min.js',
    '/vendor/chartjs/chart.umd.min.js',
    '/vendor/fontawesome/css/all.min.css',
    '/vendor/fontawesome/webfonts/fa-brands-400.ttf',
    '/vendor/fontawesome/webfonts/fa-brands-400.woff2',
    '/vendor/fontawesome/webfonts/fa-regular-400.ttf',
    '/vendor/fontawesome/webfonts/fa-regular-400.woff2',
    '/vendor/fontawesome/webfonts/fa-solid-900.ttf',
    '/vendor/fontawesome/webfonts/fa-solid-900.woff2',
    '/vendor/fonts/inter-latin.woff2',
    '/vendor/odometer/odometer-theme-minimal.min.css',
    '/vendor/odometer/odometer.min.js',
    '/vendor/tailwind/tailwindcss-3.4.17.js',
    '/XNetwork.styles.css',
];

// Install event - cache essential assets
self.addEventListener('install', (event) => {
    console.log('[Service Worker] Installing service worker...');
    
    event.waitUntil(
        caches.open(CACHE_NAME)
            .then((cache) => {
                console.log('[Service Worker] Precaching assets');
                return cache.addAll(PRECACHE_ASSETS);
            })
            .then(() => {
                console.log('[Service Worker] Skip waiting on install');
                return self.skipWaiting();
            })
    );
});

// Activate event - clean up old caches
self.addEventListener('activate', (event) => {
    console.log('[Service Worker] Activating service worker...');
    
    event.waitUntil(
        caches.keys()
            .then((cacheNames) => {
                return Promise.all(
                    cacheNames
                        .filter((cacheName) => {
                            // Delete old caches
                            return cacheName !== CACHE_NAME && cacheName !== RUNTIME_CACHE;
                        })
                        .map((cacheName) => {
                            console.log('[Service Worker] Deleting old cache:', cacheName);
                            return caches.delete(cacheName);
                        })
                );
            })
            .then(() => {
                console.log('[Service Worker] Claiming clients');
                return self.clients.claim();
            })
    );
});

// Fetch event - implement caching strategies
self.addEventListener('fetch', (event) => {
    const { request } = event;
    const url = new URL(request.url);

    // Skip non-GET requests
    if (request.method !== 'GET') {
        return;
    }

    // Skip Blazor SignalR connections
    if (url.pathname.includes('/_blazor')) {
        return;
    }

    // Skip API calls that need fresh data (use network-first)
    if (url.pathname.includes('/api/') || url.pathname.includes('/_framework/')) {
        event.respondWith(networkFirst(request));
        return;
    }

    // For static assets, use cache-first strategy
    if (isStaticAsset(url)) {
        event.respondWith(cacheFirst(request));
        return;
    }

    // For navigation requests, use network-first with cache fallback
    if (request.mode === 'navigate') {
        event.respondWith(networkFirst(request));
        return;
    }

    // Default: try network first, fall back to cache
    event.respondWith(networkFirst(request));
});

// Cache-first strategy (for static assets)
async function cacheFirst(request) {
    const cache = await caches.open(CACHE_NAME);
    const cached = await cache.match(request);
    
    if (cached) {
        console.log('[Service Worker] Cache hit:', request.url);
        return cached;
    }

    try {
        const response = await fetch(request);
        
        // Cache successful responses
        if (response && response.status === 200) {
            cache.put(request, response.clone());
        }
        
        return response;
    } catch (error) {
        console.error('[Service Worker] Fetch failed:', error);
        throw error;
    }
}

// Network-first strategy (for dynamic content)
async function networkFirst(request) {
    const cache = await caches.open(RUNTIME_CACHE);
    
    try {
        const response = await fetch(request);
        
        // Cache successful responses
        if (response && response.status === 200) {
            cache.put(request, response.clone());
        }
        
        return response;
    } catch (error) {
        console.log('[Service Worker] Network failed, trying cache:', request.url);
        const cached = await cache.match(request);
        
        if (cached) {
            return cached;
        }
        
        // If no cache available, try the precache
        const precached = await caches.match(request);
        if (precached) {
            return precached;
        }
        
        throw error;
    }
}

// Helper function to determine if a URL is a static asset
function isStaticAsset(url) {
    const staticExtensions = ['.css', '.js', '.png', '.jpg', '.jpeg', '.gif', '.svg', '.woff', '.woff2', '.ttf', '.eot'];
    return staticExtensions.some(ext => url.pathname.endsWith(ext));
}

// Listen for messages from clients
self.addEventListener('message', (event) => {
    if (event.data && event.data.type === 'SKIP_WAITING') {
        self.skipWaiting();
    }
});
