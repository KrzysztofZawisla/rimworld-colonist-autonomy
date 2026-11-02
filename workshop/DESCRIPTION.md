# Steam Workshop description

The Workshop description is not stored in the mod. Steam keeps it on the Workshop item page.
`mod/About/About.xml` only supplies the short description shown in the in-game mod list.

## Where to paste it

1. Publish the mod, or create the Workshop item on Steam. If the in-game upload dialog offers a
   description field, you can paste the text there, but the item page is the source of truth.
2. Open the item page and click **Edit** (or **Edit description**, depending on the Steam layout).
3. Open `description.bbcode.txt`, select all, and paste it into the description field.
4. Replace the three image placeholders (see below). The Steam editor preview shows the result.
5. Save the page.

The Steam editor uses BBCode, not Markdown. Its toolbar inserts the same tags (`[h2]`, `[b]`,
`[list]`, `[url]`, `[img]`). You can paste the whole file and then use the toolbar only for
images if you prefer.

## Images

The item preview and the description images are uploaded separately.

- **Preview image:** use `cover-16x9.png` (1280×720) for the Workshop page; use
  `../mod/About/Preview.png` (512×512) as the square preview in the game's mod list.
- **Description images:** upload `how-it-works.png`, `decision-modes.png` and `models.png` to the
  item's images, or to any public image host. Copy each direct image URL and replace:
  - `PASTE_URL_FOR_HOW_IT_WORKS_IMAGE`
  - `PASTE_URL_FOR_DECISION_MODES_IMAGE`
  - `PASTE_URL_FOR_MODELS_IMAGE`

If the editor has an image button, place the cursor on one of those lines and use the button
instead; it inserts the `[img]...[/img]` tag for you. Keep the three images in the order used in
the description.

## Formatting notes

- Steam renders `[h1]`, `[h2]`, `[b]`, `[list]`, `[*]`, `[url]`, `[img]` and `[hr][/hr]`.
- Do not paste Markdown headings or `**bold**`; Steam shows them literally.
- The description should stay readable in the Steam app and in a browser. The BBCode above uses
  short paragraphs and lists for that reason.
- If you edit the copy, update both this file and `description.bbcode.txt`.
