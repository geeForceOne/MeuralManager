---
paths:
  - "src/MeuralManager.Core/**"
---

# Meural API

- `Api/MeuralApiClient.cs` is the only class that calls `api.meural.com/v1` (`x-meural-api-version: 4`). All write calls live in `MeuralApiClient.Changes.cs`. Paginated lists go through `GetAllPagesAsync<T>`.
- Login (`Api/NetgearAuthenticator.cs`) mirrors ha-meural: Cognito `CUSTOM_AUTH`, falling back to `USER_PASSWORD_AUTH`, then a Meural OAuth token exchange.
- Postman collection JSON (fetch this, not the SPA page): https://documenter.gw.postman.com/api/collections/1657302/RVnWjKUL?segregateAuth=true&versionTag=latest. `PUT items/:id` with crop fields does nothing (tested). Don't rebuild server-side crop.
- **Timeouts.** A Meural timeout doesn't mean the change failed. Every write call goes through `Api/TimeoutGuard.cs`, which runs a cheap check of its own every 10 s once a call has waited 15 s. The first answer wins, an unknown outcome gets three last checks, and only a confirmed "not done" counts as a failure. Never retry blindly.
- A fresh upload gets a random 7-character name until it is renamed. Recognize it as the one image newer than the previous newest (`PlaylistService.FindTimedOutUpload`), never by its name.
- Delete and bulk loops pause 1 s between calls on purpose.
- Images come from a CDN host: use the unauthenticated `HttpClient`s (`BackupService`, `ImageDownloader`), not the API client.
