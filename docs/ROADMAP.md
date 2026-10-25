# Flatpack roadmap

Researched 2026-10-24: how the best impostor tools look and feel (Epic's Impostor Baker, Amplify Impostors, Unity LOD
Groups, Godot octahedral impostor addons, SpeedTree, Simplygon), production techniques and papers, and what the
s&box engine already offers that Flatpack doesn't use yet. Every item below is tied to a measured weakness from
[`Benchmarks/`](../Benchmarks/README.md) or a pain point users report with impostor tools.

Effort: **S** a day or two, **M** about a week, **L** longer.

## Done in this round

- **Optimize Scene** (Editor > Flatpack > Optimize Scene). One window lists every model in the open scene with copies,
  triangles and bake status, and recommends each one with a reason ("293,609 triangles each: big saving", "Cheap
  already", "Animated: a card can't show animation", warnings for thin parts). Recommended models start ticked.
  **Flatpack Selected** bakes what's missing and adds Flatpack to every copy in one undo step; **Remove Flatpack from
  Scene** takes it all off, also undoable. Tested: 10 objects added, Ctrl+Z, re-apply, remove, Ctrl+Z.

## Next: make it native (iteration 4)

1. **The card becomes a real LOD of the model (M).** s&box can build a model with LODs from code:
   `ModelBuilder.AddMesh(mesh, lod)` and `WithLodDistance`, and Flatpack can read every LOD of the source with
   `Model.TryReadDrawCall`. Flatpack would build one model per bake: the source's own LODs, then the card as the last
   LOD. The engine then picks the card by screen size like any other LOD, with no swap component needed and no extra
   scene objects. Fixes: the card isn't part of the LOD chain; per-object swap bookkeeping.
2. **Works with the Clutter tool for free (S, after 1).** s&box's clutter system draws forests with GPU culling and
   indirect draws, keyed by model, and picks LODs on the GPU (up to 4 buckets). Once the card is a LOD (3 or lower) of a
   real model, painted forests get impostors automatically. For that the bake also writes a `.vmdl` (KV3 text with a
   `LODGroupList`) next to the source, since clutter stores model paths.
3. **Presets and project defaults (S).** "Tree", "Prop", "Building", "Hero (close-up)" presets in the bake window and the
   optimiser, and a project-wide default. Putting `[Property]` settings on `FlatpackSystem` shows them on Project
   Settings > Systems with no extra UI. Fixes: too many dials (Epic's Impostor Baker does exactly this with preset assets).
4. **Quality score after every bake (M).** Run the iteration 3 harness inside the editor after a bake: render model and
   card from a few distances and report "holds up from N bounding radii", with a warning when it never does (fences).
   Then **set the swap distance from that score** instead of a flat 30 radii, and show it as a percentage of screen
   height (Unity's convention), which stays right across resolutions. Fixes: guessing distances; silent bad bakes.
5. **Far tier: cheaper cards at distance (S).** Beyond a distance, skip the depth search and blend three plain
   projections; further still, show one view. Iteration 2 measured cards costing more than a tree's own billboard LOD
   at range; Epic and the Godot addons all tier quality this way.
6. **Memory diet (S to M).** Half-resolution roughness/AO, skip maps that are a single value, and write `.vtex` files
   directly (`Texture.SaveToVtex`, BC7 or DXT1 where there's no alpha). Today each bake costs about 17 MB; the Godot
   addon reports 75% savings from half-res material maps and 33% from skipping constant maps. Show the cost in the
   window before baking.

## After that: better looking (iteration 5)

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
11. **Tree pivots (S).** An option to centre the bake on the trunk (Epic's "Center XY On Mesh Pivot"), which removes
    ghosting on trees whose canopy leans off-centre.

## Easier to live with

12. **Live preview of `.fpack` assets (M).** Click a bake in the asset browser and see the model and card side by side
    with a distance slider and an "impostor only" mode; thumbnails show the card. s&box supports this with an
    `[AssetPreview("fpack")]` class (the Clutter tool's preview is the template).
13. **Stale bakes (M).** Store a fingerprint of the model and bake settings in the `.fpack`; when the model changes
    (`AssetSystem` change events), mark its bake stale and offer **Rebake Stale** in the optimiser. Fixes: cards that
    no longer match the model after an edit.
14. **Existing-bake prompt (S).** Baking a model that already has a bake asks: replace, keep both, or skip (Epic's
    Impostor Baker does this). Fixes: accidental overwrites.
15. **See what's happening (S).** A scene-view gizmo showing each Flatpack object's swap distance, and a debug tint for
    model / card / fading so popping is easy to track down (Unreal's LOD coloration mode).
16. **Pre-bake checks (S).** Warn before baking skinned models, translucent materials (they bake as opaque) and models
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
