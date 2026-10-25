# Iteration 1: first shader

Date: 2026-10-24. Commit `249cdb0`. Engine 26.10.13b. RTX 3090, Ryzen 9 9950X. Editor play mode,
`scenes/flatpack_benchmark.scene`.

Shader: one depth parallax step per frame, three frames blended with plain barycentric weights. Bakes: 8 x 8
hemisphere frames, 2048 atlas, bounds from the box half diagonal.

## Performance

Empty scene: 1.21 ms GPU. Raw numbers in `data/bench.jsonl`.

| Model | Instances | Triangles drawn: model / card | Draw calls: model / card | GPU ms: model / card |
|---|---:|---|---|---|
| Hospital bed | 100 | 333,327,578 / 726 | 42 / 46 | 47.13 / 1.52 |
| Small tree, camera near | 100 | 9,906,722 / 684 | 192 / 48 | 3.26 / 2.03 |
| Office chair | 400 | 22,236,602 / 2,940 | 41 / 43 | 4.14 / 1.68 |
| SCP-173 | 400 | 10,356,074 / 3,166 | 40 / 40 | 2.30 / 1.54 |
| Tall plant | 400 | 2,508,022 / 2,784 | 40 / 40 | 1.74 / 1.35 |
| Small tree, camera far | 100 | 2,414 / 614 | 26 / 32 | 1.25 / 1.59 |

## Image quality

Not good enough. Circling the office chair at close range, the card showed double images (armrests, piping and
wheels twice), details popped from one step to the next, and the card-vs-model difference measured **7.02**. Texture
streaming kept the atlases at 256 to 512 pixels until a fix in the same iteration.

## Media

- `media/flatpack_turntable_tree_small_01.gif`: the CC BY tree and its card turning (first shader).
- `media/flatpack_thumbnail.png`, `media/thumbnail_chair_raw.png`: the first package thumbnail attempt, Facepunch's
  citizen chair. The card's legs ghost.
- `local/` (not in git): turntables of the hospital bed, office chair and SCP-173. Their sbox.game packages have no
  licence set, so they stay off GitHub.

What changed next: [iteration 2](../iteration-2-stable-shader/README.md).
