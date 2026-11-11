# Murmuration for Uno Platform: spec

Four flock controls that share one overlay. Ported from the HTML motion studies.

## Architecture Brief

- **Module structure:** `src/Murmuration` (class library, `net10.0` and `net10.0-desktop`, UnoFeature `Skia`) and `src/Murmuration.Gallery` (desktop sample app). Uno.Sdk 7.0.5, bundled SkiaSharp 4.151.1.
- **Shared overlay:** `FlockHost` is a `Grid` that adds one `SKCanvasElement` on top of its children. Every flock control inside it draws birds there, so birds can cross control boundaries.
- **Simulation:** `Flock` holds one flock in overlay coordinates: boids with a seven-neighbour cap, two attractors, soft walls, and perch landing. No allocation per frame except one detached `SKPath` per colour bucket.
- **Ink:** `Ink.FromText` rasterises a word with Skia. `Ink.CaptureAsync` renders a live element with `RenderTargetBitmap` and samples its pixels. Both return points the flock can start from or land on.
- **State model:** each control owns its state machine and an optional `Flock`. No MVVM or MVUX dependency. Inputs are dependency properties and methods.
- **Threading:** the host's `DispatcherTimer` (16 ms) ticks controls and steps flocks on the UI thread under `FlockHost.Gate`. `RenderOverride` takes the same lock. Dependency properties are never read on the render thread.
- **Visibility:** the host listens to `LayoutUpdated`. A control inside a collapsed ancestor keeps its flock but stops stepping and drawing. The timer stops when nothing is animating.
- **Platform constraint:** Skia rendering is required. Without it, or with `ReduceMotion`, each control falls back to a plain fade or bar.
- **Validation:** solution builds with 0 warnings. Each control was run on Linux desktop (X11, software rendering) and checked from screenshots.

## Design Brief

- **Visual direction:** birds are short strokes along their velocity, 1.15 to 1.9 px wide by depth. A resting bird is a dot.
- **Colour:** `Foreground` sets bird and text colour, defaulting by theme. `AccentBrush` sets perched birds on the progress bar, defaulting to `SystemAccentColor`.
- **Typography:** `FlockStreamingText` draws its own text with Skia (`FontSize`, `LineHeight`, `FontFamilyName`), so glyphs and birds register exactly.
- **Layout:** each control sizes by normal layout. The flock uses the control's bounds as its sky. `FlockProgressBar` puts the wire on its bottom edge.
- **Adaptive behaviour:** orbit sizes scale with the control's width and height. Bird counts are properties.

## Interaction Brief

- **Progress:** birds arrive when `Value` first rises, land left to right as it grows, calm during a stall, and a small wave marks completion. Setting `Value` back to 0 lifts the perched birds off; with nothing to do they leave after 2 s.
- **Thinking:** `StartAsync(source)` lifts the source element's marks off as birds. `State` selects a tight orbit, two lobes, or a wide sweep. `HandOffTo(streamingText)` passes the flock on. `Stop()` fades it.
- **Streaming:** `Append(token)` lands each finished word with borrowed birds that then rejoin the flock. `Complete()` sends the flock to `ReturnTo` and fades it.
- **Loading:** `IsLoading = true` lifts the current content off. `false` lands the birds as the new content row by row, then crossfades to the real content.
- **Empty and error states:** not in scope. The controls show progress and arrival only.
- **Accessibility:** `ReduceMotion` on each control. `FlockStreamingText` sets its automation name to the full text on `Complete()`. Its text is not selectable.
- **Runtime verification:** run the gallery with `MURMURATION_DEMO=progress|rewind|thinking|streaming|loading` to open and play one tab unattended.

## Implementation Plan

- Done: engine, host, four controls, gallery, desktop build and run.
- Next: add mobile and WebAssembly target frameworks and verify there.
- Next: decide the NuGet package shape if this ships beyond samples.

## Unresolved Questions

- Does `RenderTargetBitmap` capture behave the same on Android, iOS and WebAssembly with the Skia renderer? Only Linux desktop was run.
- The overlay is not clipped to a control or a `ScrollViewer`. Is drawing over neighbouring UI acceptable for your layouts?
- Should the library also target `net10.0-windows` (WinAppSDK), where `SKCanvasElement` is unavailable and only the fallbacks would show?
