# Changelog

Flatpack by Anakin Studio. Newest entries first.

Testing levels: **static** (read through, not compiled), **compiled** (editor compile succeeded),
**editor** (exercised in the running s&box editor).

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
