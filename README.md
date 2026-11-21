# Screen Draw

Press a hotkey and draw on whichever monitor your mouse is on. Runs in the system tray.

| Key | Action |
| --- | --- |
| `Ctrl+Shift+D` | Start drawing on the monitor under the mouse / stop drawing (clears marks) |
| `Esc` | Stop drawing (clears marks) |
| `Ctrl+Z` | Undo last stroke |
| `C` | Clear all strokes, keep drawing |
| `Ctrl+C` | Copy a screenshot of the monitor (with drawings) to the clipboard |
| `Ctrl+Alt+Shift+Q` | **Emergency quit** — instantly kills the app, works even if it hangs |

You can also right-click the tray icon → Exit.

## Showing drawings in OBS (Browser Source)

Screen Draw serves a transparent page at `http://localhost:7878/` that mirrors your strokes live.
(Tray icon → *Copy OBS Browser Source URL* copies it.)

1. In your OBS scene, add **Sources → + → Browser**.
2. URL: `http://localhost:7878/`. Set **Width/Height to your monitor's resolution** (e.g. 1920 × 1080).
3. Put it **above** your Game/Window Capture in the source list and make both fill the canvas.
   Strokes line up 1:1 with the monitor.
4. Multiple monitors? Use `http://localhost:7878/?screen=2` to only show drawings made on display 2
   (the number from Windows *Display settings*).

The overlay can't draw on top of a game in *exclusive* fullscreen, so set the game to
borderless/windowed fullscreen.

OBS **Display Capture** also records the overlay directly, if you can use it.

## Build

```
dotnet publish -c Release -o publish
```

Produces `publish\ScreenDraw.exe` (needs the .NET 8 Desktop Runtime).

To start with Windows: press `Win+R`, type `shell:startup`, and put a shortcut to `ScreenDraw.exe` there.

Hotkeys, pen color and width are constants at the top of `Program.cs` and `OverlayForm.cs`.
