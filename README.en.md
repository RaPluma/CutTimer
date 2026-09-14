<div align="center">

# CutTimer

**Per-cut working time for 2D animation key animators**

[中文](README.md) | English

<img src="https://img.shields.io/github/v/release/RaPluma/CutTimer?style=flat-square" alt="Version">
&nbsp;
<img src="https://img.shields.io/github/stars/RaPluma/CutTimer?style=flat-square" alt="Stars">
&nbsp;
<img src="https://img.shields.io/github/downloads/RaPluma/CutTimer/total?style=flat-square" alt="Downloads">
&nbsp;
<img src="https://img.shields.io/badge/license-MIT-blue?style=flat-square" alt="License">
&nbsp;
<img src="https://img.shields.io/badge/platform-Windows%2010%2B-0078D4?style=flat-square" alt="Platform">

<img src="docs/screenshots/main-timer.png" alt="CutTimer main window" width="820">

</div>

## What it is

CLIP STUDIO PAINT already tracks how long a canvas has been worked on, and its
idle detection is good. But two things make that number useless for a key
animator:

1. **You can only see it one file at a time** — there is no roll-up.
2. **It counts everyone.** A `.clip` travels through
   key animation → animation director → 2nd key → in-between check,
   so reading it directly means counting your upstream's hours as your own.

CutTimer fixes #2 by **differential accounting**: it records the value the first
time it sees a cut as a baseline, then only accumulates deltas. The upstream
contribution cancels out as a constant offset.

```
first seen:  CanvasWorkTime = 85:46:34   -> stored as baseline
1h later:    CanvasWorkTime = 86:02:27   -> only 15:53 counts as yours
```

There is **nothing to start or stop**. CSP writes work time to disk only when you
save, so the app watches for save events — press `Ctrl+S` and it settles the
current cut and switches to it.

## Features

| | |
|---|---|
| **Timer** | Current cut stopwatch + stand-up countdown (hand-drawn ring). Pause and reset |
| **Stats** | Daily bar chart, plus today / 7-day / 30-day totals |
| **Floating mini window** | Always on top, mutually exclusive with the main window, draggable and resizable, with the countdown badge in the corner |
| **Stand-up reminder** | Windows toast + looping chime that keeps ringing until you deal with it. Interval is any value from 5 to 600 minutes |
| **Automatic registration** | Save once and the cut registers itself |
| **Workspace discovery** | Follows CSP's "last used folder", plus an optional manual disk scan |

<div align="center">
<img src="docs/screenshots/mini-window.png" alt="Floating mini window" width="260">
&nbsp;&nbsp;
<img src="docs/screenshots/stats.png" alt="Daily stats" width="420">
</div>

## Getting started

Download from [Releases](https://github.com/RaPluma/CutTimer/releases):

| File | Size | Notes |
|---|---|---|
| `CutTimer-1.0.0-win-x64.zip` | 63 MB | **Extract and run.** No runtime needed |
| `CutTimer-1.0.0-win-x64-lite.zip` | 28 MB | Needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) and the [Windows App SDK Runtime](https://aka.ms/windowsappsdk/1.7/latest/windowsappruntimeinstall-x64.exe) |

Unzip and run `CutTimer.exe`. It is an **unpackaged** app — there is no installer.

> **Requires** Windows 10 1809 (17763) or newer, 64-bit.

**First run**: open a cut in CSP, make a stroke or two, and press `Ctrl+S`.
The cut appears in the list and the timer starts.

To check the reminder works, set the interval to 1 minute and wait — or press
"Reset" on the reminder card.

## Known limitations

- **Registration happens on *save*, not on *open*.** CSP holds no file handle for
  the open document, exposes no document API, and puts no filename in its window
  title — so there is genuinely no way for an external process to know which cut
  you are on ([all six approaches were tried](docs/detection.md)).
  Just press `Ctrl+S` once after opening a new cut.
- **Work spanning midnight is counted on the day the save happened.**
- **No disk scan at startup.** To find existing project folders, open Settings and
  press "Scan disk…" — it shows a progress bar and live counters.

The UI uses **Sarasa Gothic**. Without it the app falls back to a system font
(no missing-glyph boxes), but it looks better with it installed.

## Data

```
%LOCALAPPDATA%\CutTimer\
├─ state.json   current state (written atomically)
└─ events.jsonl settlement ledger (append-only, never rewritten)
```

`events.jsonl` is plain text, one settlement per line, **readable without this app**:

```jsonl
{"t":"2026-01-15T14:30:00+08:00","cut":"CUT_012","path":"...","delta":950000,"total":309117415,"mine":322837}
```

`tail` it, import it into Excel, or script your own daily totals.
Because it is append-only, **"my time" can be recomputed from the ledger even if
`state.json` gets corrupted**.

## Building

```sh
git clone https://github.com/RaPluma/CutTimer
cd CutTimer
dotnet publish -c Release -r win-x64 --self-contained true -o release
```

Requires .NET SDK 10. WinUI 3 + Windows App SDK, unpackaged.

More detail lives in **[docs/](docs/)** (Chinese):

- **[How it detects the current cut](docs/detection.md)** — the six dead ends, the
  `.clip` container format, and why differential accounting is necessary
- **[Implementation notes](docs/implementation.md)** — storage trade-offs,
  WinUI 3 pitfalls, fonts and assets

<div align="center">
<br>
<strong>If this helped you, consider giving it a Star ⭐</strong><br><br>
<a href="https://github.com/RaPluma/CutTimer/issues">Report an issue</a> ·
<a href="https://github.com/RaPluma/CutTimer/releases">Releases</a>
</div>
