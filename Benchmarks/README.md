# Benchmarks

One folder per iteration of Flatpack's quality and performance work, oldest first. Each has a README with what was
measured, how, and what changed; `data/` holds the raw numbers and `media/` the images. `local/` folders (not in git)
keep renders of models whose sbox.game licence doesn't allow publishing them.

| Iteration | What it covers | Headline |
|---|---|---|
| [1. First shader](iteration-1-first-shader/README.md) | First working baker and shader, GPU benchmark | 100 hospital beds 47.1 ms to 1.5 ms GPU; cards ghosted and popped while turning |
| [2. Stable shader](iteration-2-stable-shader/README.md) | Depth ray march, atlas sizing fix, 12 x 12 views, rerun benchmark | Office chair difference from the model 7.02 to 3.73; beds 52.3 ms to 1.6 ms |
| [3. Ten models](iteration-3-ten-models/README.md) | Ten licensed models, how close you can get, before/after flicker proof | Solid props hold up from 1.5 to 4 radii, foliage from 6 to 8; chair error 8.5 to 3.9 across a full orbit |
