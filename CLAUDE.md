# CLAUDE.md

MeuralManager: a Docker-hosted Blazor Server app that cleans up a Meural Canvas cloud account (uploads no playlist uses, playlists no frame shows, with optional ZIP backup first) and manages playlists and frames. Two optional photo sources, Immich and Image Backup (github.com/geeForceOne/MeuralImageBackup), upload photos straight into playlists. Both are off by default.

## Commands

```
dotnet build MeuralManager.slnx              # .slnx, there is no .sln
dotnet run --project src/MeuralManager.Web   # then sign in at the printed URL
dotnet test tests/MeuralManager.Tests        # xUnit, pure logic only, no Meural calls
docker compose up --build
```

There is no lint config.

## Layout

- `src/MeuralManager.Core`: everything that touches Meural, Immich or disk. No UI dependency and no `Console.*`. Long-running methods take `IProgress<string>?` and a `CancellationToken`.
- `src/MeuralManager.Web`: the Blazor UI. `Services/MeuralSessionState.cs` is the per-tab hub. It owns the one shared account scan, backed by a per-account SQLite cache (`<cache root>/<email>/meural-cache.db`). Every change patches that cache in place; nothing forces a rescan.
- Per-account settings (feature toggles, theme, API keys, frame orientations) live in that SQLite settings table. API keys are Data-Protection-encrypted. Only splitter widths stay in browser storage.
- Area notes load automatically from `.claude/rules/` when you work on matching files: the Meural API, photo sources, the web pipeline, and UI/CSS.

## Conventions

- **Verify external APIs, don't work from memory.** The Meural API is undocumented: check `davemorin/meural-manager`, `GuySie/ha-meural`, or the Postman collection before assuming an endpoint's shape. Immich ships an OpenAPI spec per release. Model IDs and SDK shapes change.
- Every outgoing Meural/Netgear request sends a `User-Agent`. Without one, Netgear's WAF blocks it.
- Releases need no version bump: the git tag is the version. Release notes live only in `CHANGELOG.md` (`## X.Y.Z - YYYY-MM-DD`, then `### New` / `### Improved` / `### Fixed`, written for users).
- All text (UI, docs, comments) uses American spelling.
- Dialogs never close on an outside click. Use Cancel, or Escape where it's offered.
- The Activity Log panel starts collapsed and only the user's click opens it. Never open it from code.
- The repo is public. Never commit keys, `*.db`, `keys/`, `cache/`, `.env` or backup zips.
