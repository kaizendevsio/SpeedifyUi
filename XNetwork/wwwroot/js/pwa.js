// PWA Registration Script for X Network
// This script registers the service worker and handles PWA installation

(function() {
    'use strict';

    // Check if service workers are supported
    if ('serviceWorker' in navigator) {
        // Wait for page load to register service worker
        window.addEventListener('load', function() {
            registerServiceWorker();
        });
    } else {
        console.warn('Service Workers are not supported in this browser');
    }

    async function registerServiceWorker() {
        try {
            const registration = await navigator.serviceWorker.register('/service-worker.js', {
                scope: '/'
            });

            console.log('Service Worker registered successfully:', registration.scope);

            // Check for updates
            registration.addEventListener('updatefound', () => {
                const newWorker = registration.installing;
                console.log('New Service Worker found, installing...');

                newWorker.addEventListener('statechange', () => {
                    if (newWorker.state === 'installed' && navigator.serviceWorker.controller) {
                        // New service worker available, notify user
                        console.log('New content is available; please refresh.');
                        showUpdateNotification();
                    }
                });
            });

            // Check for updates every hour
            setInterval(() => {
                registration.update();
            }, 60 * 60 * 1000);

        } catch (error) {
            console.error('Service Worker registration failed:', error);
        }
    }

    function showUpdateNotification() {
        // You can implement a custom notification here
        // For now, we'll just log it
        console.log('A new version of the app is available. Refresh to update.');
        
        // Optional: Auto-reload after a delay
        // setTimeout(() => {
        //     window.location.reload();
        // }, 3000);
    }

    // Handle install prompt
    let deferredPrompt;

    window.addEventListener('beforeinstallprompt', (e) => {
        console.log('beforeinstallprompt event fired');
        
        // Prevent the mini-infobar from appearing on mobile
        e.preventDefault();
        
        // Stash the event so it can be triggered later
        deferredPrompt = e;
        
        // Update UI to notify the user they can install the PWA
        showInstallPromotion();
    });

    function showInstallPromotion() {
        // You can show a custom install button here
        console.log('App can be installed');
        
        // Example: Create an install button (you can customize this)
        // const installButton = document.getElementById('install-button');
        // if (installButton) {
        //     installButton.style.display = 'block';
        //     installButton.addEventListener('click', async () => {
        //         if (deferredPrompt) {
        //             deferredPrompt.prompt();
        //             const { outcome } = await deferredPrompt.userChoice;
        //             console.log(`User response to the install prompt: ${outcome}`);
        //             deferredPrompt = null;
        //             installButton.style.display = 'none';
        //         }
        //     });
        // }
    }

    // Track successful installation
    window.addEventListener('appinstalled', (e) => {
        console.log('PWA was installed successfully');
        deferredPrompt = null;
    });

    // Check if app is running in standalone mode (installed)
    if (window.matchMedia('(display-mode: standalone)').matches || window.navigator.standalone === true) {
        console.log('App is running in standalone mode (installed)');
    }

    // Handle iOS specific install detection
    window.addEventListener('DOMContentLoaded', () => {
        const isIos = /iPad|iPhone|iPod/.test(navigator.userAgent);
        const isStandalone = window.navigator.standalone;

        if (isIos && !isStandalone) {
            console.log('Running on iOS - Show install instructions for Add to Home Screen');
            // You can show iOS-specific install instructions here
        }
    });

})();