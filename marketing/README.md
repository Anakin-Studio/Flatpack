# Flatpack: sbox.game package page kit

Copy and images for the `anakin.flatpack` package page. Paste the text blocks into the package's edit page on sbox.game
after publishing from the Library Manager.

## Package

| Field | Value |
|---|---|
| Title | Flatpack |
| Ident | `anakin.flatpack` |
| Type | Library |
| Org | Anakin Studio (`anakin`) |
| Asset licence (publish wizard) | CC BY 4.0 (the library's own assets are its two shaders; the code is MIT on GitHub) |
| Source | https://github.com/Anakin-Studio/Flatpack |

## Summary

For the one-line summary under the title (92 characters):

```
Bake any model into a 1-face impostor card that looks, lights and shadows like the original.
```

Shorter, if the field is tight (61 characters):

```
Turn 263,000-face models into one card. Same look, tiny cost.
```

## Description

```markdown
## One face instead of 263,066

Flatpack bakes a high-poly model into an impostor: a single camera-facing card that shows the model from every
angle, lights like it and casts its shadow. Up close you see your real model with all its LODs. Past a distance you
choose, the card takes over.

## Set it up in one click

1. Add **Flatpack Renderer** to any object with a model.
2. Press **Bake Impostor** in the inspector.

Every other object with the same model finds the bake on its own. For a whole level, **Editor > Flatpack > Impostor
Baker** bakes a list of models, and **Apply to Scene** adds the component to every object that has a bake.

## What it saves

Measured on an RTX 3090, same scene, real models against cards:

- 100 hospital beds (974,642 triangles each): **52.3 ms to 1.6 ms** of GPU time.
- 100 trees (263,066 triangles each) at close range: **10.2 ms to 4.2 ms**, 336 draw calls to 53.
- 400 office chairs (15,378 triangles each): **4.1 ms to 1.5 ms**.

Cards cost about the same whatever they replace, so the bigger the model, the bigger the saving.

## What you get

- **Baker** that renders your model from 144 directions in a few seconds: albedo, normals, depth, roughness and AO.
- **Impostor shader** that traces into the baked depth of the three nearest views so they line up as you walk around,
  then shades with the standard s&box lighting, shadows and fog.
- **Flatpack Renderer**: distance swap with hysteresis so nothing flickers at the boundary, optional dither
  cross-fade, optional extra mesh LODs before the card, and an automatic swap distance from the model's size.
- **One system for thousands**: a single time-sliced distance pass per frame, and all cards from one bake instance
  together.
- Hemisphere, full sphere and cylinder (tree billboard) layouts, atlases from 512 to 4096.

## Good to know

- Best on models with thousands of triangles or more. Very cheap models, or ones that already end in a billboard LOD,
  gain little.
- Metal parts light like painted surfaces on the card, and foliage translucency and wind don't carry over.
- Bake at 4096 for objects the camera gets close to.

Free and open source (MIT): github.com/Anakin-Studio/Flatpack

Made by Anakin Studio.
```

## Tags

Use the first five if the page limits tags; add the rest where it allows.

```
library, rendering, optimization, lod, impostor
performance, foliage, trees, shader, editor, tool, billboard
```

## Images

All images use Tree_small_01 by Small Box Studio (sbox.game/smallboxstudio/treesmall01, CC BY 4.0), credited on the
image. Keep the credit if you crop them.

| File | Size | Use |
|---|---|---|
| `thumbnail_1024.png` | 1024 x 1024 | Package thumbnail. The tree beside its card, "263,066 faces" against "1 face". |
| `cover_1920x1080.png` | 1920 x 1080 | Cover or first gallery image. 256 Flatpack trees: 926 triangles and 20 draw calls on screen. |
| `screenshot_before_after.png` | 2000 x 1070 | Gallery. Wireframe model against the card with its one quad outlined. |
| `screenshot_forest.png` | 1920 x 1080 | Gallery. The forest shot without text. |
| `turntable.gif` | 512 x 376 | Gallery or description. Model and card rotating side by side. |

Alt text, if the page asks for it:

- Thumbnail: "A 263,066-face tree beside its Flatpack impostor card, which has one face."
- Cover: "A field of 256 trees drawn as Flatpack impostor cards."
- Before/after: "Wireframe of a dense tree model next to its single-quad impostor card."
- Turntable: "A tree model and its impostor card turning side by side, looking the same from every angle."

## Don't use

The test suite also bakes a hospital bed, an office chair, SCP-173 and a plant from sbox.game. Their creators set no
licence, so keep them off the package page. The benchmark numbers above come from them, which is fine; their images
are not.
