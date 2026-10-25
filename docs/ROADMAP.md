# Flatpack roadmap

Researched 2026-10-24: how the best impostor tools look and feel (Epic's Impostor Baker, Amplify Impostors, Unity LOD
Groups, Godot octahedral impostor addons, SpeedTree, Simplygon), production techniques and papers, and what the
s&box engine already offers that Flatpack doesn't use yet. Every item below is tied to a measured weakness from
[`Benchmarks/`](../Benchmarks/README.md) or a pain point users report with impostor tools.

Effort: **S** a day or two, **M** about a week, **L** longer.

## Done: one-click scene optimiser

- **Optimize Scene** (Editor > Flatpack > Optimize Scene). One window lists every model in the open scene with copies,
  triangles and bake status, and recommends each one with a reason ("293,609 triangles each: big saving", "Cheap
  already", "Animated: a card can't show animation", warnings for thin parts). Recommended models start ticked.
  **Flatpack Selected** bakes what's missing and adds Flatpack to every copy in one undo step; **Remove Flatpack from
  Scene** takes it all off, also undoable. Tested: 10 objects added, Ctrl+Z, re-apply, remove, Ctrl+Z.

## Done in iteration 4: quality levels

Measured in [`Benchmarks/iteration-4-quality-levels`](../Benchmarks/iteration-4-quality-levels/README.md).

- **Quality levels** (Low, Medium, High, Ultra, Custom) in every way of baking, including a picker in Optimize Scene.
  The sweep behind them: more views don't reduce flicker (the card already flickers as much as the real model and no
  more); 8 x 8 views ghost; pixels per view decide sharpness up close; search steps are the biggest lever on both
  quality and GPU cost.
- **Early depth test** in the shader (conservative depth): 6 to 29% less GPU time where cards overlap, same images.
- Tested and dropped: blend sharpness above 1 (worse), a higher minimum search step count at range (no change).

## Next: make it native (iteration 5)

1. **The card becomes a real LOD of the model (M).** s&box can build a model with LODs from code:
   `ModelBuilder.AddMesh(mesh, lod)` and `WithLodDistance`, and Flatpack can read every LOD of the source with
   `Model.TryReadDrawCall`. Flatpack would build one model per bake: the source's own LODs, then the card as the last
   LOD. The engine then picks the card by screen size like any other LOD, with no swap component needed and no extra
   scene objects. Fixes: the card isn't part of the LOD chain; per-object swap bookkeeping.
2. **Works with the Clutter tool for free (S, after 1).** s&box's clutter system draws forests with GPU culling and
   indirect draws, keyed by model, and picks LODs on the GPU (up to 4 buckets). Once the card is a LOD (3 or lower) of a
   real model, painted forests get impostors automatically. For that the bake also writes a `.vmdl` (KV3 text with a
   `LODGroupList`) next to the source, since clutter stores model paths.
3. **Project default quality (S).** Quality levels shipped in iteration 4; what's left is a project-wide default.
   Putting `[Property]` settings on `FlatpackSystem` shows them on Project Settings > Systems with no extra UI.
4. **Quality score after every bake (M).** Run the iteration 3 harness inside the editor after a bake: render model and
   card from a few distances and report "holds up from N bounding radii", with a warning when it never does (fences).
   Then **set the swap distance from that score** instead of a flat 30 radii, and show it as a percentage of screen
   height (Unity's convention), which stays right across resolutions. Fixes: guessing distances; silent bad bakes.
5. **Far tier: cheaper cards at distance (S).** Iteration 4 found the search already drops to 4 steps on small cards
   and raising that floor changed nothing, so the next saving is skipping the search entirely on tiny cards (0 steps
   ghosts badly up close, but may be invisible below about 30 px). Measure before shipping.
6. **Memory diet (S to M).** Half-resolution roughness/AO, skip maps that are a single value, and write `.vtex` files
   directly (`Texture.SaveToVtex`, BC7 or DXT1 where there's no alpha). Today a Medium bake costs about 16 MB of video memory and High 64 MB; the Godot
   addon reports 75% savings from half-res material maps and 33% from skipping constant maps. Show the cost in the
   window before baking.

## After that: better looking (iteration 6)

7. **Foliage translucency (M).** Bake the engine's Transmission view into a spare channel and apply the same
   wrap-and-back-scatter lighting `foliage.shader` uses (`ApplyFoliageLighting`). Fixes: leaves that glow on the real
   tree look flat on the card (weed tree, ferns).
8. **Wind on cards (S).** The engine already has global wind values (`g_vWindDirection`); sway the card's top corners a
   little and fade it out with distance, as SpeedTree and Unity do for billboards. Fixes: still trees next to swaying ones.
9. **Fences and fine mesh (S to M).** At a distance, let alpha-to-coverage show partial coverage (s&box uses MSAA, no
   TAA) instead of clipping mip-averaged alpha, so wire mesh reads as see-through instead of a solid panel. Hashed alpha
   testing is the published alternative, but a patent covers part of it, so try coverage first.
10. **Metal parts (M).** Store metalness in the roughness/AO map's spare channel. s&box has no metalness debug view, so
    the baker reads each source material's metalness value and renders a per-material pass. Fixes: chair wheels and
    the dragon's base light like plastic.
11. **Grazing-angle streaks (M).** Flat surfaces seen edge-on (a chair seat at 2 radii) show stair-step streaks at
    every quality level: the search lands on neighbouring texels of a steep depth slope. Try a filtered final
    refinement step, or a slope-aware thickness.
12. **Why 12 x 12 at 4096 loses at range (S).** It was the sharpest bake up close but worse than the default past 12
    radii. Likely mip alignment: 341-pixel views don't halve cleanly. If so, round views to a power of two.
13. **Tree pivots (S).** An option to centre the bake on the trunk (Epic's "Center XY On Mesh Pivot"), which removes
    ghosting on trees whose canopy leans off-centre.

## Easier to live with

Target design for the tools: the bake window shows the quality levels as four cards with memory and "good for" text
instead of a dropdown; the Flatpack Renderer inspector shows what the object is drawing right now, the swap distance in
radii and metres with a check against the bake's measured quality (item 4), Auto / Model / Card preview buttons, and
Re-bake at a higher level one click away; Optimize Scene shows the video memory a bake will add before you apply it.

14. **Live preview of `.fpack` assets (M).** Click a bake in the asset browser and see the model and card side by side
    with a distance slider and an "impostor only" mode; thumbnails show the card. s&box supports this with an
    `[AssetPreview("fpack")]` class (the Clutter tool's preview is the template).
15. **Stale bakes (M).** Store a fingerprint of the model and bake settings in the `.fpack`; when the model changes
    (`AssetSystem` change events), mark its bake stale and offer **Rebake Stale** in the optimiser. Fixes: cards that
    no longer match the model after an edit.
16. **Existing-bake prompt (S).** Baking a model that already has a bake asks: replace, keep both, or skip (Epic's
    Impostor Baker does this). Fixes: accidental overwrites.
17. **See what's happening (S).** A scene-view gizmo showing each Flatpack object's swap distance, and a debug tint for
    model / card / fading so popping is easy to track down (Unreal's LOD coloration mode).
18. **Pre-bake checks (S).** Warn before baking skinned models, translucent materials (they bake as opaque) and models
    with no bounds. Partly covered by the optimiser's recommendations.

## Not planned

- **Shared atlases across many models.** Useful for huge asset sets, but it fights per-model re-baking and close-up
  quality; revisit after the memory diet.
- **GPU-driven custom renderer.** Making the card a native LOD (item 1) lets the engine's own batching and the clutter
  system's indirect draws do this work. CPU cost today is already small: 0.14 ms per frame for 5,041 Flatpack objects.
- **Engine features that don't exist:** there's no TAA, no managed ModelDoc API and no built-in dithered LOD fade in
  s&box today, so ideas that depend on them are out.

## Sources

Epic Impostor Baker docs; Epic World Partition HLOD and automatic LOD docs; Unity LOD Group manual; Amplify Impostors;
hdrsniped/octahedral-impostor-lod-godot and wojtekpil/Godot-Octahedral-Impostors (ideas only); Wyman and McGuire,
Hashed Alpha Testing (2017, 2019); Castaño, Computing Alpha Mipmaps; Green (Valve), Improved Alpha-Tested
Magnification (2007); Haar and Aaltonen, GPU-Driven Rendering Pipelines (2015); Cinevva open-world impostor blog
(2026); the s&box engine source and API docs in the local install.
