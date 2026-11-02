# Steam Workshop description draft

The Steam-ready BBCode and the upload steps are in `workshop/DESCRIPTION.md` and
`workshop/description.bbcode.txt`. This file is the longer source draft.

## Colonist Autonomy

Colonists can ask a local decision model what to do when vanilla has no job for them. The
default model is Cloudflare Clef; the mod only ranks a closed list of actions prepared by the
game, then builds and applies normal RimWorld jobs. It does not use the cloud and does not send
colony data anywhere except the local server.

### Requirements

- **Harmony** (required dependency).
- Windows x64 or Linux x64 with a working Vulkan driver, or macOS (Intel or Apple Silicon).
- A GPU with enough VRAM: about 16 GB for the recommended Clef Flash Q8 (9B) with the default
  16k context, or 24 GB or more for the optional Clef Q4 (27B). CPU-only inference is not
  installed by the one-click installer.
- Disk space for the model (9.7 GB for Flash, 17.6 GB for Q4) plus the llama.cpp archive
  (12-38 MB depending on the platform).

### First start

1. Subscribe to Harmony.
2. Open **Options → Mod settings → Colonist Autonomy**.
3. Click **Install / check dependencies** (installs the recommended Flash model), or choose
   **Install llama.cpp** and then **Download Clef Flash Q8**; the Q4 model is optional.
4. Wait for the download (the status line shows progress). A cancelled download keeps the
   `.part` file and the next click resumes it.
5. Enable **Auto-start llama-server** in the mod settings after reviewing the paths, then start
   or load a colony. The local llama-server starts when the colony loads and stops when you
   return to the main menu or quit the game.

If dependencies are missing, the mod shows a one-time hint in game instead of trying to start
a server that cannot exist.

### Source code

The full source code is in
[KrzysztofZawisla/rimworld-colonist-autonomy](https://github.com/KrzysztofZawisla/rimworld-colonist-autonomy).
The native bridge and build tooling are built with
[rimworld-rs](https://github.com/KrzysztofZawisla/rimworld-rs).

### Models

- **Clef Flash Q8 (9.7 GB, recommended)** — 9B model, faster and lighter; the default choice.
- **Clef Q4 (17.6 GB, optional)** — full 27B model, best quality, highest VRAM use.
- GGUF files: [bartowski/Cloudflare_clef-GGUF](https://huggingface.co/bartowski/Cloudflare_clef-GGUF)
  and [bartowski/Cloudflare_clef-flash-GGUF](https://huggingface.co/bartowski/Cloudflare_clef-flash-GGUF).
- The models are Apache-2.0; llama.cpp is MIT. Full notices and licenses are included in
  `About/THIRD_PARTY_NOTICES.md`.

### Notes

- The mod is a client. It does not start the model server until the dependencies are installed
  and **Auto-start llama-server** is enabled.
- Decisions are made locally. The mod does not send colony state to an external service.
- Auto-start/stop uses Harmony game events; without Harmony the mod's settings still open, but
  automatic server lifecycle is disabled.

### Disclaimer

Portions of the materials used to create this content/mod are trademarks and/or copyrighted
works of Ludeon Studios Inc. All rights reserved by Ludeon. This content/mod is not official
and is not endorsed by Ludeon.

### Workshop images

Upload material lives in `workshop/` (sources in `workshop/source/`):

- `cover-16x9.png` — header / first image,
- `how-it-works.png` — the request pipeline,
- `decision-modes.png` — the three base decision shapes; Hybrid switches between Intent and Action,
- `models.png` — Flash vs Q4 and requirements,
- `../mod/About/Preview.png` — 512×512 preview image.
