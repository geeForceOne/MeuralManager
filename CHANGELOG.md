# Release notes

## 1.12.0 - 2026-10-10

### New

- Immich: compare two photos before choosing. "Compare" in the preview pins a photo as A, and every photo you click next shows as B - side by side, or in the same spot with "Flip". "Keep A" or "Keep B" puts the winner in the basket and keeps it for the next comparison.
- The logo now moves: when you hover it, on the sign-in screen, and while a scan is running.

### Improved

- The Activity Log now starts collapsed everywhere; click it to open it.

### Fixed

- Minor fixes and updates.

## 1.11.0 - 2026-09-23

### New

- Stats API (playlist count, image count, last scan time) for dashboards like Homepage. Create a token in Settings.

## 1.10.0 - 2026-09-21

### New

- Immich: new layout - photos, an upload basket, and a preview pane. Click a photo to preview it; add it to the basket with the check mark. Rename photos in the basket (with AI suggestions) before uploading, and optionally show what's already in the target playlist.
- Settings: set each frame to 16:9 or 9:16. Cropping now opens on the format of the frames a playlist is installed on (9:16 if unsure or mixed).

### Improved

- Preview panes (Playlists, All Pictures, Immich): the image now fills the pane, with its details below.
- Feature toggles (Picture of the Moment, crop tool, remote control, Immich) are now saved per account instead of per browser.
- Settings: a saved API key that can no longer be read now says so instead of silently showing as empty; the page also uses the full width.
- New logo, sidebar icons and browser-tab icon.

## 1.9.0 - 2026-09-20

### New

- Optional Immich integration (off by default).

## 1.8.0 - 2026-09-05

### New

- Playlists: a "Remove from frame" dropdown next to "Install on frame" - each one only lists frames the action actually applies to (install hides frames that already have the playlist, remove only lists frames that do).

### Improved

- Frame remote control toolbar: the playlist dropdown is now sorted alphabetically, and updates immediately when a playlist is installed on or removed from a frame elsewhere in the app instead of waiting for the next refresh.

## 1.7.1 - 2026-09-04

### Improved

- Frame remote control toolbar: the current-image thumbnail now shows a hand cursor on hover and its press-and-hold preview is bigger.

## 1.7.0 - 2026-09-04

### New

- Frame remote control toolbar at the bottom of the app - next/previous image, power on/off, and switching the loaded playlist, straight from the browser, plus a current-image thumbnail. Supports multiple frames and multiple toolbar instances at once. Talks directly to the frame over your local network, so it needs a fixed IP (a DHCP reservation) and network access from wherever this app runs - can be turned off in Settings.

## 1.6.0 - 2026-09-03

### New

- Playlists: uploading more than one image now offers "Upload all as-is" to skip the crop prompt for the rest of the batch in one click.
- Playlists: mark playlists as favorites - they always sort to the top of the list, with whatever sort you've picked still applying within the favorites and the rest.

### Improved

- Playlists: uploads that fail (e.g. a Meural timeout) now show which file failed and why, with a Retry button per file and a Retry all.
- Playlists: an upload that times out is now double-checked against the account before being reported as failed - if Meural actually received it, it's added to the playlist with no error shown.
- Dashboard: "Last scanned" now shows the date as well as the time.

## 1.5.0 - 2026-09-02

### New

- Playlists: crop images to 16:9 or 9:16, non-destructively - a Revert to original button restores the pre-crop image at any time.

### Improved

- Playlists: the three-pane layout now remembers its pane sizes across visits.

## 1.4.0 - 2026-09-01

### New

- Playlists: select all images in a playlist with one click, then download the selected images as a ZIP.
- Playlists and All Pictures: get an AI-suggested name for an image with one click (Claude or ChatGPT - add API keys, pick models, and choose a Professional or Creative rename style in Settings).
- Playlists: Select NoName picks out images with a generic name (like "IMG_1234" or a Facebook/Unsplash-style export name) in one click, and AI rename applies AI-suggested names to the whole selection at once.

### Improved

- Images now download once and are cached locally, so thumbnails and previews load instantly on repeat views instead of re-fetching from Meural every time - a progress indicator in the sidebar shows the initial background caching as it happens.
- All Pictures, Orphan Uploads, and Playlists now render large lists much faster.
- Removed the Activity Log from All Pictures to give the list more room.

### Fixed

- Switching playlists quickly sometimes left the previous playlist's (stale) items on screen.

## 1.3.1 - 2026-08-31

### Fixed

- Thumbnails and previews (including "Picture of the Moment") occasionally showed a broken image because their cached CDN link had expired - a fresh link is now fetched automatically.

## 1.3.0 - 2026-08-30

### New

- All Pictures page: browse, search, sort, rename, and delete every uploaded image, see which playlist(s) it's in, and preview the selected image.

## 1.2.0 - 2026-08-29

### New

- Click-to-rename for playlist images, right from their thumbnail.

### Fixed

- CSS/JS changes required a hard-refresh to show up after a redeploy.

## 1.1.0 - 2026-08-28

### Fixed

- Missing blazor.web.js in published Docker images.
- The scan cache didn't persist across container restarts.

## 1.0.0 - 2026-08-28

Initial release.
