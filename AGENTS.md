# AGENTS.md

Guidance for agents working in this repository.

## Language policy

- **All documentation and code must be written in English.** This covers source code, comments, doc comments, log messages, in-game UI text, commit messages, and every markdown file.
- Polish is only acceptable in proper names (for example the author's name) and third-party license texts.
- When touching an existing Polish passage, translate it instead of adding more Polish.

## Project overview

RimWorld 1.7 mod where colonists ask a local decision model (Cloudflare Clef by default; GGUF
served by `llama-server`) what to do. The model never generates text: it ranks a closed list of
actions prepared by the game, and the mod applies regular RimWorld jobs.

Three parts:

- `src/` — Rust bridge: settings, HTTP worker, decision cache and scheduling, answer
  selection, llama-server lifecycle, the Harmony schedule-gate install, and the dependency
  installer.
- `companion/` — C# assembly: `ClefThinkNode`, `ClefGatedThinkNode`, state and candidate
  building, the sleep-schedule gate, job drivers, and the in-game self-test.
- `mod/` — XML defs plus `About/` metadata and licenses.

## Commands

- Build: from the `rimworld-rs` checkout, `cargo xtask build --path <this repo>`; `install`
  copies the mod into the game (`RIMWORLD_DIR` overrides game detection), `package` writes
  `dist/`, and `--all-targets` builds every platform.
- Tests: `cargo test`
- Lint: `cargo clippy --all-targets -- -D warnings`

The mod ships one native library per platform under `Rust/<rid>/`; build all of them before
publishing so the same mod folder keeps working on Windows, Linux and macOS.

## Conventions

- Follow the existing style; do not add comments unless they explain non-obvious logic.
- Keep the game thread non-blocking: all HTTP work stays on the worker thread and results are
  applied in `on_tick`.
- Pin every downloaded dependency version and verify size and SHA256 before use.
- Update the documentation in the same change. Whenever behavior, defaults, settings,
  requirements, commands, or file layout change, update every affected document
  (`README.md`, `WORKSHOP.md`, `workshop/`, `mod/About/`) so the docs describe the code as it
  is, not as it was.
