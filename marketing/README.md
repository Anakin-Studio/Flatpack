# Flatpack: sbox.game package page kit

Everything the `anakin.flatpack` package page asks for. Images are made from the latest bakes (iteration 6), always
with the real model beside the card, using licensed models only.

## Fields

| Field | Value |
|---|---|
| Title | Flatpack |
| Description (108 of 128) | `Bake any model into a 3D impostor card that takes over at a distance. Same look, a fraction of the GPU cost.` |
| Tags | `impostor lod optimization performance rendering billboard foliage trees octahedral editor tool` |
| Category | Rendering |
| Publish state | Hidden until the images are up, then Released |
| Source | https://github.com/Anakin-Studio/Flatpack |

## Images

| File | Size | Where | Shows |
|---|---|---|---|
| `package_square_512.png` | 512 x 512 | Square thumbnail | The real tree and its card, 572,544 triangles against 2 |
| `package_wide_910x512.png` | 910 x 512 | Wide thumbnail | The same, labelled |
| `package_tall_512x910.png` | 512 x 910 | Tall thumbnail | The same, stacked |
| `screenshot_1_tree.png` | 1920 x 1080 | Screenshot | Real tree and card side by side from 6 bounding radii, Ultra bake |
| `screenshot_2_forest.png` | 1920 x 1080 | Screenshot | 2,500 trees, split screen: native models left, Flatpack right, with GPU times |
| `screenshot_3_swap_chart.png` | 1920 x 1080 | Screenshot | GPU time against swap distance for a heavy, a medium and a light model, with native |
| `flatpack_trailer_1080p.mp4` | 1920 x 1080, 19 s | Video | Title, the tree turning (real against card), the fly-in over the forest split screen, end card |

Where the numbers come from: the GPU times (28.8 ms native, 10.0 ms Flatpack) are the swap-distance test of the same
2,500-tree field on an RTX 3090 ([`Benchmarks/iteration-6-real-scenes`](../Benchmarks/iteration-6-real-scenes/README.md)).
The triangle counts are the models' most detailed LODs, every draw call counted.

In the forest split screen, the far native trees look darker than the near ones: that's the tree model's own lowest
LODs. Flatpack's cards keep the near trees' colour. It's what the engine draws; nothing was adjusted.

## Longer description (for the package page body, if it has one)

```markdown
## One card instead of half a million triangles

Flatpack bakes a model into an impostor: a card that shows the model from every angle, lights like it and casts its
shadow. Up close you see your real model. From the distance where the card looks the same (Flatpack measures it after
every bake), the card takes over.

## Set it up

1. Add **Flatpack Renderer** to an object with a model, press **Bake Impostor**.
2. Or open **Editor > Flatpack > Optimize Scene**: it lists every model in your scene, recommends what to bake, and
   applies it to every copy in one undoable step.

## What it saves

- 2,500 trees (534,024 triangles each): **28.8 ms to 10.0 ms** of GPU time.
- On a real map (an SCP facility with a forest on the surface): **13.1 ms to 9.5 ms** on the surface. Indoors, where
  walls hide distant props, the saving is small: there the cost is lights and draw calls, not triangles.
- Very light models (under a few thousand triangles) are cheaper left as models; the optimiser tells you which.

## Quality levels

Low, Medium (default), High, Ultra, or Auto, which picks the cheapest level that still looks right at your swap
distance. Bakes take a few seconds.

Open source, MIT: https://github.com/Anakin-Studio/Flatpack
```

## Credits for the images

Tree_small_01 and Tree_small_03 by [Small Box Studio](https://sbox.game/smallboxstudio) (CC BY 4.0).
