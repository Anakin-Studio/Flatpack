# Plan: making the card look like the model

Review of 2026-10-25, against the code at commit 92b5512 (iteration 7).

**Status (iteration 8, same day):** A is in as a dial, off by default (it cancels out on the test set). B and C are in:
wrong pixels at 2 radii 34.9% to 21.9% at Medium. The foliage pass and the rim pass were measured and dropped. The
metres to-do is done. Numbers and reasons: [`Benchmarks/iteration-8-card-look`](../Benchmarks/iteration-8-card-look/README.md).
The question was: what single direction would make the cards look drastically better? The answer below is one
headline change in three parts (depth-aware reconstruction), then a foliage pass, then a silhouette pass. Everything
else is tuning.

Sources read: `flatpack_impostor.shader`, `FlatpackBaker.cs`, `FlatpackMath.cs`, `FlatpackQualityCheck.cs`, the
benchmark write-ups for iterations 3 to 7 and their images, and the horse statue bake in `Assets/flatpack/`.

## Where the look is lost today

Ranked by how much of the visible error each one causes, from the heatmaps and distance sheets.

1. **Three views that disagree get averaged.** `FlatpackSelectFrames` hands the pixel shader barycentric weights, and
   `FlatpackSampleFrame` sums colour, normal and hit position with `weight * coverage`. Nothing asks whether the
   three traces landed on the same surface. Where they didn't (a wing edge, a spindle, a wire panel seen at an angle),
   the pixel shows two surfaces at once: the doubled dragon wings and spindles in the iteration 5 heatmaps, the fence
   panel doubling at Medium in iteration 7, the chunky overlapping patches in the hero card's crown. `s.HitOs` is also
   the average of three different points, so the written depth sits between surfaces. This is the artefact a viewer
   notices first, and it is the one no quality level fixes (iteration 4: "more views don't reduce flicker").
2. **Depth is the weakest channel and carries the most weight.** The surface is rebuilt from 8 bits of depth spanning
   the whole bounding sphere (2 radii), stored as the alpha of a BC7 normal atlas and point sampled. On a surface seen
   at a grazing angle one texel spans many depth steps, so the search lands on a terrace: the stair-step streaks on the
   chair seat and the dragon plinth, present at every level. For a flat prop the sphere's depth range is several times
   what the model uses, so most of the 8 bits are wasted.
3. **Fixed-step search steps over thin parts.** Sixteen steps over 2 radii means a step of 1/8 radius; a spindle or a
   twig thinner than that is skipped unless the shell test catches it. The chair holds up from 16 radii at Medium for
   this reason, and Miss Fallback exists only to paper over it (and ghosts). Nothing in the bake tells the search where
   surfaces can't be, so it can't skip empty space either.
4. **Half of every frame is empty.** Each frame is the bounding sphere, so a tall statue uses about 40% of its cell's
   width (horse atlas: the object is roughly 70 by 150 texels of a 170 texel cell). The README's "about 170 pixels
   across at Medium" is really 70 to 120 on most props. This is the cheapest sharpness there is: the same atlas,
   framed per view, holds 1.5 to 2 times the texels on the object.
5. **Foliage lights like a cutout.** The card carries each leaf's own normal, so a crown lit by one sun shows a
   salt-and-pepper mix of lit and dark leaves where the real tree's foliage shader gives soft wrap lighting and
   translucency. The self-shadow bake helps the brightness match (hero: within 2%) but it is stored per texel from
   eight hard shadow maps, and the hero crop shows it as dark blotches.
6. **Silhouettes are a hard cut.** s&box renders without MSAA, so Alpha To Coverage does nothing and the edge is one
   clipped pixel row. Jaggies on every card, specks on foliage (Edge Floor trades them for a thinner edge). The real
   model has the same jaggies, so the harness doesn't punish this, but it is what makes a card read as "a card" at 6 to
   12 radii.

Lesser, noted for the fix-in-passing list: dilation grows in a diamond (4-neighbour breadth-first, so the halo is
Manhattan shaped), BC7 shares one block between normal colour and depth alpha, the binary refinement is point sampled,
the one mip level for all three traces comes from the first frame only.

## The headline change: depth-aware reconstruction

Three parts. Each stands alone, but together they attack items 1 to 4 and they share one new bake output.

### A. Blend by agreement, not by angle (shader only, S)

After the three traces, find the nearest hit along the view ray (`t_min` over the frames that hit). Scale each frame's
weight by how close its hit is to that one:

```
agree_i = saturate( 1 - ( t_i - t_min ) / ( k * radius ) )   // k about the shell thickness, 0.05 to 0.1
w_i = w_i * agree_i
```

Frames that hit far behind the nearest surface fade out of the blend instead of being averaged in. This is what
Epic's Impostor Baker material does ("depth blending") and it is the standard cure for doubling in octahedral
impostors. It is not the "facing weights" idea dropped in iteration 5 (that weighted by view angle, not by where the
surfaces are). Renormalise after scaling, keep the barycentric weights as the base so transitions stay continuous.

What it should fix: doubled wings, spindles and wires; the between-surfaces depth write; most of the "chunky patches"
in foliage crowns (leaf layers that disagree stop blending into mush). What it may cost: a slightly harder view
transition where one frame takes over; the orbit test's "jump" number is the guard.

Done when: wrong pixels at 2, 4 and 8 radii drop on the dragon, chair and fence with no model worse, and the orbit
jump on the chair stays within 10% of today's 5.33.

### B. Per-frame framing and a real depth channel (bake + shader, M)

Store, per frame, the tight bounds the baker saw: image rectangle (scale and offset inside the cell) and depth range
(nearest and farthest surface along the frame axis). Write them into a tiny lookup texture, one texel per frame
(12 x 12, RGBA16F, a fourth texture on the material: `g_tFlatpackFrames`). The vertex shader reads it for the three
chosen frames and passes the values `nointerpolation`, so the pixel shader pays nothing extra.

- Framing: the baker renders each view with the orthographic camera fitted to that view's silhouette (two passes: a
  cheap coverage pass at low resolution to find the rectangle, then the real passes), padded a few texels. The shader's
  `FlatpackFrameUv` and `uvP / uvPerT` map through the rectangle. The frame's own mips stay clean because the padding
  stays inside the cell. Expected gain: 1.5 to 2 times the texels on the object at the same atlas size, so Medium looks
  like today's Ultra up close and Low becomes usable earlier.
- Depth range: `camera.ZNear` and `ZFar` fitted to that view's surfaces instead of the sphere's front and back. For a
  flat prop this alone gives 2 to 4 times the depth precision for free.
- Depth storage: move depth out of the normal atlas alpha into its own single-channel texture. Check what the texture
  compiler accepts for one channel: BC4 is the safe fallback (8-bit, but no longer sharing a BC7 block with the
  normal); R16 or R16F uncompressed is the target (8 MB at 2048, which the memory diet below pays back). The normal
  atlas then drops to BC5 (two channels, reconstruct Z), which is sharper than BC7 for normals and half the size.

Done when: the chair seat and the dragon plinth lose their stair steps at 2 radii in the heatmap, and the close-range
wrong pixel average over the test set improves by at least what Ultra gains over Medium today (about 8 points at 2
radii), at Medium's memory.

### C. Conservative search: a coarse min-depth map (bake + shader, M)

Bake one extra tiny atlas: each frame's depth reduced 8 to 1 with MIN (the nearest surface in each 8 x 8 texel block),
so 256 x 256 R8 for a 2048 bake, 64 KB. The search runs in two levels:

1. March the coarse map. A block whose minimum depth is behind the ray is guaranteed empty, so the ray skips it in one
   step and never steps over a thin part, because a one-texel twig lowers its block's minimum.
2. Where the coarse map says a surface may be, refine in the full-resolution depth as today (the shell test and the
   binary steps stay).

This is quadtree displacement mapping cut down to two levels. It replaces Miss Fallback and most of the reason for
32 search steps: thin parts are found by construction, and the full-resolution samples go only where surfaces are.
It should also help GPU time where cards fill the screen, since most of a card's rays pass through empty blocks.

Done when: the chair holds up from 4 radii or better at Medium (today 16), the weed tree's twigs appear in the card at
4 radii, Miss Fallback can be removed, and the dense-forest GPU test is no worse than today's Medium.

### Why these three together

A fixes what the viewer sees first (doubling). B gives the search better data and the eye more texels at no memory
cost. C makes the search trustworthy on thin parts, which is where every remaining close-range failure in the
benchmarks lives. Each is measured by the harness that already exists; none depends on engine features s&box lacks.

## Foliage pass (M)

Only for bakes the baker recognises as foliage (any material on `foliage.shader`, or an alpha-tested material whose
partial-coverage share is high but under the see-through cut). Solid props keep today's lighting.

1. **Translucency.** Bake the Transmission debug view into the roughness/AO atlas (the blue channel now holds sky
   visibility; translucency can share the metalness alpha slot since foliage is never metal, flagged in the material)
   and apply the engine's foliage wrap lighting in the card's shading. Roadmap item 7; this is the biggest lighting gap
   on trees and ferns.
2. **Normal smoothing.** Blend each texel's baked normal toward a shell normal (the direction from the bounding centre to
   the hit point, or a wide coverage-weighted blur of the frame's normals) by a bake slider, default about 0.5 for
   foliage. SpeedTree and Unity billboards do this; it turns the salt-and-pepper crown into one lit volume with leaf
   detail on top. No new channel: store the blended normal.
3. **Self-shadow cleanup.** Blur the sky-visibility map over 3 texels before packing, and turn Bake Self Shadow on by
   default for foliage with Sky Occlusion at the value the hero needed (1.0). The hero's blotches are per-texel noise
   from eight hard shadow maps; a blur removes them without changing the average.

Done when: the hero tree and the weed tree match the model's brightness within the current 2% without hand tuning, and
their DSSIM at 6 and 12 radii in the bake check drops below the knight's (a solid control).

## Silhouette pass (M, experimental)

The only route to a soft edge without MSAA or TAA is a second, translucent draw for the rim. Give the card model a
second mesh (the same quad) with a rim material on a translucent variant of the shader: it draws only pixels whose
coverage is between 0 and Alpha Clip, with no depth write and a depth test against the opaque card's depth so interior
pixels are rejected. Instanced, so it is one extra draw per bake, not per object. Cost: the rim pass traces again (use
4 steps; edges don't need more). Judge by eye and by the bake check's DSSIM, not by wrong pixels: the real model is
jagged too, so the shape score will call a smooth edge "different". If the rim pass costs more than 20% on the
dense-forest test, restrict it to cards above a screen size (the edge matters at 6 to 12 radii, not at 48).

## Memory diet that pays for the above

| Atlas | Today | Proposed |
|---|---|---|
| Albedo + coverage | BC7, 4 MB at 2048 | BC7, unchanged |
| Normal + depth | BC7, 4 MB | Normal BC5 2 MB, depth R16 8 MB (or BC4 2 MB) |
| Roughness, AO, sky, metal | BC7, 4 MB | BC7 at half resolution, 1 MB (roadmap item 6) |
| Frame lookup | none | 12 x 12 RGBA16F, negligible |
| Coarse min-depth | none | R8 at 1/8, 64 KB |

Medium stays at 16 MB with uncompressed depth, or drops to 10 MB with BC4 depth. Either is a better trade than today's
layout: depth precision is what the surface is built from, roughness and AO are not.

## Fix in passing

- Dilation: use Euclidean distance (jump flood) so halos are round; the diamond halo shows at frame mips.
- The one mip level is taken from frame 0's centre-plane UV; after B, take it from the chosen frame's own scale.
- Binary refinement samples point depth; after B's 16-bit depth, a final linear interpolation between the last two
  samples is cheap and should remove the last terrace (iteration 5's "smooth hit" failed on 8-bit data).
- `g_flFlatpackThickness` has a floor of 1.5 steps; after C the floor can drop, since the coarse map stops step-overs.

## Harness and docs to-do

- **Distance sheets need metres as well as radii.** The "How close can you get?" sheets (knight, tree, chair in
  iterations 3 and 4) label each column in bounding radii and screen pixels only. Radii mean nothing to a reader who
  doesn't know the model's size; the iteration 6 tree sheet already does it right ("6 radii (21 m)"). Every sheet,
  the README swap table and the Flatpack Renderer inspector should show the distance in metres next to the radii
  (radius in world units times radii, converted the way the iteration 6 sheet did; write that conversion down once in
  `FlatpackMath` so the inspector, the bake check summary and the sheets agree). The sheet script lives in the editor
  harness, not in the repo, so re-render the iteration 3 and 4 sheets when it changes.

## Not worth doing (already measured, or needs engine features)

- More views, blend sharpness above 1, a higher minimum step count at range: measured flat or worse (iteration 4).
- Facing-weighted blending, filtered depth on 8-bit data, a thicker shell: measured worse (iteration 5).
- Dithered single-view selection, temporal blending: need TAA, which s&box does not have.
- Alpha To Coverage, dithered coverage: no MSAA, and the dither reads as noise (iteration 7).

## Order and effort

| Step | Effort | Depends on | Visible gain |
|---|---|---|---|
| A. Agreement blending | S | nothing | doubling gone on every model |
| B. Per-frame framing, depth channel | M | nothing | sharper at Medium, streaks gone on flat surfaces |
| C. Coarse min-depth search | M | B's depth texture | thin parts kept, Miss Fallback removed |
| Foliage pass | M | A (so crowns stop blending into mush first) | trees light like trees |
| Silhouette rim | M, experimental | A | smooth edges at 6 to 12 radii |

Start with A: a one-day shader change, measurable with the existing orbit and ladder harness, and it tells you how much
of the remaining error is reconstruction rather than resolution before you spend a week on B and C.
