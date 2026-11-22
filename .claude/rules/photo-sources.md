---
paths:
  - "**/*Immich*"
  - "**/*PhotoSource*"
  - "**/*ImageBackup*"
  - "**/*UploadBasket*"
  - "**/UploadPreviewPane.razor"
---

# Photo sources (Immich, Image Backup)

- Both use Immich's REST API. MeuralImageBackup answers the subset `ImmichApiClient` uses: galleries count as albums, there are no people, and `search/smart` is a plain name search. They share one client, one proxy and one page component (`PhotoSourceBrowser.razor`). What differs between them lives in `Services/PhotoSource.cs`, whose `Key` prefixes their settings rows.
- Immich only accepts JPEG/PNG originals. For anything else, use the converted `fullsize` or `preview` (`GetBestImageAsync`). Each download gets its own subfolder.
- Browsers never see the API key. Thumbnails stream through `/immich-proxy/{token}/...` (a stable random token per account and source).
- `UploadBasket` is source-agnostic. Change it only through its methods, because it raises `Changed` and the page re-renders its sibling panes. A new source plugs in through callbacks; don't copy the basket.
- "Restore as playlist" uploads through a second basket of its own, with no crop dialog and no "already uploaded" question.
- Crop UI: the image stays fixed and an aspect-locked frame moves and resizes over it. No pan/zoom viewport. The starting aspect comes from `MeuralSessionState.GetCropSuggestion`.
