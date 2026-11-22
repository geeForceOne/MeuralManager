---
paths:
  - "src/MeuralManager.Web/**"
---

# Web pipeline and UI

- Static files: reference them in `App.razor` as `@Assets["css/app.css"]` (fingerprinted), never as a plain path.
- New minimal-API endpoints need `.WithMetadata(new SkipStatusCodePages())`, or they get the NotFound page instead of their 401/404.
- `Pages/Error.razor` renders statically (`[ExcludeFromInteractiveRouting]`) so it still shows when the circuit is broken.
- Theme: every dark rule exists twice in `app.css`, under `@media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) … }` and under `:root[data-theme="dark"]`. Keep the two identical. The accent is `#bc8f68` in both themes.
- Preview panes: `.preview-image-wrap` fills the remaining height. Don't put a `max-height` on `.preview-image`.
- New sidebar entries get a purpose-drawn inline SVG: 24px viewBox, outline, stroke-width 1.8, round caps. Check that it still reads at 18px.
- The sidebar logo stays still. `BrandMark` motions run only on the login/Dashboard logo and while busy.
- Don't name a component parameter `Assets` (it collides with `ComponentBase.Assets`).
- Blazor renders `aria-pressed="@x"` as an empty attribute. Write `@(x ? "true" : "false")`.
- A plain `Func<>` callback doesn't re-render the parent. Call `StateHasChanged`.
- Numbers in SVG attributes use the invariant culture.
- Table cells are never `display: flex`. Put a flex `<div>` inside the cell.
