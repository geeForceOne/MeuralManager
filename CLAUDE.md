# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

MeuralManager is a Docker-hosted web app for cleaning up a Meural ("Canvas" digital art frame) cloud account. Meural doesn't delete uploaded images when you delete a playlist that contained them, so orphaned uploads silently pile up against the account's storage quota. This app finds:
- uploaded images that aren't referenced by any playlist, and
- playlists that aren't loaded on any Canvas frame,

and lets the user optionally back up the images (as a downloadable ZIP) before deleting the orphans from the Meural account. It also has full playlist management: browse playlists and their images (with thumbnails), create/rename/delete playlists, add existing uploads or newly-uploaded local images to a playlist, remove images from a playlist, and install a playlist onto a Canvas frame.

## Commands

```
dotnet build MeuralManager.slnx              # build everything
dotnet run --project src/MeuralManager.Web   # run the app (browse to the printed localhost URL, then sign in)
docker compose up --build               # build and run the container (see docker-compose.yml)
```

There is no `.sln` file — this repo uses the newer `.slnx` XML solution format (`MeuralManager.slnx`). There are no test projects and no lint config yet.

## Workflow

When the user gives feedback or requests changes in a back-and-forth (bug reports, tweaks, "also do X"), don't implement each one immediately as it comes in. Acknowledge it, queue it, and keep gathering — only start making changes once the user explicitly says "go" (or an equally unambiguous confirmation). This lets them batch up a full list of feedback before any code gets touched. Purely informational questions (no code change requested) can still be answered right away.

## Git workflow

Never run `git commit` or `git push` unless the user explicitly asks for it in that turn. Finish the change and leave it uncommitted in the working tree; tell the user it's ready and wait for an explicit go-ahead before committing (and again before pushing — a commit approval does not imply a push approval, and approval from an earlier turn does not carry forward to later changes).

When the user does ask you to commit:
- Write clean, specific commit messages describing *why*, not just what changed.

## Architecture

Two projects, split so all Meural networking/business logic is reusable and UI-agnostic:

- **`src/MeuralManager.Core`** (`net10.0` class library) — everything that talks to Meural or touches disk, with no dependency on the web UI:
  - `Api/MeuralApiClient.cs` — the only class that makes Meural API calls (`api.meural.com/v1`, `x-meural-api-version: 4`). Delegates authentication to `Api/NetgearAuthenticator.cs`, which implements Netgear Accounts' current Cognito-backed login (`CUSTOM_AUTH`, falling back to `USER_PASSWORD_AUTH` for unmigrated accounts, then exchanging the Cognito session for a Meural OAuth token pair) — the same flow Meural's own Home Assistant integration uses (github.com/GuySie/ha-meural). A missing `User-Agent` header will get requests blocked by Netgear's WAF, so both classes always send one. Paginated list endpoints (`user/items`, `user/galleries`, `user/devices`, `devices/{id}/galleries`) all go through the shared `GetAllPagesAsync<T>` helper.
  - `Models/` — records mapping the Meural API JSON: `MeuralItem`, `MeuralGallery`, `MeuralDevice`, `MeuralPage<T>` (paginated list envelope), `MeuralItemEnvelope`/`GalleryEnvelope` (single-object envelopes, used by `GET items/{id}` and the POST/PUT `galleries` responses).
  - `Services/CleanupService.cs` — pure set-difference logic (`FindOrphanItems`, `FindUnusedGalleries`) plus the delete loops (`DeleteItemsAsync`/`DeleteGalleriesAsync`), which impose a deliberate 1-second delay between deletes to avoid hammering the API.
  - `Services/BackupService.cs` — downloads item images to disk before deletion, using a **separate, unauthenticated `HttpClient`** (images live on a different CDN host, not `api.meural.com`).
  - `Services/PlaylistService.cs` — bulk playlist-membership operations (`AddItemsToGalleryAsync`, `RemoveItemsFromGalleryAsync`, `UploadAndAddToGalleryAsync`), same loop/progress/cancellation/politeness-delay shape as `CleanupService`'s delete loops.
  - `Services/ImageDownloader.cs` — one shared, unauthenticated `HttpClient` used only for thumbnails (many short-lived requests over the app's lifetime, vs. `BackupService`'s per-operation client for one-shot bulk downloads).
  - `Services/FileNaming.cs` — filename sanitization, image-extension guessing, and content-type guessing (for multipart upload), shared across the backup and upload paths.
  - `Data/PlaylistCacheStore.cs` — a local SQLite cache of playlists/items/devices/frame-assignments, so browsing doesn't have to hit the (slow) Meural API on every read. `MeuralManager.Web` keys one of these per signed-in account.
  - Every long-running method takes an `IProgress<string>?` (for status/log messages) and a `CancellationToken` — there is no `Console.*` or other I/O baked into Core, so the same methods drive both a UI log/toast and cancellation.
  - **The Meural API is not publicly documented.** Read/delete endpoints and the login flow were reverse-engineered from Meural's official Home Assistant integration. The playlist-CRUD endpoints (`GET galleries/{id}/items`, `POST galleries`, `PUT galleries/{id}`, `POST`/`DELETE galleries/{galleryId}/items/{itemId}`, `POST items` multipart upload, `PUT items/{id}`) were verified by reading the actual `fetch()` calls and request bodies in `davemorin/meural-manager` (github.com/davemorin/meural-manager), an open-source, working Meural web manager — not guessed. If a future endpoint is needed, check that repo (or a similar working client) before assuming a REST shape.

- **`src/MeuralManager.Web`** (`net10.0`, Blazor Server) — the UI, packaged for Docker via `Dockerfile`/`docker-compose.yml`:
  - `Program.cs` wires up the Blazor Web App pipeline (interactive server render mode, no prerendering so JS interop/localStorage is available immediately), Data Protection key persistence (`DATA_PROTECTION_KEYS_PATH`), and the backup-download minimal API endpoint.
  - `Services/MeuralSessionState.cs` is the per-circuit (one browser tab = one session) hub: holds the signed-in `MeuralApiClient`, owns the one shared full-account "scan" (items/galleries/devices/frame-assignments) that the Dashboard, Playlists, Orphan Uploads, and Unused Playlists pages all read from instead of each re-fetching independently, and backs that scan with a per-account `PlaylistCacheStore` (`<cache root>/<email>/meural-cache.db`) so it survives a container restart. Every mutation (create/rename/delete/add/remove/upload/install-on-frame) patches this cache in place rather than forcing a rescan.
  - `Services/WebSessionStore.cs` / `Services/UserPreferencesStore.cs` persist the login session and UI preferences (e.g. "Picture of the Moment") client-side via `ProtectedLocalStorage` (encrypted with ASP.NET Core Data Protection) — the former tied to the signed-in account, the latter a plain per-browser preference.
  - `Services/BackupArchiveService.cs` + `BackupCleanupService.cs` — backups are staged into a temp directory, zipped, and served once via `/backups/{id}/download`; a background service sweeps old zips.
  - `Components/Layout/MainLayout.razor` gates the whole app on `MeuralSessionState.IsAuthenticated` (showing `Components/Shared/Login.razor`, which handles the password + MFA-challenge flow, otherwise) and renders a non-dismissable full-screen overlay while `Session.IsScanning` is true so navigation can't interrupt a scan.
  - `Components/Pages/` — `Dashboard` (scan trigger + status, tool shortcuts, "Picture of the Moment"), `Playlists` (resizable three-pane browser: sortable playlist table, item thumbnail grid, image preview), `OrphanUploads`, `UnusedGalleries`, `Settings`.
  - `Components/Shared/` — small reusable pieces used across pages: `ConfirmDialog`/`PromptDialog` (imperative modal dialogs awaited from code), `AddExistingItemsModal` (searchable thumbnail picker), `ActivityLogPanel`, `Toast` (transient success/error banner).
  - Hand-written CSS design system in `wwwroot/css/app.css` (dark-first, light via `prefers-color-scheme`) — no component library.
