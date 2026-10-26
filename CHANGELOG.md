# Changelog

Flatpack by Anakin Studio. Newest entries first.

Testing levels: **static** (read through, not compiled), **compiled** (editor compile succeeded),
**editor** (exercised in the running s&box editor).

## 2026-10-26: README images re-rendered with iteration 8 bakes

- Change: every README image that shows a card is re-rendered from fresh bakes with the current baker: the hero tree
  (card brightness now within 1% of the tree, was 2%), the turntable, the knight distance sheet, the ten-model grid,
  the chair orbit against the first shader (difference from the model 2.5 against 6.3, frame-to-frame jump 3.4 against
  7.9), the tree distance sheet and the chair at every quality level. The new files live in `docs/media`; the
  benchmark folders keep their own iteration's images. The tree distance sheet's GPU footer now says it was measured in
  iteration 6. README text updated to the new numbers.
- Files: `docs/media/*`, `README.md`
- Testing: editor (all renders from the running editor; tree, chair at four levels, cactus, burger car and rock baked
  fresh, the other grid models from the iteration 8 test-set bakes).

## 2026-10-25: Metres beside bounding radii

- Change:
  - `FlatpackMath.MetresPerUnit`, `ToMetres` and `FormatMetres`: one conversion (world units are inches; one decimal
    under 10 m), the same one the iteration 6 tree sheet used for "6 radii (21 m)".
  - The Flatpack Renderer inspector shows metres beside "matches the model from" and "swaps at", using the object's
    scale. The bake check summary does too (`FlatpackQualityCheck.Report.Radius`).
  - README distance and swap tables carry metres. The iteration 3 and 4 distance sheets (knight, tree, chair) are
    relabelled with metres; their renders are unchanged. The harness heatmaps label rows in radii and metres.
- Files: `Libraries/Flatpack/Code/FlatpackMath.cs`, `Libraries/Flatpack/Editor/FlatpackRendererEditor.cs`,
  `Libraries/Flatpack/Editor/FlatpackQualityCheck.cs`, `Libraries/Flatpack/UnitTests/FlatpackMathTests.cs`, `README.md`,
  `Benchmarks/iteration-3-ten-models/media/distance_{knight,tree}.png`,
  `Benchmarks/iteration-4-quality-levels/media/presets_distance_{chair,tree,knight}.png`
- Testing: editor. Compiles clean; `FormatMetres` gives "21 m" for 6 radii of the 140.08-unit tree and "4.1 m" for the
  chair; bake summaries print metres (for example "4 bounding radii (15 m)" for the tree). The inspector label wasn't
  looked at by eye.

## 2026-10-25: Foliage pass and silhouette rim pass measured, not kept (iteration 8)

- Change: no code kept. Self shadow on by default for foliage (Sky Occlusion 1, blurred over 3 texels) and leaf
  normals leaned halfway toward the crown's outside were built and measured: both made the tree and the weed tree worse
  (tree 17.3% to 43.7% wrong pixels at 2 radii with the shadow, 33.4% with the normals; the two trees want opposite
  amounts of shadow). Translucency wasn't built: the Transmission debug view is black for `foliage.shader`. The rim
  pass wasn't built: a 4-step trace over every card alone costs at least 40% of the card's GPU time in the dense
  forest (at least 29% fewer frames per second: the knight forest 34 FPS to 22), twice the plan's 20% limit. Numbers in the iteration 8 README; roadmap item 7 updated.
- Files: `Benchmarks/iteration-8-card-look/README.md`, `docs/ROADMAP.md`
- Testing: editor (foliage variants baked and run through the harness; GPU cost measured in play mode).

## 2026-10-25: Coarse min-depth search, Miss Fallback removed (iteration 8, step C)

- Change:
  - Bake: a map of each view's nearest surface per 16 x 16 texel block (`<name>_fp_depthmin.pfm`, R16, about 0.1 MB),
    with `g_flFlatpackCoarse` and `g_flFlatpackCoarseBlock` in the material.
  - Shader: with the map, the search walks each view block by block, skips blocks whose nearest surface the ray never
    reaches, and samples the rest every Coarse Stride texels (`g_flFlatpackCoarseStride`, default 2), never more than
    half a shell apart in depth. The shell thickness loses its step-size floor there. Without the map (older bakes) the
    even-step search is unchanged.
  - Miss Fallback removed (`g_flFlatpackMissFallback` is ignored): with the coarse search it made five of six models
    worse (chair 9% to 38% wrong pixels at 2 radii).
  - The bake check and the see-through fit render the card twenty times before measuring: right after a bake into a
    new folder the new textures weren't on the GPU yet, and a chair measured 12 radii instead of 4.
  - Framed see-through bakes no longer thin below Soft Coverage up close (the fit only sees 8 to 32 radii; the fence
    nearly vanished at 2 radii). Bakes more than half partly covered (fine wire mesh: fence 0.59, weed tree 0.42) bake
    again unframed, which measured better for the fence.
- Files: `Libraries/Flatpack/Assets/shaders/flatpack_impostor.shader` (+ `.shader_c`),
  `Libraries/Flatpack/Editor/FlatpackBaker.cs`, `Libraries/Flatpack/Editor/FlatpackQualityCheck.cs`,
  `Libraries/Flatpack/Code/FlatpackImpostor.cs`, `README.md`, `docs/ROADMAP.md`, `docs/PLAN-card-look.md`,
  `Benchmarks/iteration-8-card-look/**`, `Benchmarks/README.md`
- Testing: editor. Seven-model harness: six-model wrong pixels at 2 radii 26.2% (framing alone) to 21.9%; chair holds
  up from 4 radii at Medium. Dense forest (2,500 cards, three interleaved rounds): tree flat (32 FPS), chair and knight
  6 to 15% more GPU time than iteration 7's Medium (chair 35.6 ms, 28 FPS, to 37.7 to 40.9 ms, 24 to 27 FPS; knight
  26.8 ms, 37 FPS, to 29.6 to 30.3 ms, 33 to 34 FPS); 8 x 8 blocks sampled every texel cost 7 to 24% more and were
  dropped.

## 2026-10-25: Per-view framing and a depth texture of its own (iteration 8, step B)

- Change:
  - Baker: each view's orthographic camera is fitted to the model's outline in that view (from the vertices, padded
    two texels), with its own width and height (`CameraComponent.CustomProjectionMatrix`), and its near plane on the
    view's nearest surface. The bounds go in a 64 x 32 lookup texture (`<name>_fp_frames.png`, uncompressed), read in
    the vertex shader for the three chosen views. New bake settings Fit Each View (on) and Depth Bits (8).
  - Depth moves out of the normal atlas's alpha into its own texture, spanning one bounding diameter back from each
    view's nearest surface: BC4 from `<name>_fp_depth.png` by default, or R16 from `<name>_fp_depth.pfm` with Depth
    Bits 16 (the texture compiler reads PNGs at 8 bits; a float PFM keeps all 16). The depth bake shader writes 16 bits
    split over red and green. Normals stay object-space BC7 in `<name>_fp_normal.png`.
  - Shader: framed bakes (`g_flFlatpackFramed` 1) march only each view's depth range, sample colour, normal and
    material through an anisotropic filter with the pixel's footprint (framing stretches texels), take the coverage
    mip from the footprint's shorter axis and the step count from the sphere-equivalent level.
  - Older bakes keep working unchanged through the material flag (checked: same harness numbers as before); re-bake
    to get the new look.
  - Tried and dropped: depth fitted to the object's own depth range (mips made fake surfaces at outlines from 12 radii),
    two-channel camera-space normals in BC5 (darkened foliage at range by about 7 levels), one mip level for both
    texel directions (far cards 2 to 12 points worse).
- Files: `Libraries/Flatpack/Assets/shaders/flatpack_impostor.shader` (+ `.shader_c`),
  `Libraries/Flatpack/Assets/shaders/flatpack_bake_depth.shader` (+ `.shader_c`), `Libraries/Flatpack/Code/FlatpackMath.cs`,
  `Libraries/Flatpack/Code/FlatpackImpostor.cs`, `Libraries/Flatpack/Editor/FlatpackBaker.cs`,
  `Libraries/Flatpack/Editor/FlatpackBakeSettings.cs`, `Libraries/Flatpack/Editor/FlatpackPfm.cs` (new),
  `Libraries/Flatpack/UnitTests/FlatpackMathTests.cs`, `README.md`, `Benchmarks/iteration-8-card-look/**`
- Testing: editor. Seven models baked at Medium with 8 and 16-bit depth and with fitting off; harness against
  iteration 7 bakes in the same session: six-model wrong pixels at 2 radii 34.9% to 26.2% (B alone, 16-bit); repeated
  bakes are byte-identical. Memory at Medium about 19 MB with 8-bit depth (was 17), 27 MB with 16-bit. Unit tests:
  every vertex inside its view's stored bounds, bounds round-trip through bytes; 10 of 10 pass.

## 2026-10-25: Agreement blending, off by default (iteration 8, step A)

- Change: shader dial Agreement (`g_flFlatpackAgreement`, in bounding radii, default 0) and Agreement Range
  (`g_flFlatpackAgreementRange`). Above 0, a view whose traced hit lies further than the falloff behind the nearest
  view's hit fades out of the colour, normal, material and depth blend; coverage still blends by the view weights. The
  fade weakens while the nearest view's share is under a quarter, so view changes stay continuous. The traces now return
  their hit and blend in `MainPs`; with Agreement 0 the result is the same as before.
- Files: `Libraries/Flatpack/Assets/shaders/flatpack_impostor.shader` (+ `.shader_c`), `Benchmarks/iteration-8-card-look/**`
- Testing: editor. Falloffs 0.04, 0.08 and 0.15 radii, with and without widening per mip level, on the seven-model set:
  the chair and the fern about a point better, the knight, dragon, tree and fence 0.3 to 1.5 points worse at 4 to 16
  radii; six-model average unchanged; chair orbit jump within 4%. On top of B and C, 0.5 points worse. The plan's
  "done when" isn't met, so it ships off.

## 2026-10-25: Stray specks off foliage fixed (Edge Floor)

- Change:
  - Shader: Edge Floor (`g_flFlatpackEdgeFloor`, default 0.75). The soft edge now keeps a pixel only when it's covered at
    least 0.75 x Alpha Clip. Before, any pixel above 1/255 of the edge ramp was drawn, and without MSAA a partly covered
    pixel beside an empty one was drawn solid: specks floating off foliage, most visible in the README hero. Applies to
    every bake, old and new, with no re-bake.
  - Debug view 3: each frame's own coverage at the pixel (rgb).
  - README hero re-rendered. Fence and weed tree re-baked (fit re-run); iteration 7 numbers updated.
  - README: the knight's close-up sheet now sits under the hero, and the tree distance sheet with the hybrid's GPU
    times moves to "How close can you get?", each with its own caption.
- Files: `Libraries/Flatpack/Assets/shaders/flatpack_impostor.shader` (+ `.shader_c`), `docs/media/flatpack_hero.png`,
  `Benchmarks/iteration-7-see-through/**`, `Benchmarks/README.md`, `README.md`
- Testing: editor. Hero crown: detached pieces 246 to 134, almost none visible by eye. Card against model coverage on the six
  test models at 6, 12 and 24 radii, two sides: 0.87 to 1.03 (was 0.98 to 1.17); a hard cut at Alpha Clip was tried and
  dropped (thinned the weed tree to 0.67). Bake check distances unchanged on all six. Fence bench re-run.

## 2026-10-25: See-through detail and baked metalness (iteration 7)

- Change:
  - Shader: Soft Coverage (`g_flFlatpackSoftCoverage`) and Soft Growth (`g_flFlatpackSoftGrowth`). Above 0, the card
    multiplies its coverage by the strength, and by `2^(growth x log2(texels per pixel))`, before the alpha cut, in
    place of the thin-part boost. Sub-texel wires stay solid lines and thicken as the card shrinks, like a real
    alpha-tested mesh. The depth pass uses the same coverage. 0 (the default) leaves solid models unchanged.
  - Baked metalness. The baker solves for it per texel from the Reflect view (real materials against copies forced to
    metalness 0 and 1, under one light) and stores it in the roughness/AO map's alpha as `1 + metal x 254`;
    `g_flFlatpackBakedMetal` turns it on, so older bakes are unchanged. Runs only for models with a metal-capable
    material. Fence wires now light like the real ones (63 against 65, were 101).
  - Baker: measures the share of partly covered atlas texels (`FlatpackImpostor.SeeThrough`). Above 0.4 it writes
    starting values for both dials and, when Measure Quality is on, fits them against the model
    (`FlatpackQualityCheck.FitSoftCoverageAsync`: 8, 16 and 32 radii, two sides, 1080p density, lit path) and patches
    the `.vmat`. Auto quality sends see-through bakes straight to Ultra (Medium doubles a wire panel at an angle). The
    per-sample alpha test in the supersample downsample now cuts at 128, like s&box materials.
  - Fence: holds up from 6 radii instead of never; coverage within 3.3 points of the real fence at 6 to 32 radii (was
    up to 6.7 short). Weed tree: holds up from 8 radii instead of 12. GPU: a field of 2,500 fences is slower as cards
    (14.0 ms real, 15.0 to 17.4 ms with cards), because a 538-triangle fence is cheaper than the card's depth search.
  - Iteration 7 benchmarks; README limits, swap table and material dials; roadmap items 9 and 10.
- Files: `Libraries/Flatpack/Assets/shaders/flatpack_impostor.shader` (+ `.shader_c`),
  `Libraries/Flatpack/Code/FlatpackImpostor.cs`, `Libraries/Flatpack/Editor/FlatpackBaker.cs`,
  `Libraries/Flatpack/Editor/FlatpackQualityCheck.cs`; `Benchmarks/iteration-7-see-through/**`,
  `Benchmarks/README.md`, `README.md`, `docs/ROADMAP.md`
- Testing: editor. Auto bakes of the fence, weed tree, chair and knight (chair and knight unchanged at Low; knight's
  metal solves to 0). Play-mode bench at 6 to 32 radii, face-on and at an angle, real model against the old and new
  card; lit, Reflect and material debug views compared at 6 radii; 2,500-fence GPU field test, two rounds each. Tried
  and dropped (in the benchmark README): alpha-to-coverage, dithering, mip-keyed growth, coverage-preserving mips,
  view snapping, sharper mips. Compiles clean.

## 2026-10-25: Real maps, fly-throughs, graphics levels, bake estimates (iteration 6, part 2)

- Change:
  - `FlatpackSystem.Level` (`FlatpackLevel` Off, Balanced, Performance, Maximum): one global setting for a game's
    graphics presets. Scales every Flatpack Renderer's swap distance (Off: never swap; Performance 0.6x from 3 radii;
    Maximum 0.35x from 1.5 radii); Force Model and Force Card ignore it.
  - Scene optimiser: the summary estimates bake time and added video memory for the chosen quality level (measured per
    level, adjusted for supersampling and self-shadow), refreshes when the Quality picker changes, and says where gains
    are biggest.
  - New `scenes/flatpack_showcase.scene`: 7,966 objects from 36 licensed sbox.game models (forests, rocks, a statue
    plaza, a road with cars). Its bakes aren't in git; run Optimize Scene after cloning.
  - Iteration 6 benchmarks: a real game map (Orion SCP-RP Site 67, local copy, numbers only), frame-by-frame
    fly-throughs on the showcase map and the real map, graphics levels, the brightness match, the chair GIF re-rendered
    from current bakes.
  - README: a distance comparison right under the hero (real tree against card at 6, 12, 24 and 48 radii at true size,
    with what the hybrid draws at each, and 2,500 trees all real against hybrid: 32 to 62 ms against 17 to 21 ms).
  - README: new hero with matched camera, light and brightness (Ultra bake with baked self-shadowing, Sky Occlusion 1;
    wireframe as an inset), turntable GIF from the same bake, On real maps and Graphics settings sections. Marketing
    thumbnails, screenshots and trailer rebuilt from the matched bakes.
- Files: `Libraries/Flatpack/Code/FlatpackSystem.cs`, `Libraries/Flatpack/Code/FlatpackRenderer.cs`,
  `Libraries/Flatpack/Editor/FlatpackSceneOptimizer.cs`; `Assets/scenes/flatpack_showcase.scene` (new);
  `Benchmarks/iteration-6-real-scenes/**`, `Benchmarks/README.md`; `README.md`; `docs/media/*`; `marketing/*`;
  `.gitignore` (showcase bakes)
- Testing: editor. Fly-throughs: 12 recorded runs (showcase 50 s, real map surface and indoors 30 s each, native and
  Flatpack twice, alternating), about 36,000 frames. Real map viewpoints: 80 runs. Graphics levels: 8 play-mode runs
  on the 2,500-tree forest. Brightness measured over the tree's pixels. Optimiser summary read back in the editor.
  Compiles clean.

## 2026-10-25: sbox.game kit from the latest bakes, triangle counts fixed

- Change:
  - `marketing/`: three package thumbnails at the sizes sbox.game asks for (512 x 512, 910 x 512, 512 x 910), three
    1920 x 1080 screenshots (tree real against card, 2,500-tree forest split screen native against Flatpack with GPU
    times, swap-distance chart) and a 19-second 1080p trailer. Page text updated: description, tags, category, longer
    description with the Orion map results. The iteration 2 images are removed.
  - Scene optimiser: counts every draw call of a model's most detailed LOD (`Lod0Triangles`). `GetIndexCountForLod`
    counts only the first, so multi-material models were undercounted (Tree_small_03: 293,609 of 534,024).
  - Docs: Tree_small_01 has 572,544 triangles (was given as 263,066), the wooden chair 912 (was 632, an average of
    rendered triangles with its own LODs). The README hero image still shows the old number until it's re-rendered.
- Files: `marketing/**`; `Libraries/Flatpack/Editor/FlatpackSceneOptimizer.cs`; `README.md`,
  `Benchmarks/iteration-6-real-scenes/README.md`, `Benchmarks/iteration-6-real-scenes/media/swap_distance_gpu.png`
- Testing: editor. Marketing renders made in the editor at 1080p and 70 degrees FOV from the latest Medium and Ultra
  bakes; forest frames in play mode with Flatpack swapping on its own. Triangle counts checked against `Model.MeshInfo`
  for six models. Compiles clean.

## 2026-10-25: Swap where the card looks right, bake check calibrated by eye (iteration 6, part 1)

- Change:
  - Bake check: also measures structural similarity (SSIM, which sees blur). A card holds up where its difference is
    no more than 0.10 above its own floor far away (`HoldsUpExcess`), and no more than 25% of pixels are clearly
    wrong. Calibrated against a person's judgement of the knight (8 radii), tree (6 to 8) and chair (6).
  - Flatpack Renderer: new Swap When It Looks Right (on) and Minimum Swap Radii (6). Auto mode swaps at the bake's
    measured distance instead of a fixed 30 radii; unmeasured bakes keep 30.
  - Auto quality now steps up until the bake holds up from the target distance (capped at 16 radii).
  - New editor API `FlatpackLodModel`: builds a model whose LODs are the source model's first three (original
    materials) and then the card, saves it as a compiled model next to the bake, and loads it back. The engine and the
    Clutter system swap to the card by themselves. No button yet. `FlatpackCard.BuildMesh` exposes the card quad.
  - `Benchmarks/iteration-6-real-scenes` (in progress): the calibration sheet and the swap-distance study against
    native. README: Where to swap section, renderer and settings tables.
- Files: `Libraries/Flatpack/Editor/FlatpackQualityCheck.cs`, `Libraries/Flatpack/Editor/FlatpackBaker.cs`,
  `Libraries/Flatpack/Code/FlatpackRenderer.cs`, `Libraries/Flatpack/Editor/FlatpackLodModel.cs` (new),
  `Libraries/Flatpack/Code/FlatpackCard.cs`; `Benchmarks/iteration-6-real-scenes/**`, `Benchmarks/README.md`,
  `Benchmarks/iteration-5-measure-and-auto/README.md`; `README.md`; `.gitignore` (local Orion test copy)
- Testing: editor. Calibrated check on the seven-model test set: chair 6, knight 8, tree 8, dragon 8, fern 6, weed
  tree 12, fence never. Swap study: 30 play-mode runs, 2,500 copies each. LOD model: built, saved, reloaded with its
  LOD table intact; the engine swaps between 29 and 31 radii as converted; Clutter in play mode draws real models near
  and cards far. Compiles clean.

## 2026-10-24: Supersampled bakes, bake check, Auto quality (iteration 5)

- Change:
  - Bakes render each view 2x larger and average down (new setting Supersample, default 2), so thin parts bake as
    partial coverage.
  - Ultra is now 12 x 12 views at 3072 with 32 search steps (36 MB; was 24 x 24 at 4096, 64 MB). New atlas size 3072.
  - New `FlatpackQualityCheck`: after a bake (setting Measure Quality, on by default) the model and the card are
    rendered in a private scene from four sides at ten distances and compared on shape and colour. The `.fpack` stores
    `QualityLevel` and `HoldsUpFrom`; the Flatpack Renderer's automatic swap distance never goes closer than
    `HoldsUpFrom`, and its inspector shows the level, where it holds up and where it swaps.
  - New quality level Auto (setting Auto Target Radii, default 30): bake at Low, check, step up until the card holds up
    from the target distance outward; if none does, keep the closest.
  - Impostor shader: optional Continuity dial (off by default) that accepts hits where the ray crosses a smooth
    surface between steps; in debug views the card dithers its coverage instead of drawing every partly covered texel,
    so the bake check sees the same shape the game shows.
  - `Benchmarks/iteration-5-measure-and-auto`: error heatmaps, a shape score, a seven-model failure test set, 13 ideas
    tried with results, iteration 4 against 5, the check against the harness, Auto's picks.
- Files: `Libraries/Flatpack/Editor/FlatpackQualityCheck.cs` (new), `Libraries/Flatpack/Editor/FlatpackBaker.cs`,
  `Libraries/Flatpack/Editor/FlatpackBakeSettings.cs`, `Libraries/Flatpack/Editor/FlatpackRendererEditor.cs`,
  `Libraries/Flatpack/Code/FlatpackImpostor.cs`, `Libraries/Flatpack/Code/FlatpackRenderer.cs`,
  `Libraries/Flatpack/Assets/shaders/flatpack_impostor.shader` (and `.shader_c`); `Benchmarks/iteration-5-measure-and-auto/**`,
  `Benchmarks/README.md`; `README.md`; `docs/ROADMAP.md`
- Testing: editor. About 250 harness evaluations on the seven-model set; every idea A/B'd against its own batch's
  Medium bake. Medium 2 to 3.4 points fewer wrong pixels at 1.5 to 4 radii (fence apart); new Ultra 5 to 6 points
  better up close, 3 points worse at 16 radii. Bake check against the full harness: correlation 0.87 over 140 points.
  Auto run on all seven at targets 30 and 8. GPU cost unchanged within noise in a same-session A/B. Compiles clean.
  Not checked by eye: the new inspector line (the screen capture came back blank).
  Dropped after measuring: smooth hit position, thicker shell, facing weights, soft coverage, 128 and 341 px views.

## 2026-10-24: Quality levels and early depth test (iteration 4)

- Change:
  - Quality levels in the bake settings: Low (12 x 12 views, 1024 atlas, 8 search steps), Medium (12 x 12, 2048, 16;
    the default), High (24 x 24, 4096, 16), Ultra (24 x 24, 4096, 32), or Custom. Grid Size, Atlas Size and the new
    Search Steps setting show only for Custom. The baker writes the search steps into the material. Grid Size goes to
    32 in Custom (was 16).
  - Optimize Scene has a Quality picker, shared with the bake window's saved settings.
  - Impostor shader: conservative depth output (`SV_DepthLessEqual`, clamped to the card's own depth), which keeps the
    GPU's early depth test so card pixels hidden behind nearer cards skip the depth search.
  - `Benchmarks/iteration-4-quality-levels`: five models at eight view and atlas combinations, every search step and
    blend setting, a new walk-in test, a dense-forest GPU test, charts, GIFs and distance strips.
  - README: Quality levels section, bake settings table, limits; roadmap updated.
- Files: `Libraries/Flatpack/Editor/FlatpackBakeSettings.cs`, `Libraries/Flatpack/Editor/FlatpackBaker.cs`,
  `Libraries/Flatpack/Editor/FlatpackSceneOptimizer.cs`, `Libraries/Flatpack/Assets/shaders/flatpack_impostor.shader`
  (and `.shader_c`); `Benchmarks/iteration-4-quality-levels/**`, `Benchmarks/README.md`; `README.md`; `docs/ROADMAP.md`;
  `.gitignore` (`Assets/flatpack/sweep/`)
- Testing: editor. 66 bakes and about 140 image evaluations. Early depth A/B in one play session, two alternating
  rounds: 6 to 29% less GPU time in all six cases; distance ladder identical before and after. All four levels baked
  through the settings path the UI uses and checked in the materials (search steps 8, 16, 16, 32). Bake window and
  Optimize Scene opened in the editor: Quality shows, Custom reveals the three fields. Compiles clean. A bake window
  left open across the code reload logged a HideIf warning every frame until reopened; fresh windows don't.

## 2026-10-24: Optimize Scene window and roadmap

- Change:
  - New window, Editor > Flatpack > Optimize Scene. It lists every model in the open scene with copies, triangles and
    bake status, and recommends each one with a reason: Recommended (big saving, with a warning for thin parts),
    Low gain (cheap already), Skip (animated, already a flat quad, tiny) or Done. Recommended models start ticked;
    Select Recommended / All / None and Rescan sit above the list.
  - **Flatpack Selected** bakes the models that have no bake yet, using the bake window's saved settings, then adds
    Flatpack to every copy as one undo step. **Remove Flatpack from Scene** removes every Flatpack component as one
    undo step.
  - Bakes now record how much coverage thin parts lose at range (`FlatpackImpostor.ThinDetail`), which the optimiser
    uses for its thin-part warning.
  - `docs/ROADMAP.md`: ranked plan from research into other impostor tools, papers and unused s&box engine features.
  - README: one-click section for the optimiser; roadmap link.
- Files: `Libraries/Flatpack/Editor/FlatpackSceneOptimizer.cs` (new), `Libraries/Flatpack/Code/FlatpackImpostor.cs`,
  `Libraries/Flatpack/Editor/FlatpackBaker.cs`; `docs/ROADMAP.md` (new); `README.md`
- Testing: editor. On a discarded copy of the benchmark scene the window recommended the tree, knight and horse
  statue, marked the rock and a low-poly chair low gain, and skipped the animated citizen and the ground plane.
  Flatpack Selected added 10 components; undo removed them; applying again and Remove both worked, and undoing Remove
  brought all 10 back. Compiles clean.

## 2026-10-24: Ten-model test, close-range and thin-part fixes, Benchmarks by iteration

- Change:
  - Tested ten public sbox.game models (trees, fern, statues, chair, fence, car, cactus, rock) with a harness that
    scores a camera orbit and a 1.5 to 30 radii distance ladder against the real model at its top LOD.
  - Card placement: up close the card backs off from the near plane and is sized to the bounding sphere's silhouette.
    Before, it was clipped away below about 2 radii (rock at 1.5 radii: 95% of pixels wrong, now 5%).
  - Thin parts at range: the baker measures how much each mip level loses coverage and writes per-mip alpha
    corrections (`g_vFlatpackMipAlpha0/1`) into the material; the shader applies them. The depth search reads the mip
    matching the pixel's footprint, so small cards stop sparkling.
  - New material option Miss Fallback (off by default): tested on, it raised errors on every model but the weed tree.
  - Benchmarks reorganised into one folder per iteration (`Benchmarks/iteration-1-first-shader`,
    `iteration-2-stable-shader`, `iteration-3-ten-models`), each with a README, `data/` and `media/`. Renders of
    unlicensed models moved to git-ignored `local/` folders; `Benchmarks/raw/` is gone.
  - README: ten-model comparison grid, before/after flicker GIF and chart, and a "how close can you get" table.
- Files: `Libraries/Flatpack/Assets/shaders/flatpack_impostor.shader`, `Libraries/Flatpack/Editor/FlatpackBaker.cs`;
  `Assets/flatpack/*` (re-baked); `Benchmarks/**`; `README.md`; `.gitignore`
- Testing: editor. Ten models plus Tree_small_01 measured before and after (`data/eval_round1.jsonl`,
  `data/eval_final.jsonl`): equal or better at every distance except the fence past 12 radii. Before/after orbit with
  the iteration 1 shader on its own 8 x 8 bake: wooden chair difference from the model 8.51 to 3.88, frame-to-frame jump
  9.55 to 5.33. The fence and the weed tree's twigs remain weak (documented).

## 2026-10-24: Marketing kit for the sbox.game page

- Change: `marketing/` holds what the package page needs: a 1024 x 1024 thumbnail (tree beside its card, "263,066
  faces" against "1 face"), a 1920 x 1080 cover (256 Flatpack trees, 926 triangles and 20 draw calls on screen), the
  before/after and forest screenshots, the turntable GIF, and `README.md` with the title, two summaries, the page
  description, tags, alt text and where each image goes. Every image uses only the CC BY tree, credited on the image.
- Files: `marketing/*`
- Testing: editor. The forest shot is a fresh play-mode render with the current shader; the counts come from
  `perf_rendering` for that frame.

## 2026-10-24: Stable impostors (flicker and ghosting fixes), README

- Change: the card flickered and showed double images while the camera circled it. We built an orbit test (camera
  circling a model and its card in 3 degree steps, scoring frame-to-frame popping and card-vs-model difference) and
  fixed what it found:
  - Depth search: each of the three blended views now traces the pixel's view ray through its baked depth (16 point
    sampled steps plus 4 binary refinements) instead of taking one parallax step, so the views land on the same
    surface and stop doubling. A shell thickness stops rays from catching the steep walls at silhouettes (streaks).
    The step count drops as the card shrinks on screen.
  - Atlas size bug: with a grid that doesn't divide the atlas (2048 / 12) the baker packed 170-pixel frames while the
    shader expected 170.67, so frames drifted across the atlas. The baker now shrinks the atlas to a whole number of
    frames (2040 for 12 at 2048).
  - Default grid 12 (views 15 degrees apart instead of 25), plain barycentric blend weights (sharper weights popped
    more), alpha-to-coverage edges in the forward pass.
  - Tighter bounds: Ritter bounding sphere from the vertices instead of the box half diagonal, so the object fills more
    of each frame. Padded background texels now bake at the far plane so the search passes through them.
  - Optional baked sky visibility (bake setting "Bake Self Shadow", material slider "Sky Occlusion"), off by default:
    it darkened enclosed parts the real model shows lit.
  - The Flatpack Renderer inspector no longer lists internal members.
  - README with a wireframe before/after hero and a turntable GIF of the CC BY tree; benchmark file rerun.
- Files: `Libraries/Flatpack/Assets/shaders/flatpack_impostor.shader`, `Libraries/Flatpack/Editor/FlatpackBaker.cs`,
  `FlatpackBakeSettings.cs`, `FlatpackRendererEditor.cs`; `Libraries/Flatpack/Code/FlatpackMath.cs`;
  `Libraries/Flatpack/UnitTests/FlatpackMathTests.cs`; `Assets/flatpack/*` (re-baked); `README.md`, `docs/media/*`;
  `Benchmarks/2026-10-24_phase2.md`
- Testing: editor. Orbit test, office chair: card vs model difference 7.02 before, 3.73 after; on all four models
  tested the card now flickers less than the real model (chair 7.65 vs 8.76, SCP-173 2.79 vs 3.44, tree 10.43 vs
  13.93, bed 18.36 vs 18.37). Benchmark rerun: 100 hospital beds 52.3 ms to 1.6 ms GPU. `dotnet test` passes 7 of 7.
  Still visible up close: softness from 170-pixel frames at 2048, metal parts lit as plastic.

## 2026-10-24: Test suite, benchmark, sharper atlases, one-click setup

- Change:
  - Sharpness: texture streaming kept impostor atlases at 256 to 512 pixels, because it can't tell how much of the
    atlas the card shows. `FlatpackSystem` now counts visible cards per impostor and calls `Texture.MarkUsed` on
    their atlases every frame (`FlatpackImpostor.MarkAtlasesUsed`). The shader takes mip gradients from the
    pre-parallax UV, and only applies the parallax step where the first lookup lands on the object, which removed
    floating leaf fragments on foliage.
  - One model, one click: Flatpack Renderer now finds a baked impostor for its model by itself, picks its swap
    distance from the object's size by default (`AutoSwapDistance`, 30 bounding radii), and has a "Bake Impostor"
    button in its inspector that bakes and assigns. The bake window gained "Apply to Scene", which adds a Flatpack
    Renderer to every object whose model has a bake, as one undo step.
  - Test suite: `scenes/flatpack_suite.scene` puts five high-poly props from Orion SCP-RP (installed from sbox.game,
    nothing copied from the Orion repo) beside their cards: hospital bed (974,642 triangles), small tree (263,066),
    office chair (15,378), SCP-173 (6,630), tall plant (1,832). `scenes/flatpack_benchmark.scene` holds the grid
    benchmark; results in `Benchmarks/2026-10-24_phase2.md`. Turntable GIFs went to `Benchmarks/raw/media/`.
  - The bed, chair and statue packages have no licence set on sbox.game, so `.gitignore` keeps their bakes
    (`Assets/flatpack/suite/`) and the GIFs out of the repo until we check rights.
  - Kept the user's own edits from publishing the testbed: `flatpack_testbed.sbproj` Org `anakin` plus the
    RecommendedMin/MaxPlayers, Coop and Versus keys, and `Libraries/Flatpack/flatpack.sbproj` Org `anakin` with
    the editor's default keys.
- Files: `Libraries/Flatpack/Code/FlatpackRenderer.cs`, `FlatpackImpostor.cs`, `FlatpackSystem.cs`;
  `Libraries/Flatpack/Editor/FlatpackRendererEditor.cs`, `FlatpackBakeWindow.cs`;
  `Libraries/Flatpack/Assets/shaders/flatpack_impostor.shader`; `Assets/scenes/flatpack_suite.scene`,
  `Assets/scenes/flatpack_benchmark.scene`; `Benchmarks/2026-10-24_phase2.md`; `.gitignore`;
  `flatpack_testbed.sbproj`, `Libraries/Flatpack/flatpack.sbproj`
- Testing: editor. Compiles with 0 errors and 0 warnings. After the fix the atlases read back at 2048 on the GPU.
  Adding a Flatpack Renderer to an office chair found `office_chair.fpack` and chose 1,116 units with no setup. The
  inspector resolves to `FlatpackRendererEditor`. Benchmark: 100 hospital beds go from 47.1 ms GPU to 1.5 ms; full
  table in the benchmark file. Not clicked by hand: the inspector buttons and "Apply to Scene" (code paths only).

## 2026-10-24: Phase 2, impostor baker, shader and runtime

- Change: Flatpack now bakes a model into an impostor and draws it.
  - Baker (`FlatpackBaker`): renders the model alone in a throwaway editor scene with an orthographic camera, one
    frame per cell of an N x N grid (hemisphere, full sphere or cylinder layout). It reads albedo, world normals,
    roughness and AO through the camera debug views, which write material data with no lighting or tonemapping.
    We checked the byte encoding of each view against known colours first. s&box has no depth view, so a bake-only
    shader writes linear depth as albedo. The baker grows colour past each silhouette inside its frame (edge
    padding), then writes `<name>_fp_albedo.png`, `<name>_fp_normal.png` (normal plus depth in alpha),
    `<name>_fp_rao.png`, `<name>_fp.vmat` and `<name>.fpack`. It yields between frames and takes a cancellation token.
  - Bake window (Editor menu, Flatpack, Impostor Baker): model list, settings (grid 4 to 16, atlas 512 to 4096,
    layout, Z-locked card, roughness/AO pass, edge padding, output folder), progress bar, cancel, "Add Selected".
    Settings persist in an editor cookie. Right-click a model in the asset browser for "Bake Flatpack Impostor".
    Right-click an object in the scene view to bake its model and add a Flatpack Renderer with the result.
  - Shader (`flatpack_impostor.shader`): the vertex shader turns a quad into a card on the front of the bounding
    sphere, facing the camera or locked to the object's Z axis. The pixel shader blends the three nearest frames
    with barycentric weights, takes one depth parallax step per frame, rebuilds the surface point, writes it as
    `SV_Depth`, rotates the baked object-space normals by the instance transform and shades through
    `ShadingModelStandard`. Forward and Depth modes, so it casts and receives shadows. Options: alpha clip,
    parallax strength, nearest-frame-only, metalness, depth-pass bias, dither fade input, a UV/weights debug view.
  - Runtime: `FlatpackImpostor` (`.fpack` GameResource) holds bounds, pivot, grid, layout and material, and builds
    one shared card model. `FlatpackRenderer` swaps the real model and the card at `SwapDistance` with a
    hysteresis band and an optional dither cross-fade. Optional `Levels` swap the source renderer's model through
    a mesh LOD chain before the card. With no ModelRenderer the card shows at every distance. `FlatpackSystem`
    runs one time-sliced distance pass per frame for every renderer in the scene.
  - Bugs found and fixed in the editor: `SV_Depth` needs the viewport depth range remap (`g_flViewportMinZ/MaxZ`),
    and the shader must declare `BoolAttribute( alphatest, true )`, or the shadow pass skips the pixel shader and
    draws the whole quad.
  - Removed the template placeholders (`MyLibraryComponent.cs`, `MyEditorMenu.cs`, `library_material.vmat`,
    `LibraryTest.cs`). Added `FlatpackMathTests` for the frame layout maths.
  - Testbed: `scenes/flatpack_test.scene` (sun, ground, a real chair beside a Flatpack chair) and test bakes of
    `chair01` and `crate01` in `Assets/flatpack/`. `.gitignore` now skips `*.scene_d`.
- Files: `Libraries/Flatpack/Code/FlatpackMath.cs`, `FlatpackImpostor.cs`, `FlatpackCard.cs`, `FlatpackRenderer.cs`,
  `FlatpackSystem.cs`; `Libraries/Flatpack/Editor/FlatpackBaker.cs`, `FlatpackBakeSettings.cs`,
  `FlatpackBakeWindow.cs`, `FlatpackMenus.cs`; `Libraries/Flatpack/Assets/shaders/flatpack_impostor.shader`,
  `flatpack_bake_depth.shader`; `Libraries/Flatpack/UnitTests/FlatpackMathTests.cs`;
  `Assets/scenes/flatpack_test.scene`; `Assets/flatpack/*`; `.gitignore`
- Testing: editor. Both shaders and both assemblies compile with 0 errors and 0 warnings. Baking `chair01` takes
  0.8 s at 1024. The bake window baked `chair01` and `crate01` in one batch. In `flatpack_test.scene` the card matches
  the model's silhouette, lighting and cast shadow from the side, above and at 700 units. Play mode confirmed the
  swap points: LOD1 at 250 going out and 150 coming back, the card at 450 and 350, the source hidden after a
  0.3 s fade. `dotnet test` passes 6 of 6. Not exercised by hand: clicking the menu items and buttons
  (scripted calls only), the cylinder and sphere layouts, Z-locked cards, 4096 atlases.

## 2026-10-24: Library created

- Change: created the Flatpack library at `Libraries/Flatpack/` (title "Flatpack", ident
  `flatpack`, type `library`). `sbox-cli --new` hides the `library.minimal` template, so we ran
  the Library Manager's own "New Library" routine (`LibraryManagerDock.CreateNewLibrary`) through
  `run_csharp`. That routine copies `templates/library.minimal` and registers the folder with
  `LibrarySystem.Add`, the same path as the dock's button. The template's sample component, editor
  menu and material stay in place until the Phase 2 code replaces them. Org is still `local`.
- Files: `Libraries/Flatpack/flatpack.sbproj`, `Libraries/Flatpack/Code/MyLibraryComponent.cs`,
  `Libraries/Flatpack/Editor/MyEditorMenu.cs`, `Libraries/Flatpack/UnitTests/*.cs`,
  `Libraries/Flatpack/Assets/Materials/library_material.vmat`, `ProjectSettings/*.config`
  (written by the editor on first open)
- Testing: editor. `LibrarySystem.All` lists the library, and the editor compiled
  `local.flatpack` and `local.flatpack.editor` with 0 errors and 0 warnings.

## 2026-10-24: Project setup

- Change: s&box loads libraries from a host project's `Libraries/<ident>/` folder and publishes
  them from the Library Manager. The repo root is now that host: "Flatpack Testbed"
  (`flatpack_testbed`), created from the `game.minimal` template with `sbox-cli --new` and moved
  up from its generated subfolder. The testbed holds test scenes and benchmarks and never ships.
  The library (`Libraries/flatpack/`, ident `flatpack`) comes next, from the editor's Library
  Manager. We deleted a first `addon.minimal` root project before anyone opened or committed it,
  because its `flatpack` ident would clash with the library.
  We merged the template `.gitignore` with three Flatpack additions (`.bin/`, `*.csproj.user`,
  `Benchmarks/raw/`) and added the MIT licence in Anakin Studio's name.
- Files: `flatpack_testbed.sbproj`, `.editorconfig`, `.gitignore`, `Assets/scenes/minimal.scene`,
  `Code/Assembly.cs`, `Editor/Assembly.cs`, `LICENSE`, `CHANGELOG.md`
- Testing: static. Project not yet opened in the editor.
