# Changelog

Flatpack by Anakin Studio. Newest entries first.

Testing levels: **static** (read through, not compiled), **compiled** (editor compile succeeded),
**editor** (exercised in the running s&box editor).

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
