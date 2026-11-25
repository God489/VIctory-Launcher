# VIctory Launcher

A Windows launcher, and the VIctory build inside it, for running the Xbox Series X|S
release of Grand Theft Auto VI natively on Windows from your own copy of the game.

**Download:** `VIctoryLauncher.exe` on the [Releases](https://github.com/God489/VIctory-Launcher/releases)
page: one file, nothing to install.

**This repository contains no game files, code or assets.** You need your own copy of
the game. It is not affiliated with or endorsed by the game's developer or publisher, or
by Microsoft. **Not playable yet** on most machines: see "Where it stands".

## What is in it

1. **The fastest VIctory fork, as its base:** [avatarmanz/VIctory-7900XTX](https://github.com/avatarmanz/VIctory-7900XTX)
   (`0a43566`), with its whole history. On an RX 7900 XTX it went from 280 ms a frame to
   46-65 ms in the same scenes of the prologue (13-22 fps indoors and on the water), by
   doing on threads of its own, on the card, and later what the title's threads waited for.
   Its README and findings: [docs/README-7900XTX.md](docs/README-7900XTX.md),
   [docs/findings.md](docs/findings.md).
2. **Console wave64 made exact on 32-lane GPUs (NVIDIA)**, `e630b18`: the fixes below,
   from the work of [Belobug/VIctory-Belobug-AMD #68 and #128](https://github.com/Belobug/VIctory-Belobug-AMD/pull/128).
3. **The launcher** ([launcher/](launcher/)): a window for all of it.

### The fixes, and how they are known to work

The console runs most of the title's compute programs in waves of 64 lanes; NVIDIA runs
waves of 32, so each console wave runs as two halves. `emit::console_wave_tests` runs 29
cases (programs and the values the console gives, from #68 and #128) through the whole
translation, fallbacks included, and on the GPU, lane by lane. On an RTX 3050:

| | `0a43566` (the base) | this |
|---|---|---|
| cases with the console's values | 8 of 29 | **29 of 29** |
| the base's own tests | 916 passed | 916 passed, + 2 new |

What was wrong, each now covered by those cases:

- **`ds_bpermute_b32` / `ds_permute_b32`.** AMD's RDNA ISA (10.4.4) runs them within each
  32-lane half of a wave64, "as if were two independent wave32's". `ds_bpermute` read
  across halves on 64-lane hosts and was refused on NVIDIA: the title's checkerboard
  shadow-mask generators, without which every shadowed light was multiplied by an empty
  mask. `ds_permute` was not translated on any host. Both now read within the half, and a
  lane that does not run the instruction gives 0; their programs split into two waves of
  32 on NVIDIA.
- **Groups that are not a multiple of 32 threads** (48, 80, 112): the one short host wave
  could take the group's first console threads and leave the rest of that run to no
  thread (a group of 80 counted 24 lanes of 40). It takes the group's last run now.
- **Scalars after a divergent loop or branch.** The console runs a loop until its last lane
  leaves, and each path some lane takes, with one set of scalar registers. Each lane read
  its own path's values (1, 2, 3 ... where the console gives 64). Now every lane reads what
  the wave's last iteration or path left (upstream VIctory `b67d15d`, TheHoracio, which the
  7900XTX line never had), combined over both halves on NVIDIA.
- **No group barrier inside a divergent loop.** The blocks of a loop that lanes leave at
  different times were counted as blocks every lane runs (a block post-dominates itself),
  so an exchange between the halves could put a barrier there.
- **A mask operation's SCC read as a number** (`WaveActiveAnyTrue` choosing a value) is the
  console wave's; a mask grown inside a branch translates on 64-lane GPUs. Waterfall loops
  are unchanged (no wave operations).

Not exact yet: a loop's exit mask counted after the loop
(`console_wave64_programs_not_yet_exact`, ignored).

### The launcher

- **Play:** drop the game folder on the window, or browse to it. It reads
  `MicrosoftGame.config`, checks for `appdata0.rpf`, and runs `victory run <exe> --execute`
  with the settings the base is played and measured with (its `scripts/play.ps1`): Fast,
  or Safe. Time, fps and frame time live from VIctory's caption; Stop ends every process
  VIctory started (a job object).
- **Before the first run:** Prepare shaders (`victory shaders`) and Warm pipelines
  (`victory warm --used`).
- **This PC:** AVX2, the GPU VIctory will use with its D3D12 wave lanes, shader model and
  mesh shader tier, RAM and free commit.
- **Settings:** each one names the variable it sets: mode, ray-traced light
  (`VICTORY_BVH_BUILDER`), the light estimate (`VICTORY_AMBIENT`), texture read-back, the
  performance panel (F3), the pipeline book, console model, and any other `VICTORY_*`.
- **Compatibility:** how many of your game's shaders translate for your GPU
  (`victory translate`).
- **Logs:** live, filtered and saved per session; `crashes.log` one click away.

## Where it stands

From the base's measurements (one PC: i9-12900K, 64 GB, RX 7900 XTX, 1440p): menus 30 fps;
the prologue indoors 13-20 fps; the first boat mission 19-22 fps; a street chase 6-10 fps;
40-46 GB of private memory, so **64 GB of RAM is recommended**. NVIDIA cards had not been
measured in the world with this; the fixes above make the console's wave64 programs give
the console's values there.

## Requirements

Windows 10/11 x64, a CPU with AVX2, a Direct3D 12 GPU with shader model 6.6 and mesh
shaders, the Microsoft Edge WebView2 Runtime (part of Windows 11), your own dump of the
title, and memory as above.

## Build

```powershell
cargo build --locked --profile diag -p victory --features diagnostics   # target\diag\victory.exe
launcher\build.ps1                                                     # launcher\build\VIctoryLauncher.exe
cargo test --workspace --no-fail-fast                                  # no game data needed
```

The launcher compiles with the .NET Framework 4.8 C# compiler that ships with Windows.
Its build fetches Microsoft's WebView2 package (1.0.4370.26) from nuget.org once and checks
its DLLs against pinned SHA-256 hashes: the repository holds no binaries
([docs/asset-policy.md](docs/asset-policy.md)).

## Credits

- **[VIctory](https://github.com/Marvisak/VIctory)** by Marvisak and its contributors.
- **[avatarmanz/VIctory-7900XTX](https://github.com/avatarmanz/VIctory-7900XTX)**: the base,
  and the speed. Its credits, kept in its history and README:
  [fxceless/victory-fork](https://github.com/fxceless/victory-fork),
  [DexNom/VIctory](https://github.com/DexNom/VIctory),
  [Belobug/VIctory-Belobug-AMD](https://github.com/Belobug/VIctory-Belobug-AMD) and
  romainhedouin, [FQY55kai/ZJOU-VICTORY](https://github.com/FQY55kai/ZJOU-VICTORY),
  [myokex/victory-amd](https://github.com/myokex/victory-amd) (with Spexivus, joroyt,
  tightfall and Joshua Odom), Geckomacabre and cxy0072019-sketch.
- **TheHoracio**: the scalars after divergent loops and branches (upstream `b67d15d`).
- **God489**: the fixes of `e630b18` and the launcher. Made with
  [Claude Code](https://claude.com/claude-code).
- **Microsoft Edge WebView2** draws the launcher's window.

## License

GPL-3.0-or-later, as VIctory. See [LICENSE](LICENSE); the one file that is not covered is
named in [docs/asset-policy.md](docs/asset-policy.md).
