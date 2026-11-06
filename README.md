# Colonist Autonomy

A RimWorld 1.7 mod where colonists ask a local decision model what to do. The default model is
[Cloudflare Clef](https://huggingface.co/Cloudflare/clef) (GGUF, `llama-server`). The current
client targets llama-server's `/v1/systemone` decision API; another provider needs an adapter.
The model does not generate text: it receives a colonist's state and a closed set of options,
and returns a choice with probabilities via `POST /v1/systemone`.

**Built with:** [rimworld-rs](https://github.com/KrzysztofZawisla/rimworld-rs)

## Source code

This repository contains the full mod source: the Rust bridge in `src/`, the C# companion in
`companion/`, the XML defs in `mod/`, and the Affinity sources for the Workshop images in
`workshop/source/`.

The native bridge and build tooling come from
[rimworld-rs](https://github.com/KrzysztofZawisla/rimworld-rs). That checkout provides the
`cargo xtask build/install/package` commands and the `game-refs/` setup used by this repository.

## How it works

- **Rust** — settings, HTTP thread, decision cache, scheduling, and answer selection.
  No request blocks the game thread: HTTP runs on a separate thread and the result is applied
  in `on_tick`. A single request serves the whole colony: the state contains every colonist,
  and questions are keyed as `p<id>_action` / `p<id>_intent`. The HTTP client shares one
  `ureq::Agent` (keep-alive) and rebuilds it only when the URL or timeout changes.
- **C# companion** — a `ClefThinkNode` hooked into vanilla `Humanlike_PostMain` (that is,
  after normal work/needs and before idling), state and candidate building, and creating
  `Job`s through vanilla `JobGiver`/`WorkGiver`.
- **Harmony bridge** — the ThinkTree is plain XML, but the bridge uses Harmony for game events;
  no custom ThinkTree patch is needed.

## Requirements

- RimWorld 1.7.4995 on Windows x64, Linux x64 or macOS (x64 / Apple Silicon).
- A working GPU backend: Vulkan on Windows/Linux (the installer fetches a Vulkan llama.cpp
  build), Metal on macOS.
- `llama-server` from llama.cpp (a build with `/v1/systemone` support) and a Clef GGUF model:
  `Clef-Flash-Q8_0.gguf` (9B, recommended) or `Clef-Q4_K_M.gguf` (27B, optional).
- To build: Rust nightly, .NET SDK 9, and a
  [rimworld-rs](https://github.com/KrzysztofZawisla/rimworld-rs) checkout with `game-refs/`
  (`cargo xtask setup`). Cross-platform builds additionally use `cargo-zigbuild` (Linux/macOS)
  and `cargo-xwin` (Windows), as described by the toolkit.
- Use the `rimworld-rs` line that still has `SettingSpec::action`/`label` (`master` or
  `feature/action-buttons`). The installer draws real settings buttons and a live status label;
  the newer `chore/logic-replaced-by-libraries` branch removed those widgets.

## Model

Example launch (replace the paths). Windows:

```powershell
<llama.cpp>\llama-server.exe -m <models>\Clef-Flash-Q8_0.gguf `
  --host 127.0.0.1 --port 8081 -ngl 99 -c 16384 -b 8192 -ub 8192
```

Linux and macOS:

```bash
<llama.cpp>/llama-server -m <models>/Clef-Flash-Q8_0.gguf \
  --host 127.0.0.1 --port 8081 -ngl 99 -c 16384 -b 8192 -ub 8192
```

The server is ready when `http://127.0.0.1:8081/health` returns 200.

### Server auto-start

When **Auto-start llama-server** is enabled, the mod can start the server at startup or when a
colony loads. It first checks `GET /health`:

- **200** — does nothing; this may be a manually started server.
- **no response** — starts `llama-server` from settings, but only for `127.0.0.1`/`localhost`.
- the process is hidden on Windows (no console window), and its stdout/stderr goes to
  `llama-server.log` in the managed install root (see below).
- the mod **stops the auto-started server** after returning to the main menu and when exiting
  the game. It does not touch a server started manually (or earlier, outside the mod).
- when the URL/port changes while the auto-started process is still running, restart the game
  or kill the process.

Auto-start is **off by default**. Enable it in settings after reviewing the executable and model
paths; the server can also be started manually as shown above.

### Dependency installer

The mod settings page has six action buttons and a status line:

- **Install / check dependencies** — checks the paths and fetches missing items one by one.
- **Install llama.cpp (Vulkan)** on Windows and Linux, **Install llama.cpp (Metal)** on macOS —
  downloads the pinned release
  [`b12069`](https://github.com/ggml-org/llama.cpp/releases/tag/b12069), verifies its size and
  SHA256, extracts it to `<install root>/llama.cpp`, and sets `llama-server executable`.
- **Download Clef Flash Q8 (9.7 GB) — recommended** — the faster 9B model, the default choice:
  [`Cloudflare_clef-flash-Q8_0.gguf`](https://huggingface.co/bartowski/Cloudflare_clef-flash-GGUF)
  (9,675,741,344 B, SHA256
  `55c222dfc9f5760d06015ee6d6452ea45113bb6f333178c345c489dbc134bcfe`), Apache-2.0.
- **Download Clef Q4 (17.6 GB) — optional** — the full 27B model for higher quality:
  [`Cloudflare_clef-Q4_K_M.gguf`](https://huggingface.co/bartowski/Cloudflare_clef-GGUF)
  (17,652,432,960 B, SHA256
  `f1e2cc7f36893b52f773cc01f0ed351abab17c510502423bc4d2f5782a93cebb`), Apache-2.0.
  Both models land in `<install root>/models`; the download sets the selected variant as
  `server_model`. A fresh install points at the Flash model.
- **Cancel dependency download** — stops the active download; the `.part` file remains and the
  next click resumes the transfer.

The managed install root is per user and per platform (`COLONIST_AUTONOMY_HOME` overrides it,
which is useful for tests and portable installs):

| Platform | Install root |
|---|---|
| Windows | `%LOCALAPPDATA%\ColonistAutonomy` |
| Linux | `$XDG_DATA_HOME/ColonistAutonomy`, otherwise `~/.local/share/ColonistAutonomy` |
| macOS | `~/Library/Application Support/ColonistAutonomy` |

The Linux and Windows builds use the Vulkan llama.cpp package; macOS uses the Metal build.
Each package is downloaded from the release above and verified before use:

| Platform | Archive | Bytes | SHA256 |
|---|---|---|---|
| Windows x64 | `llama-b12069-bin-win-vulkan-x64.zip` | 37,554,286 | `e1b1c4e5481fe88e3f49f97908b53bb7699ad8aa3f6332a58f944e7a8f068b33` |
| Linux x64 | `llama-b12069-bin-ubuntu-vulkan-x64.tar.gz` | 35,740,211 | `8001b930cc5e7cb500193dbdbea762a7f808ad2996ae12440e7af5020c94a4da` |
| macOS arm64 | `llama-b12069-bin-macos-arm64.tar.gz` | 16,548,295 | `91023146fab641e15d429c84002bf2e614a7a127992eedb443872fb23ba69439` |
| macOS x64 | `llama-b12069-bin-macos-x64.tar.gz` | 12,287,221 | `7f1e6a7ce371c582ad8a9d9abed7d0f96a2a8060d4679109897cb3b98c181620` |

If `server_exe`/`server_model` already point to an existing file, the installer uses it; known
managed models are re-verified by SHA256 before use. Custom paths are user-selected and are not
hash-verified. Downloads resume after interruption (the `.part` file remains); the game must stay
open until the transfer finishes, and closing the game aborts it. The installer supports Windows
x64, Linux x64 and macOS (x64 / Apple Silicon).

### First start

If the llama.cpp server or the model is missing and the server is not responding, the mod shows
a one-time dialog explaining where to open the installer. Auto-start is skipped in that case
(without a log error) instead of trying to launch a nonexistent file. After installing, enable
Auto-start if desired, then restart the game or load a colony — the server starts with the new
paths.

## Build and install

Build with the `rimworld-rs` toolkit from its checkout; `--path` points at this repository. One
command builds the Rust library, the bridge, the XML assets and the companion assembly:

```bash
cd ../rimworld-rs
cargo xtask build   --path ../rimworld-colonist-autonomy   # build/ColonistAutonomy
cargo xtask install --path ../rimworld-colonist-autonomy   # <game>/Mods/ColonistAutonomy
cargo xtask package --path ../rimworld-colonist-autonomy   # dist/ColonistAutonomy-0.1.0.zip
```

The toolkit finds the game itself; set `RIMWORLD_DIR` to the game folder when it cannot.

Add `--all-targets` to build the native library for every platform:

```bash
cargo xtask package --path ../rimworld-colonist-autonomy --all-targets
```

Each target lands in `Rust/<rid>/` inside the mod, so one mod folder (and one Workshop item)
works on all three systems. The cross builds need the matching Rust targets
(`rustup target add ...`), `cargo-zigbuild` for Linux/macOS and `cargo-xwin` for the MSVC
Windows target, exactly as documented by `rimworld-rs`.

## Enabling

Enable **Colonist Autonomy** in the mod list. For testing, you can also launch the game with
`-mod=snowdrop.colonistautonomy`.

**Harmony** is required for the game events used by server auto-start/auto-stop
(`game_started`/`game_loaded`). Without Harmony, the ThinkTree and installer still work, but
the bridge disables those events and auto-start will not run. Quicktests had Harmony loaded.

## Settings

| Setting | Default | Meaning |
|---|---|---|
| Enabled | true | Disables the whole mod (including the ThinkTree node). |
| Decision mode | Action (model picks target) | One of the four modes described below; the options window explains the selected one. |
| Model server URL | `http://127.0.0.1:8081` | Address of `llama-server`. |
| Server status / Test server | not tested | The probe runs on a worker thread; the label shows the result without blocking the game. |
| Auto-start llama-server | false | When enabled, starts the server when entering a colony if `/health` does not respond. Off by default so the local server is not launched without an explicit opt-in. |
| llama-server executable | `<install root>/llama.cpp/llama-server` (`llama-server.exe` on Windows) | Path to the server binary (auto-start/installer). |
| Model file (.gguf) | `<install root>/models/Cloudflare_clef-flash-Q8_0.gguf` | Model passed as `-m` (auto-start/installer); the Q4 model can be selected instead. |
| llama-server extra args | `-ngl 99 -c 16384 -b 8192 -ub 8192` | Extra arguments; the mod adds `-m`, `--host`, and `--port` itself. |
| Refresh seconds | 30 | How often decisions are refreshed. |
| Base confidence | 0.40 | The base for the per-action thresholds: work is relative to the question size (`max(0.20, 2.5 / options)`), capped at the old work baseline (`base - 0.10`), social 0.40, needs 0.35, threat 0.20, idle 0.50 (0.60 when alternatives exist), interrupt 0.60. At 16 options work is 0.20, at 8 it is 0.30, and at 6 or fewer it is 0.30. The decision panel shows the threshold and option count used for each decision. |
| Interrupt current work | true | Whether an active refresh may interrupt current work. Interrupts also need the interrupt confidence and a job at least 5 seconds old; important jobs stay protected. |
| Respect sleep schedule | true | Refuses a work job when the estimated travel plus work would not fit before the colonist's next Sleep block. Player-forced jobs, drafted/mental pawns, and firefighting, patient, and doctor work are never refused. The estimate is conservative (straight-line distance, 1.35x for obstacles) and does not reserve anything. A refused pawn is picked up by the `Humanlike_PreMain` fallback node so it can still choose a local action. |
| Dry run | false | The model is asked and decisions are logged, but no jobs are issued. |
| Debug log | false | Extra logs: candidates, questions, state, and `stale`/`busy` reasons. |
| Last decision / Show recent decisions | — | A live one-line summary and a dialog with the last 20 decisions: choice, confidence, threshold, outcome, and the idle reason. |

### Decision modes

The **Decision mode** dropdown in the mod options offers:

- **Intent (game picks target)** — a single `intent` question: food/sleep/joy, drugs allowed by
  the pawn's policy, work type (`work:Hauling`, `work:PlantCutting`, ...), social, flee, idle.
  Vanilla picks the concrete target.
- **Action (model picks target)** — the default. One flat list of concrete actions:
  `work:PlantsCut:<id>`, `drug:<id>`, `social:<id>`, `eat`, `sleep`, `joy`, `flee`, `hide`,
  `idle`. For work/social/flee the model also picks the target; for needs and drugs, vanilla
  picks the target (best meal/bed/joy/drug).
- **Intent + target (two steps)** — one request with several questions: `intent` +
  `work_target`/`social_target`/`threat_target`. The model compares targets only within the
  chosen intent.
- **Hybrid (auto)** — uses **Intent** when the companion found no concrete target and
  **Action** when it did. This keeps requests small in an empty colony and precise when there
  is work to pick.

Below the dropdown, the **Mode details** line describes the selected mode. The decision panel
shows the threshold that was applied to each entry.

### Decision quality

- Work candidates are expanded beyond the first scanner result: up to 4 things and 4 cells per
  WorkGiver, up to 8 WorkGivers per type, and a fallback scan of the map or a 16-cell radius
  when a scanner exposes no global list. If no concrete target is found, the top work types are
  offered as `work:<type>` so vanilla can pick the target.
- With **Respect sleep schedule** on, concrete work whose travel plus estimated work does not fit
  before the next Sleep block is removed from the candidate list. The same estimate backs a
  Harmony prefix on `Pawn_JobTracker.StartJob`, so the job never starts and takes no reservations.
  A refused pawn is marked as gated and `ClefGatedThinkNode` (inserted at `Humanlike_PreMain`)
  asks the model for a local action instead of leaving the colonist idle.
- Work descriptions append `enemy Nm` when the nearest hostile is within 15 cells of the target,
  so the model can avoid hauling or building next to a raid without a hard rule.
- Impossible needs are filtered: `eat` requires meals, edible nutrition, or food in the pawn's
  inventory; `joy` requires a joy source on the map or enjoyable weather. Sleep stays available
  because vanilla can sleep on the ground. Drugs are offered only when the pawn's drug policy
  allows them for joy and the mood/joy thresholds match.
- The colony block now carries `bills`, `growing_zones`, `stockpiles`, `construction`,
  `designations`, `hostiles`, `research`, and `research_progress`, so the model can favour a
  real backlog instead of guessing.
- Work confidence is judged against the number of options: a 16-option question needs 0.20,
  an 8-option question needs 0.30, and a short question stays at the old work baseline. This
  keeps a long flat list from being punished just because the probability mass is spread.
- Every pawn gets a 5-second decision cooldown, a job younger than 5 seconds is not
  interrupted, and two pawns cannot claim the same concrete work or social target in one batch.
- The state includes a `history` object per pawn: last choice, confidence, outcome,
  `idle_streak`, and ticks since. The model is told to avoid repeating failed actions.

## Signals passed to the model

Besides the basic state (work, needs, skills, relations), every pawn receives `signals`:

- **Urgency:** `food`/`rest`/`joy` categories (`Starving`, `Tired`, `Low`, ...), `mood`, and
  break thresholds (`break.minor/major/extreme`).
- **Health:** `pain`, `consciousness`, `manipulation`, `moving`, `sight`, `tendable`.
- **Threat:** `count`, `nearest` (m), `dir`, `armed`, `armor`, `cover`, `near`, `danger`.
- **Time:** `hour`, `assignment` (`Work`/`Joy`/`Sleep`, ...), `night_in`, `sleep` (scheduled
  sleep).
- **Environment:** `temp`, `light`, `indoors`, `room_temp`, `beauty`, `impressive`, `clean`.
- **Contact:** relations (`spouse`, `lover`, `fiance`, `ex-*`, `rival`, `friend`, `neutral`),
  opinion, and the `CanInteractNowWith` filter.

At the `colony` level: `colonists`, `idle`, `meals`, `medicine`, `steel`, `components`,
`silver`, `wood`, `food_nutrition`, `power_stored`, `power_gain`, `bills`, `growing_zones`,
`stockpiles`, `construction`, `designations`, `hostiles`, `research`, `research_progress`.

Option descriptions carry the "why", not just the label:

```text
work:HaulGeneral:123  -> "haul 75 steel (12m, deteriorating, enemy 8m, prio 2, Hauling 3)"
eat                   -> "Eat a meal (food: Starving, colony meals: 2)"
drug:789              -> "Take smokeleaf joint (drug x3, addictive)"
social:456            -> "Talk with Jan (lover, opinion +40)"
flee                  -> "Flee from 3 hostiles, nearest 8m N (unarmed, cover 20%)"
```

When a single modded pawn/thing throws, signals are built section by section with fallbacks,
and the whole colony request is not lost.

## Logs

`Player.log`:

```text
Windows  %USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Logs\Player.log
Linux    ~/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Logs/Player.log
macOS    ~/Library/Application Support/Ludeon Studios/RimWorld by Ludeon Studios/Logs/Player.log
```

Look for `[ColonistAutonomy]` lines:

```text
[ColonistAutonomy] companion ready
[ColonistAutonomy] settings: enabled=true mode=action refresh=30s ... auto_start=true server=http://127.0.0.1:8081
[ColonistAutonomy] auto-start: started llama-server (pid 1234, log: .../ColonistAutonomy/llama-server.log)
[ColonistAutonomy] auto-start: stopped llama-server
[ColonistAutonomy] Kimmy: work:PlantsCut:14072 (confidence 0.66, mode action)
[ColonistAutonomy] applied CutPlantDesignated to Kimmy (cutting: bamboo tree.)
[ColonistAutonomy] Erickson: joy (confidence 0.31 < 0.40) -> vanilla
[ColonistAutonomy] WARN transport: ... (os error ...)
```

There is one line per decision; with `debug=true`, candidates, questions, and state are added.

Timing measurements are logged at INFO level:

```text
[ColonistAutonomy] collect: 3 pawn(s) in 2 ms                       # C#: state + candidate scan + JSON
[ColonistAutonomy] request: 3 pawn(s) in 707 ms (input 658 tokens)  # HTTP + model + context cost
```

With `debug=true`, `host stats: ...` from `ctx.stats` (bridge operation time) is added.

## Licenses and attribution

In the mod package, under `About/`:

- `LICENSE.txt` — the mod's code license (MIT),
- `MIT-llama.cpp.txt` — the llama.cpp license,
- `Apache-2.0.txt` — the Clef/Clef Flash model license,
- `THIRD_PARTY_NOTICES.md` — sources, versions, and SHA256 of the downloaded files.

A Steam-ready Workshop description and upload steps are in `workshop/DESCRIPTION.md` (raw
BBCode in `workshop/description.bbcode.txt`); `WORKSHOP.md` keeps the longer source draft.

## Limitations and notes

- **Multiplayer is not supported.** Model-driven jobs and the schedule gate run on the local
  client only; expect desyncs with multiplayer mods.
- The sleep gate is a conservative estimate, not a scheduler: it looks only at the next **Sleep**
  block, uses straight-line distance plus work-type durations, and never refuses player-forced
  jobs, drafted/mental pawns, or firefighting, patient and doctor work.
- Scheduled drug policies (for example penoxycyline) and tending patients stay with vanilla. Drug
  candidates cover only drugs the pawn's policy allows for joy; the model does not override the
  policy and does not self-treat.
- Needs (`eat`/`sleep`/`joy`) get their target from vanilla; the model chooses the **type** of
  action.
- Social uses a custom `JobDriver` (`SmartColonists_ClefSocialize`) and
  `InteractionDefOf.Chitchat`.
- With the default 16k context, the recommended Flash 9B model used about 16 GB of VRAM in
  testing. The optional 27B Q4 model needs 24 GB or more and did not fit a 24 GB card with that
  context; it shares the GPU with the game, so a request can take longer. The timeout is 60 s,
  and after three errors the mod backs off for 60 s and hands decisions back to vanilla. Switch
  back to the Flash model for faster iterations.
- The build script compiles the native library for the host platform by default. Use
  `--all-targets` (`-AllTargets`) for a folder that also carries `win-x64`, `osx-x64` and
  `osx-arm64`.
- The dependency installer supports Windows x64, Linux x64 and macOS (x64 / Apple Silicon).
- There is no `llama-server` auto-start when dependencies are missing; the mod shows a one-time
  hint instead.

## Tests

```bash
cargo test
cargo clippy --all-targets -- -D warnings
```

Run them against the same `rimworld-rs` line used to build (the one with
`SettingSpec::action`/`label`/`choice`). Unit tests cover the response parser, mode selection,
state truncation, settings, per-action and relative work thresholds, the drug action kind and
threshold, the server health probe, Hybrid selection, unique-target keys, and the installer
helpers. Verified in game on Linux: mod and companion startup, the HTTP decision loop
against a local server, low confidence → vanilla, and actual job application in the original
three modes plus Hybrid (`SmartColonists_ClefSocialize`).

On macOS the in-game check covered mod and companion startup, the HTTP decision loop against a
local server, server auto-start with the expected arguments, and server stop when the game
quits. The ignored installer tests also downloaded, extracted and ran the pinned macOS
`llama.cpp` package successfully.

### In-game self-test

The companion carries a smoke test that runs only when the game is launched with
`-clefselftest`. The flag also makes the Rust side skip the dependency prompt and the model
server, so the test needs no model and no network:

```bash
# from the rimworld-rs checkout, with this mod installed
cargo xtask run -- -quicktest -clefselftest -mod=brrainz.harmony,snowdrop.colonistautonomy
```

It runs after the quicktest map is ready, logs `[selftest] PASS/FAIL/SKIP` lines, and ends with
`[selftest] summary N passed, N failed, N skipped`. The suite covers ThinkTree defs, settings
labels, candidate and question generation in all four modes, job builders, drug candidates and
jobs, the schedule gate, the gated `Humanlike_PreMain` node, and the state payload. The latest
run reported 46 passed, 0 failed, 0 skipped.

## Disclaimer

Portions of the materials used to create this content/mod are trademarks and/or copyrighted
works of Ludeon Studios Inc. All rights reserved by Ludeon. This content/mod is not official
and is not endorsed by Ludeon.

## License

MIT (the mod's code). The Clef model has its own license — it is not bundled with the mod.
