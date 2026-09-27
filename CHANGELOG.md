# Changelog

All notable changes to DNotes are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[semantic versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0] - 2026-09-27

First public release. Native C# / .NET Framework 4.8 rebuild of the edge-docked note
concept, simplified to a single floating **DANISHYAR** button.

### Added

- **Floating button** — a transparent-cornered disc carrying the emblem, draggable,
  edge-snapping, and set to `WS_EX_NOACTIVATE` so it never steals focus while you type.
  Left click opens a six-node radial fan, right click a plain menu.
- **Note windows** — a real Win32 `RichTextBox` body with a custom-painted neon caret
  (a stock caret is near-black and vanishes on a dark note), a title field, and a footer
  carrying bold/italic/underline, six accent colours, archive and delete.
- **Note resizing** — the window is borderless, so it ships its own grips on all four
  edges and corners plus a visible handle in the bottom-right. Limits are 280 × 190
  minimum and 900 × 1100 logical maximum; geometry is stored in logical units so a note
  keeps its apparent size across display scale changes.
- **All Notes** — searchable across titles, bodies and tags, filterable to All, Active or
  Archived, with `Ctrl`-click multi-select, `Ctrl`+`A`, and a **DELETE** button that
  refuses to act on an empty selection.
- **Import / export** — Markdown, plain text, a single document, or a `.dnotes` archive
  that round-trips colours, states and dates.
- **Search inside a note** (`Ctrl`+`F`), which replaces the footer rather than covering
  the text.
- **87-check end-to-end self-test** (`--selftest`) that drives real `SendInput` mouse and
  keyboard traffic and samples real screen pixels.
- A process-level exception guard that logs, notifies once, and carries on — plus a
  self-test check that fails the run if the guard ever had to catch something.

### Fixed

Bugs found and closed during the build, each now covered by a self-test check:

- **Crash on hover.** `new Cursor(Native.LoadCursor(...))` threw
  `ArgumentException`/`OutOfMemoryException` on every mouse move, because a
  `LoadCursor` handle is a shared system resource the `Cursor` class may not own — and
  it leaked a handle per move besides. Now uses the shared `Cursors.Hand`, and the
  leaked `HICON` is released.
- **Unreadable note previews in All Notes.** The preview was right-aligned by measuring
  the text and starting the draw *outside* its own clip rect, which makes GDI+ stack the
  glyphs on top of each other; the clip was also too short, slicing the descenders. The
  stored notes were never damaged — it was purely a painting fault. Previews are now
  measured and trimmed to the gap they are given and drawn with no clip at all.
- **Notes could not be made bigger.** There was no resize handling of any kind, so a
  borderless note was stuck at whatever size it opened at. See *Note resizing* above.
- **Dead corner grips.** `ApplyRegion` rounds the corners to 14 px, so the pixels right at
  the corner fall outside the window region and clicks passed straight through them. The
  corner target was widened to clear the radius.
- **Footer controls overlapped on wide notes.** The one-row palette computed how many
  swatches fit but positioned them against the content edge rather than that limit, so on
  any note wide enough to show all six they slid underneath ARCHIVE and DELETE.
- **A note could be shrunk below its own footer.** The minimum width was 240 logical while
  the measured footer needs 275, so the buttons stacked. The floor is now 280.
- **No way to delete from All Notes.** The deck had carried the delete action and went
  away with it, leaving the `Delete` key as the only route. A **DELETE** button now sits
  beside **OPEN** and is disabled until a row is ticked.
- **A latent risk in the delete path.** `Selection()` deliberately falls back to "every
  visible note" so `OPEN` can act on the whole list. Delete now requires an explicit
  selection so that fallback can never turn one click into "delete all seven notes".
- **All Notes footer could collide.** Seven actions at a fixed width would have overlapped
  IMPORT below 640 logical. Button width is now derived from the window width, and a
  self-test sweep checks 33 widths.

### Known issues

- The self-test's keystroke checks are timing-sensitive and can report a false failure
  when another application grabs the foreground mid-run. It is not run in CI for exactly
  this reason; run it locally from an interactive session.
- The single-instance "signal the running copy" path in `Program.cs` sets an event that
  nothing waits on, so a second launch does not raise the existing window. Harmless, but
  it is dead code.

[Unreleased]: https://github.com/saboorsarem/DNotes/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/saboorsarem/DNotes/releases/tag/v1.0.0
