# Steam Workshop assets

The images in this folder are exported from the Affinity source documents used to design them.
They are not loaded by the mod; they are upload material for the Workshop page.

The description to paste into Steam is in `description.bbcode.txt`; `DESCRIPTION.md` explains
where the field is and how to add the images. `../WORKSHOP.md` is the longer source draft.

| File | Size | Use |
|---|---|---|
| `cover-16x9.png` | 1280×720 | Workshop preview / header |
| `how-it-works.png` | 1280×720 | Description: the request pipeline |
| `decision-modes.png` | 1280×720 | Description: the three base decision shapes; Hybrid switches between them |
| `models.png` | 1280×720 | Description: Flash vs Q4, requirements |
| `../mod/About/Preview.png` | 512×512 | Square preview in the game's mod list |

The Affinity sources are in `source/` (`cover-16x9.af`, `preview-512.af`,
`how-it-works.af`, `decision-modes.af`, `models.af`) so the images can be edited later without
rebuilding them from scratch.

## Publishing

1. Build and install the mod normally (`cargo xtask install --path ...` from `rimworld-rs`).
2. In RimWorld, open **Options → Mod settings → Colonist Autonomy** once so the mod is known.
3. Use the game's Workshop upload flow, or create the Workshop item on Steam.
4. Paste `description.bbcode.txt` into the Workshop description field. `DESCRIPTION.md` has the
   step-by-step and the image placeholders.
5. Upload `cover-16x9.png` as the Workshop preview; `../mod/About/Preview.png` is the square
   preview for the in-game mod list.
6. Upload the three infographics as Workshop images and insert their URLs in the description.

The in-game mod description comes from `../mod/About/About.xml` (`<description>`); the Workshop
description lives on Steam and is prepared in `workshop/DESCRIPTION.md`.
