# Halyard brand

| File | What it is |
|---|---|
| `emblem-light.png`, `emblem-dark.png` | Full Halyard emblem (Fleet frame and wordmark) for light and dark backgrounds; the README uses the light one |
| `mark.svg`, `mark-256.png` | Icon-only mark, simplified to read at 32 px |
| `favicon.ico` | Browser icon (16, 32, 48) |
| `tray.ico` | Tray icon (16, 20, 24, 32) |

These follow `docs/UNIFIED-UI.md` section 8. They are not wired into the app yet; the UI, the favicon in
`web/index.html` and the mark in the emailed leadership report still use the earlier Cascade mark below.

## Earlier mark (from when Halyard was called Cascade)

A lowercase "c" built from three equal rounded arcs, opening to the right: theme → epic → story.

| Color | Hex | Use |
|---|---|---|
| Navy | `#1E3A5F` | top arc and wordmark on light backgrounds (white on dark) |
| Teal | `#14B8A6` | middle arc |
| Sky | `#38BDF8` | bottom arc |

Files: `cascade-mark.svg` / `cascade-mark-on-dark.svg` (bare mark), `cascade-app-icon.svg` (white rounded square),
`cascade-lockup*.svg` (mark + wordmark), `png/` (app icon 16–1024 px; `png/cascade-64.png` is embedded in the
emailed report), `cascade-mark-512.png`, `cascade.ico` (16–256 px). They keep their file names until the new
emblem is wired in, so the build and the report keep finding them.

The same geometry is inlined in `web/src/app.tsx` (sidebar) and `web/index.html` (favicon); change all three together.
