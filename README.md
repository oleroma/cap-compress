# cap-compress

A small portable Windows app that shrinks a video to fit a size cap, such as
Discord's 10 MB upload limit. Pick the target size and the app works out
everything else with ffmpeg.

- **Target size** is the main slider. Its sizes stop just below the source
  size. Its last stop, **No limit**, drops the size target: resolution and frame
  rate can go up to the original, and it encodes at constant quality (x264 CRF
  20, NVENC CQ 23), so the file is whatever size that gives, even bigger than
  the original.
- **Trim** is one slider with two handles. Less video means more bitrate for
  the part you keep. Arrow keys move a handle by 0.1 s; hold Shift to move 1 s
  at a time, and press or release it mid-hold to change speed.
- **Resolution** and **Frame rate** (each Auto or manual; frame rate starts at
  a manual 24 fps) are capped by the bitrate the target allows, so you can't
  pick a combination that wouldn't fit. Lowering one raises the limit of the other.
- **Encoder**: x264 (two-pass, best quality and size accuracy) or NVENC (much
  faster, NVIDIA GPUs). NVENC is offered only when a one-frame test encode
  works at startup, so PCs without an NVIDIA card don't see it.
- **Remove audio** gives the audio's share to the video.
- **Mix all tracks** shows up for videos with several audio tracks (such as
  OBS recordings with the mic on its own track) and mixes them into one. Turn
  it off to keep only the first track.
- **HDR** videos (phone clips in HLG or HDR10) are tone-mapped to normal SDR,
  so they don't come out grey and washed out.

Opening a video doesn't freeze the window, and closing it during an encode
asks first.

The output is saved next to the original as `name_compressed.mp4`. If an encode
comes out over the target, it retries at a lower bitrate; nothing is saved if it
can't fit.

The app remembers your last settings between restarts: target size, Auto or
manual resolution and frame rate, encoder, Remove audio, Mix all tracks, and
the window's size and place. Trim starts over for each video. They're kept in
`%LOCALAPPDATA%\CapCompress\settings.txt`; delete it to go back to the defaults.

## ffmpeg

The app looks for `ffmpeg.exe` and `ffprobe.exe` next to itself, in an `ffmpeg`
folder beside it, on `PATH`, and in winget's links folder. If none is found, it
offers to download ffmpeg (about 100 MB, from gyan.dev) into an `ffmpeg` folder
next to the app.

## Building

```bat
build.cmd
```

This builds `build\cap-compress.exe` with the C# compiler that ships with
Windows (.NET Framework 4.x), so nothing needs installing.

## Tests

The tests run in the background and never take focus or send input to the
desktop.

- `tests\EngineTest.cs` (compile with `src\Engine.cs`): size planning, and with
  `<video> <out dir>`, real encodes with x264, NVENC, trim, mute and cancel.
- `tests\UiTest.cs` (compile with all of `src\`, `/main:CapCompress.UiTest`):
  drives the real window in-process, `<video> <screenshot dir>`. Its windows
  open off-screen without taking focus, and it keeps its settings in
  `<screenshot dir>\data`, so yours are never touched.
