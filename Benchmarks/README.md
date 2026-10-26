# Benchmarks

One folder per iteration of Flatpack's quality and performance work, oldest first. Each has a README with what was
measured, how, and what changed; `data/` holds the raw numbers and `media/` the images. `local/` folders (not in git)
keep renders of models whose sbox.game licence doesn't allow publishing them.

| Iteration | What it covers | Headline |
|---|---|---|
| [1. First shader](iteration-1-first-shader/README.md) | First working baker and shader, GPU benchmark | 100 hospital beds 47.1 ms to 1.5 ms GPU; cards ghosted and popped while turning |
| [2. Stable shader](iteration-2-stable-shader/README.md) | Depth ray march, atlas sizing fix, 12 x 12 views, rerun benchmark | Office chair difference from the model 7.02 to 3.73; beds 52.3 ms to 1.6 ms |
| [3. Ten models](iteration-3-ten-models/README.md) | Ten licensed models, how close you can get, before/after flicker proof | Solid props hold up from 1.5 to 4 radii, foliage from 6 to 8; chair error 8.5 to 3.9 across a full orbit |
| [4. Quality levels](iteration-4-quality-levels/README.md) | Views vs pixels vs search steps, flicker, walk-in, dense-forest GPU test, early depth test | More views don't reduce flicker (card already matches the model's); four quality levels; 6 to 29% less GPU time |
| [5. Measure and Auto](iteration-5-measure-and-auto/README.md) | Error heatmaps, shape score, seven-model failure test set, 13 ideas tried, supersampled bakes, in-editor bake check, Auto quality | Medium 2 to 3 points fewer wrong pixels up close; Ultra sharper at 36 MB instead of 64; Auto picks Low (4 MB) for most models |
| [6. Real scenes](iteration-6-real-scenes/README.md) | Bake check calibrated by eye, where to swap against native, a real game map, frame-by-frame fly-throughs | Tree forest 28.8 ms native to 10.0 ms; real map surface 30.3 ms to 22.5 ms average along a fly-through; little change in closed rooms |
| [7. See-through detail](iteration-7-see-through/README.md) | Why wire mesh failed, fixes tried, see-through detection and fitting, baked metalness, Ultra for see-through | Fence holds up from 6 radii instead of never, coverage within 3.3 points of the real fence; wires light right (63 against 65, were 101); no GPU gain on a 538-triangle fence |
