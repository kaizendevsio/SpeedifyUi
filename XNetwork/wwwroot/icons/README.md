# PWA Icons Directory

This directory contains the application icon for the Progressive Web App.

## Current Icon

**logo.png** - The application icon used for all PWA purposes

This single icon file is referenced in the PWA manifest for all required icon sizes (72x72 through 512x512). The browser will automatically scale the icon as needed for different contexts.

## Icon Usage

The logo.png file is used for:
- Android home screen icons (various sizes)
- Android splash screens
- iOS home screen (via apple-touch-icon)
- PWA installation prompts
- Browser tabs and bookmarks

## Purpose Configuration

The icon is configured with `"purpose": "any maskable"` in the manifest, which provides:
- **any**: Standard icon display
- **maskable**: Adaptive icon support for Android, allowing the OS to apply different shapes and masks

This dual-purpose configuration ensures the best compatibility across different platforms and use cases.