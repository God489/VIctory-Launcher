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
3. **NVIDIA RTX and 32 GB of RAM**, from codec_d's [cool_fox](https://github.com/DoubleGhosty/cool_fox)
   (merged in `39335a3`; it is built on the same base): see below.
4. **The launcher** ([launcher/](launcher/)): a window for all of it.

### The fixes, and how they are known to work

The console runs most of the title's compute programs in waves of 64 lanes; NVIDIA runs
waves of 32, so each console wave runs as two halves. `emit::console_wave_tests` runs 29
cases (programs and the values the console gives, from #68 and #128) through the whole
translation, fallbacks included, and on the GPU, lane by lane. `emit::belobug_wave64_tests`
runs the 26 console wave64 tests of Belobug/VIctory-Belobug-AMD (#68, #128 and the
upstream ones beside them) the same way. On an RTX 3050:

| | `0a43566` (the base) | this |
|---|---|---|
| the 29 cases with the console's values | 8 | **29** |
| the 26 Belobug tests that pass | 10 | **22** |
| the base's own tests | 916 passed | 916 passed, + 24 new (+ cool_fox's, below) |

What was wrong, each now covered by those tests:

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
- **DPP row masks name the console's rows.** In the upper half of a console wave run as two
  host waves, `row_mask` bits 2 and 3 name its rows; the host lanes 0..31 named rows 0 and 1.
- **Whole-wave reductions combine both halves.** The section simulation followed only
  `s_mov` and `s_and` among scalar steps, so the `s_add` / `s_min` joining the two halves'
  results (read with `v_readlane` 0 and 32) stopped the program (upstream VIctory `a8bfe78`'s
  scalar steps, ported). A console wave with no upper half counts it as the operation's
  identity, not 0 (a minimum over 96 threads was 0).

Not exact yet: a loop's exit mask counted after the loop
(`console_wave64_programs_not_yet_exact`, ignored), and 4 of the Belobug tests
(`lane_writes_reach_lanes_outside_the_branch`, `loops_run_while_any_lane_of_the_console_wave_does`,
`loops_with_an_exit_mask_grown_from_zero`, `several_console_waves_loop_on_their_own`,
ignored): they need the console flow, in which every lane of a console wave runs each block
it runs and a loop runs while any lane does. This tree does not have it (the base keeps
group barriers out of branches, which lost the card in its runs); they failed on the base too.

### From cool_fox: NVIDIA RTX and 32 GB of RAM

codec_d took the same base to an RTX 5090 with 32 GB of RAM, half of what the base
recommends. Its measurements ([docs/bug-journal.md](docs/bug-journal.md)): a median of
54.3 ms a frame over 120 heavy intervals of the opening street, with no out-of-memory
error at a peak of 34.4 GB of commit, where the build before its pool back-off ran out of
memory. What it brings:

- **Memory for limited RAM:** residency and upload budgets that tell mild pressure from
  a critical commit; a GPU pool that cannot grow backs off for 500 ms and reuses what it
  has instead of failing; ordinary prefetch waits below 4 GB of free commit; idle pool
  blocks are trimmed. The launcher's **Memory profile: Limited** (Auto under 48 GB of RAM)
  sets what it runs with: `VICTORY_LEVELS_ON_DEMAND=1`, `VICTORY_CARD_BUFFERS_MIN_KB=64`,
  `VICTORY_AMBIENT_AIR=1.5` and Series S.
- **Descriptor slots freed when the GPU is done** with the work that used them: they were
  held for 16 more submissions, NVIDIA's dynamic slot pool ran out and draws were dropped.
- **`VICTORY_INDIRECT_REST` off by default:** an indirect record too large for what is left
  of its list's budget takes nothing, so the draws after it still run. The base's own
  findings named that record as the suspect for the outdoor flicker and this as the
  experiment; two of the base's tests now check both modes (`d55e50a`).
- **Series S:** the two-plane black screen (the retail plane read at its 72-byte stride).
- **Wave64 on NVIDIA:** wave-wide branches by default for two RTX-validated scene kernels
  and for loops that run while any lane of the console wave does; a select on a mask
  operation's SCC reads the whole console wave's; a program left needing 64-lane waves on
  a 32-lane card is refused at translation. Our exact path (above) is unchanged: its 29
  cases and 22 Belobug tests give the same results with the merge.
- XMA audio fixes, movie-plane uploads spread over frames, fence waits woken by
  `WaitOnAddress`, d16 typed integer loads, the write guard of join edges no lane reaches,
  and a 256 MiB DirectStorage window.

With the merge, on an RTX 3050: 950 tests passed (10 of them cool_fox's), 50 ignored, and
2 failed: the two indirect budget tests above, which pass after `d55e50a`.

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
  performance panel (F3), the pipeline book, console model, memory profile, and any other
  `VICTORY_*`.
- **Compatibility:** how many of your game's shaders translate for your GPU
  (`victory translate`).
- **Logs:** live, filtered and saved per session; `crashes.log` one click away.

## Where it stands

From the base's measurements (one PC: i9-12900K, 64 GB, RX 7900 XTX, 1440p): menus 30 fps;
the prologue indoors 13-20 fps; the first boat mission 19-22 fps; a street chase 6-10 fps;
40-46 GB of private memory, so **64 GB of RAM is recommended**. On an RTX 5090 with 32 GB
(cool_fox's measurements, its Limited profile): 54 ms a frame in the opening street,
34 GB at the peak. The fixes above make the console's wave64 programs give the console's
values on NVIDIA.

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
- **codec_d**: [cool_fox](https://github.com/DoubleGhosty/cool_fox), the NVIDIA RTX and
  32 GB RAM work merged here, and its bug journal.
- **TheHoracio**: the scalars after divergent loops and branches (upstream `b67d15d`).
- **God489**: the fixes of `e630b18` and the launcher. Made with
  [Claude Code](https://claude.com/claude-code).
- **Microsoft Edge WebView2** draws the launcher's window.

## License

GPL-3.0-or-later, as VIctory. See [LICENSE](LICENSE); the one file that is not covered is
named in [docs/asset-policy.md](docs/asset-policy.md).
