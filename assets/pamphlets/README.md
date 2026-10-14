# Conspiracy pamphlets — roleplayer photo drops

Handout images for the roleplayer antic `DropPhoto` (FakeFriends.cs): the plugin loads
JPGs from `oxide/data/FakeFriends/photos/` at init (`LoadPhotoCache`), uploads them to
the server's FileStorage, and stamps a random one onto the photo items roleplayers drop
near players. Until this batch, that folder didn't exist — every print was blank.

Half are Rust-lore conspiracies (cargo ship never docks, scientists are clones, nobody
drives the Bradley, recyclers hum at 3AM, who fuels the patrol heli), half are real-world
classics bent into Rust (moon landing filmed at Launch Site, birds aren't real, flat-map
society, Cobalt never left). `island-resets` is the meta one — the residents remember
across wipes, which is literally true.

## Constraints

- **854×480 CSS layout, rendered at 2× (1708×960)** — native Rust photo-item
  aspect; the 2× device scale keeps text sharp in the inspect view (v0.15.1).
- **≤ 512 KB each** (`LoadPhotoCache` skips bigger files; raised from 256 KB
  for the 2× renders), max 64 files loaded.

## Shared effects (every source carries both — ported from RustQuests 2026-08-08)

- **Frame-safe zone** (`.safe`): Rust picture frames display a squarer ~4:3
  center crop of the 16:9 print, cutting roughly 107px off each side — framed
  pamphlets lost the first/last letters of every line. Each file's composition
  is wrapped in `<div class="safe">` with `transform:scale(.72)` about the
  center, so framed copies lose nothing and hand-held copies read as a sheet
  with print margins. Full-bleed texture layers (paper, grain, tape, film
  strips) stay OUTSIDE the wrapper so the margins keep their texture.
- **Dirt pass** (`.dirt`): a `mix-blend-mode:multiply` overlay — overall brown
  cast + edge vignette + a handful of stain blobs (positions varied per file) —
  added as the LAST element in every file, above the grain. Without it the
  prints rendered too bright next to Rust's own papers (compare the workbench
  blueprint). Multiply means transparent areas don't darken and dark designs
  dim proportionally, so it's safe on every sheet.

Caution from this pass: the `.safe` scale exposes content that used to hang
invisibly below the 480px fold (island-resets hid two closing lines that way) —
always re-render and eyeball every JPG after edits.

## Pipeline

Sources are self-contained HTML in `src/` (system fonts only, xerox-zine aesthetic).

```bash
powershell -ExecutionPolicy Bypass -File assets/pamphlets/render.ps1
```

renders every `src/*.html` via headless Chrome and writes the JPGs next to this file.

## Deploy

Copy `*.jpg` to your server's `oxide/data/FakeFriends/photos/` (the release zip ships them under `data/FakeFriends/photos/`).
Takes effect on the next plugin load — `oxide.reload FakeFriends` or server restart.
