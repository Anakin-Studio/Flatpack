# Flatpack

**Impostors for s&box, by Anakin Studio.** Flatpack bakes a high-poly model into a single camera-facing card that
looks like the model from every angle, lights like it, and casts its shadow.

![A 263,066-face tree beside its Flatpack card: one face](docs/media/flatpack_hero.png)

Far from the camera, a 263,066-face tree and a 2-triangle card look the same, and the card costs almost nothing to draw.
Flatpack renders the model from 144 directions into texture atlases, then draws one quad that picks the three views
nearest yours, traces into their baked depth so they line up, and blends them as you walk around it.

![The tree and its card rotating side by side](docs/media/flatpack_turntable.gif)

## What it saves

100 hospital beds (974,642 triangles each) on an RTX 3090, everything else in the scene held the same:

| | GPU time | Triangles drawn |
|---|---:|---:|
| Real models | 52.3 ms | 326,505,084 |
| Flatpack cards | 1.6 ms | 706 |

| Model (instances) | GPU ms, model | GPU ms, cards |
|---|---:|---:|
| Hospital bed, 974,642 tris (100) | 52.29 | 1.63 |
| Small tree, 263,066 tris, close (100) | 10.23 | 4.21 |
| Office chair, 15,378 tris (400) | 4.11 | 1.51 |
| SCP-173 statue, 6,630 tris (400) | 3.16 | 1.72 |
| Tall plant, 1,832 tris (400) | 1.75 | 1.70 |
| Small tree, far, own billboard LOD active (100) | 1.25 | 1.88 |

The empty scene costs 1.30 ms. Cards cost about the same whatever they replace, so the saving grows with the source's
triangle count. Below a couple of thousand triangles, or when a model already ends in a cheap billboard LOD, a card
won't beat it. Method and raw numbers: [`Benchmarks/2026-10-24_phase2.md`](Benchmarks/2026-10-24_phase2.md).

## Install

Flatpack is an s&box library (`anakin.flatpack`). In the editor, open **View > Library Manager**, find Flatpack and
install it. To work from source, clone this repo and copy `Libraries/Flatpack` into your project's `Libraries` folder.

## Give one model a Flatpack LOD

1. Select the object with the model in your scene.
2. Add the **Flatpack Renderer** component.
3. Press **Bake Impostor** in its inspector.

The bake takes a few seconds and writes the atlases, a material and a `.fpack` resource next to
the model (or in `Assets/flatpack/` for models from sbox.game). The component picks the swap distance from the model's
size: the card takes over at 30 bounding radii, when the object is a few percent of the screen tall.

Up close nothing changes: your `.vmdl`'s own LODs keep working, and the card is the last step after them. Every other
object using the same model finds the bake on its own once you add the component.

Other ways in:

- Right-click a model in the asset browser: **Bake Flatpack Impostor**.
- Right-click an object in the scene view: bakes it and adds the Flatpack Renderer for you.
- **Editor > Flatpack > Impostor Baker** opens the bake window for a list of models. **Apply to Scene** then adds a
  Flatpack Renderer to every object in the open scene whose model has a bake, as one undo step.

## Flatpack Renderer

| Property | What it does |
|---|---|
| Impostor | The baked `.fpack`. Leave it empty to use any bake of the source model. |
| Source | The real model's renderer. Found on the same object if empty. |
| Auto Swap Distance | Swap at Auto Distance Factor bounding radii (default 30). Off: use Swap Distance. |
| Swap Distance | Camera distance where the card takes over, in world units. |
| Hysteresis | Dead band around the swap distance, so a camera hovering at the boundary doesn't flicker. |
| Cross Fade, Fade Time | Dither the card in over the model instead of popping. |
| Levels | Optional extra models (lower-poly versions) to swap in by distance before the card. |
| Mode | Auto, or force the model or the card (handy for checking a bake). |

A Flatpack Renderer with no model renderer beside it shows the card at every distance.

`FlatpackSystem` checks every renderer's distance in one time-sliced pass per frame, so 5,000 trees cost one loop, and
all cards from the same bake share one model and material so the engine can instance them.

### Card as the last LOD of a .vmdl

If you'd rather use the engine's LOD switching (no hysteresis, no fade), add a LOD to the model in ModelDoc whose only
mesh is a quad with the impostor material and UVs from (0,0) top-left to (1,1) bottom-right. The shader ignores the
quad's position and size and builds the card from the bake data and the instance transform.

## Bake settings

| Setting | Default | Notes |
|---|---|---|
| Grid Size | 12 | Views per atlas side. 12 gives 144 views, 15 degrees apart. |
| Atlas Size | 2048 | 4096 for objects you'll see up close. |
| Layout | Hemisphere | Hemisphere for anything on the ground, Sphere for objects seen from below, Cylinder for classic tree billboards. |
| Z-Locked Card | off | Card only turns around the object's up axis. Always on for Cylinder. |
| Bake Roughness Ao | on | Roughness and material AO atlas. |
| Bake Self Shadow | off | Bakes how much sky each part sees; see Sky Occlusion below. Triples bake time. |
| Edge Padding | 16 | Texels of colour grown past each silhouette, so mipmaps don't halo. |
| Output Folder | next to the model | Relative to `Assets`. |

The impostor material (`<name>_fp.vmat`) has a few dials of its own: **Search Steps** (depth trace quality, default
16), **Thickness**, **Blend Sharpness**, **Alpha Clip**, **Metalness**, **Shadow Bias** and **Sky Occlusion** (darkens
enclosed parts using the self-shadow bake, for dense foliage).

## How it works

**Bake.** Flatpack opens a private scene with nothing in it but your model and an orthographic camera, and renders it
from each grid direction through s&box's debug views: albedo, world normals, roughness and AO come out as raw material
data with no lighting or tonemapping. A small helper shader writes linear depth. The frames go into three atlases, and
the baker grows colour into the empty space around each silhouette so filtering can't pull in the background.

**Draw.** The vertex shader turns the quad toward the camera, on the front face of the model's bounding sphere. The
pixel shader picks the three baked views nearest your view direction, with barycentric weights. For each one it traces
the pixel's view ray through that view's depth map until it hits the baked surface, so all three land on the same
point of the model and agree. It blends them, rotates the baked normals by the object's rotation, writes the real
surface depth, and shades the result with the standard s&box lighting, shadows and fog.

## Limits

- Metal parts bake as plain colour, so they light like painted plastic. s&box has no metalness debug view to read
  from. Each material has one Metalness slider for the whole object.
- Up close the card is only as sharp as one frame: about 170 pixels across at 2048 with a 12 x 12 grid. Use 4096 for
  objects the camera gets near.
- Hemisphere bakes can't show an object from below. Use Sphere for those.
- Foliage shaders with translucency or wind lose those effects on the card. The card uses standard lighting.
- Very thin parts (wheels, wires) can lose pixels when the three views disagree about them.

## This repository

The repo root is **Flatpack Testbed**, an s&box project that hosts the library during development. It is never
published.

- `Libraries/Flatpack/`: the library (`Code`, `Editor`, `Assets/shaders`, unit tests).
- `Assets/scenes/flatpack_test.scene`: a real chair beside a Flatpack chair.
- `Assets/scenes/flatpack_suite.scene`: five high-poly props from sbox.game beside their cards. Re-bake them from the
  Flatpack Renderer inspector after cloning; their bakes aren't in git.
- `Assets/scenes/flatpack_benchmark.scene`: the grid used for the numbers above.
- `CHANGELOG.md`: every change, with how it was tested.

## Credits

Flatpack by [Anakin Studio](https://github.com/Anakin-Studio), MIT licence.

Tree_small_01 in the images by Small Box Studio
([sbox.game/smallboxstudio/treesmall01](https://sbox.game/smallboxstudio/treesmall01)), CC BY 4.0.
