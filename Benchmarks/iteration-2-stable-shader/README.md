# Iteration 2: stable shader

Date: 2026-10-24. Engine 26.10.13b. RTX 3090, Ryzen 9 9950X. Editor play mode, `scenes/flatpack_benchmark.scene`
(one shadowed sun, sky, envmap, ground plane). Commit `1774408`. Raw numbers: `data/bench.jsonl`.

Shader version: per-frame depth search (16 steps up close, fewer as the card shrinks on screen), 12 x 12 hemisphere
views, 2048 atlases. The first shader's numbers are in [iteration 1](../iteration-1-first-shader/README.md).

## Method

For each model we spawn a square grid of instances (spacing 2.2 bounding radii, random yaw), point the main camera at
the whole grid, and run it twice: every Flatpack Renderer forced to the real model, then forced to the card. After
3 seconds to settle we read GPU time from `perf_overview` (average over the last 2 seconds) and triangles and draw
calls from `perf_rendering`. GPU time is the honest number: the editor caps the frame at 90 fps when unfocused, so
frame time sits at 11.1 ms in most runs. The empty scene costs **1.30 ms** GPU on its own.

Models are the high-poly props Orion SCP-RP uses, installed from sbox.game.

## Results

| Model | Source triangles | Instances | Triangles drawn: model / card | Draw calls: model / card | GPU ms: model / card |
|---|---:|---:|---|---|---|
| Hospital bed (`arcaneprops.hospitalbed`) | 974,642 | 100 | 326,505,084 / 706 | 42 / 46 | **52.29 / 1.63** |
| Small tree, camera near (`smallboxstudio.treesmall01`) | 263,066 | 100 | 39,162,766 / 780 | 336 / 53 | **10.23 / 4.21** |
| Office chair (`exeros.officechair`) | 15,378 | 400 | 21,406,190 / 2,834 | 46 / 47 | **4.11 / 1.51** |
| SCP-173 (`hueycan.scpunityscp173v1`) | 6,630 | 400 | 10,243,364 / 3,132 | 40 / 40 | **3.16 / 1.72** |
| Tall plant (`gtavteam.gta5apamphaccplanttall01`) | 1,832 | 400 | 2,584,966 / 2,864 | 40 / 40 | **1.75 / 1.70** |
| Small tree, camera far | 263,066 | 100 | 2,790 / 708 | 32 / 40 | **1.25 / 1.88** |

GPU time above the 1.30 ms empty scene:

| Model | Model cost | Card cost | Saved |
|---|---:|---:|---:|
| Hospital bed x100 | 50.99 ms | 0.33 ms | 50.7 ms |
| Small tree x100, near | 8.93 ms | 2.91 ms | 6.0 ms |
| Office chair x400 | 2.81 ms | 0.21 ms | 2.6 ms |
| SCP-173 x400 | 1.86 ms | 0.42 ms | 1.4 ms |
| Tall plant x400 | 0.45 ms | 0.40 ms | 0.05 ms |
| Small tree x100, far | -0.05 ms | 0.58 ms | -0.6 ms |

## Reading it

- Card cost depends on the pixels they cover, not on what they replace, so the saving grows with the source's
  triangle count. The 975k-triangle bed goes from 19 fps to the frame cap.
- Cards from one impostor instance: 100 or 400 cards add only a handful of draw calls. The close tree drops from 336
  draw calls to 53.
- The near tree cards cost the most (2.9 ms): they fill a large part of the screen, and every pixel runs the depth
  search.
- The far tree loses. `tree_small_01.vmdl` ships its own LOD chain ending in a 2-triangle billboard, so at a distance
  the engine already draws almost nothing, and the impostor pixel shader costs more than a plain billboard. The tall
  plant is too cheap to beat. Flatpack pays off on models without a cheap last LOD, or as a better-looking replacement
  for one.
- Not measured yet: cross-fade cost, the distance pass with thousands of renderers, 4096 atlases, VRAM.

## Image quality (orbit test)

`flatpack_suite.scene`, camera circling each model and its card in 3 degree steps at a close 5 to 6 bounding radii, the
worst case for an impostor. "Flicker" is the mean second time-difference per pixel (smooth motion cancels, popping
doesn't); "difference" is the mean absolute pixel difference between card and model.

| Model | Flicker, model | Flicker, card | Card vs model difference |
|---|---:|---:|---:|
| Office chair | 8.76 | 7.65 | 3.73 |
| SCP-173 | 3.44 | 2.79 | 3.70 |
| Small tree | 13.93 | 10.43 | 8.51 |
| Hospital bed | 18.37 | 18.36 | 7.05 |

With the first shader the office chair's difference was 7.02, with visible double images while turning. A grid that
didn't divide the atlas evenly (2048 / 12) pushed it to 13.1 until the baker started sizing atlases to a whole number of
frames.

## Media

The README hero (`docs/media/flatpack_hero.png`) and turntable (`docs/media/flatpack_turntable.gif`) come from this
iteration, along with everything in `marketing/`.

What changed next: [iteration 3](../iteration-3-ten-models/README.md).
