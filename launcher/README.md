# VIctory Launcher

A Windows launcher for [VIctory](https://github.com/Marvisak/VIctory), which runs the
Xbox Series X|S release of Grand Theft Auto VI natively on Windows from files you supply.

It carries a diagnostics build of this repository: [avatarmanz/VIctory-7900XTX](https://github.com/avatarmanz/VIctory-7900XTX),
the fastest fork, with console wave64 made exact on 32-lane GPUs (NVIDIA) on top (see the
[README](../README.md) of the repository).

**No game files, code or assets are included.** You need your own copy of the game.
Not affiliated with or endorsed by the game's developer or publisher, or by Microsoft.
**Not playable yet** on most machines: 13 to 22 fps in parts of the world on an RX 7900 XTX
with 64 GB of RAM, less elsewhere.

## What it does

- **One file.** `VIctoryLauncher.exe` carries its interface, WebView2's loader and
  `victory.exe`; it unpacks them once to `%LOCALAPPDATA%\VIctory Launcher\runtime`.
- **Play.** Drop your game folder on the window (or browse to it). The launcher reads
  `MicrosoftGame.config`, finds the executable, checks for `appdata0.rpf`, and runs
  `victory run <exe> --execute --console series-x|series-s` with the settings the base is
  played and measured with (its `scripts/play.ps1`): Fast, or Safe. While the game runs it
  shows the time, frames per second and frame time from VIctory's window caption, and
  Stop ends VIctory and every process it started (a job object, so a relaunch is covered too).
- **Before the first run.** Prepare shaders (`victory shaders <game>`: every shader
  translated into the cache) and Warm pipelines (`victory warm --used`: the pipelines
  earlier runs drew with, built ahead for the driver's cache).
- **Verify.** Runs `victory run <exe>` without starting the title: the build VIctory
  recognises, the console model, the title DLLs and the imports VIctory still lacks.
- **This PC.** AVX2, the GPU VIctory will use (DXGI's high-performance adapter) with its
  D3D12 wave lanes, shader model and mesh shader tier, RAM and free commit (64 GB of RAM
  recommended: the world takes 40-46 GB).
- **Settings.** Every option names what it sets: `--console`, Fast or Safe
  (`VICTORY_THIN`, `VICTORY_JOIN_PARTS`, `VICTORY_UPLOAD_AHEAD` or `VICTORY_UNDER_WIDE`,
  `VICTORY_LAND_ECHO`), `VICTORY_BVH_BUILDER`, `VICTORY_AMBIENT`, `VICTORY_TEXTURE_BACK`,
  `VICTORY_PANEL`, `VICTORY_PIPELINE_BOOK`, `--unimplemented`, `--strict`, title arguments,
  any other `VICTORY_*`, `VICTORY_DATA_DIR`, `VICTORY_CACHE_DIR`, `VICTORY_LOG`; and
  clearing the shader cache.
- **Compatibility.** Runs `victory translate <game> [--validate]` for your GPU's wave size
  (`VICTORY_HOST_WAVE`) and shows how many compute, vertex/pixel and mesh programs
  translate and validate, with the reasons for the rest.
- **Logs.** VIctory's output live, filtered, copied or saved; every session is also kept
  in `%LOCALAPPDATA%\VIctory Launcher\logs`. Links to the data folder and `crashes.log`.

## Build

Windows 10/11 x64, PowerShell, and the diagnostics `victory.exe` of this repository (the
build the base is played and measured with). The launcher compiles with the .NET
Framework 4.8 C# compiler that ships with Windows; no SDK is needed.

```powershell
cargo build --locked --profile diag -p victory --features diagnostics
launcher\build.ps1          # -> launcher\build\VIctoryLauncher.exe
```

The build fetches the Microsoft.Web.WebView2 1.0.4370.26 package from nuget.org once, into
`launcher\vendor\webview2` (not in the repository), and checks
`Microsoft.Web.WebView2.Core.dll`, `Microsoft.Web.WebView2.WinForms.dll` and the x64
`WebView2Loader.dll` against pinned SHA-256 hashes; Microsoft's license goes into the exe
beside them. The window needs the Microsoft Edge WebView2 Runtime, which Windows 11
includes.

For testing, `VLAUNCHER_DEBUG_PORT=<port>` opens the page to DevTools and
`VLAUNCHER_DEBUG=1` enables its context menu and keys.

## License

GPL-3.0-or-later, as VIctory. See [LICENSE](../LICENSE).
