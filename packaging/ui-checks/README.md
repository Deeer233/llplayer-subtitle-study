# LLPlayer offscreen UI QA

Run in PowerShell 7.4+ on Windows with a .NET 10 SDK and Windows Desktop runtime. `-Package` accepts the unpacked package directory or its `LLPlayer.dll`. The launcher uses the workspace's portable .NET 10 SDK if available; pass `-Dotnet` to select another `dotnet.exe`.

```powershell
& 'C:\Users\Deeer\Desktop\美剧字幕翻译\packaging\ui-checks\Invoke-OffscreenUiQa.ps1' `
    -Package 'C:\Users\Deeer\Desktop\美剧字幕翻译\.pilot\polish-release-20261127-125305-144e1786\payload\LLPlayer.dll'
```

`-BuildOnly` compiles without a package. `-SelfTest` also renders a synthetic WPF visual to test the host, not the product. Each invocation writes only to its new `packaging/ui-checks/.pilot/ui-qa-*` run tree. The console prints that path. See `build.log`, `run.log` and `report.json`; exit codes are 0 for pass, 1 for failed assertions, and 2 for a probe setup/runtime error. A build or launcher error stops before the probe runs.

Package mode records the loaded DLL's path, SHA-256 and MVID. The separate STA console instantiates the packaged review window with `mutePreview=true` using an explicit synthetic `StudyCloud` root, `localOnly=true`, and `startWorker=false`. It loads theme resources without instantiating `LLPlayer.App`, and never shows a window, starts playback, opens a dialog or connects to Anki. It does not use `StudyCloud.Shared`.

| PNG | Client area (DIP) | WPF scale | Pixels |
| --- | --- | --- | --- |
| `01-regular-100-idle.png` | 640 x 730 | 100% | 640 x 730 |
| `02-small-200-idle.png` | 450 x 430 | 200% | 900 x 860 |
| `03-small-235-failure.png` | 450 x 430 | 235% | 1058 x 1011 |
| `04-small-235-running.png` | 450 x 430 | 235% | 1058 x 1011 |

`RenderTargetBitmap` captures detached client content at the requested WPF layout DPI. It does not prove behavior on a physical display, non-client chrome, a live DPI transition, or a real keyboard/mouse session. Preference sizes are checked against the host `WorkArea`; the image viewports are fixed independently.

The probe checks the review layout and scroll, small Anki and word-list tabs, idle/running/success/failure feedback, cancellation, preference round-trip, malformed/out-of-range preferences, and explicit lookup priority. Cancel uses the real confirm and cancel handlers with a synthetic audition latch, before the write stage. It verifies the fixture `state.json` is byte-identical, in-memory state is unchanged, and no new file remains. This is not an audio-decoding, mid-write cancellation, or data/service regression suite.
