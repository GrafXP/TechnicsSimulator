# Handoff: Mechanics panel usability + drivetrain isolation

> **Resolved follow-up (2026-07-20):** Manual testing confirmed that isolation repaints
> correctly; the earlier failure was limited to the automated screenshot harness. The actual
> follow-up issues are now fixed: transparent context uses single-pass weighted OIT, opacity
> changes no longer rebuild the scene, mechanics rows zoom to their combined instance bounds,
> and viewport gestures are right-drag rotate / middle-drag pan / wheel zoom. The solution builds
> cleanly and all 206 tests pass. The investigation below is retained as historical context.

## Original request
In the viewer's **Mechanics** tab, the user sees a list of detected mechanical parts (gear
meshes, clutches, drivers, boundaries). Problems they reported:
1. Clicking a row didn't highlight anything.
2. Models are complex; you can't find the tiny detected part in the 3D view.
3. Wanted: click a detected part → that part highlights, all *other* mechanical parts stay 100%
   visible, and all non-mechanical parts fade to ~80% transparent, so only the drivetrain is
   prominent.

## Status: ~90% done, ONE rendering bug unresolved on large models

All logic works and all **205 tests pass** (162 core + 43 WPF). The scene graph is rebuilt
**correctly** — proven with a red-probe material and probe logging. The one remaining problem is
that the viewport **does not auto-repaint** after the rebuild on the large model **42100** (works
fine on 8275).

## What was implemented (in working tree, NOT yet `git commit`ed)

**Files changed:**
- `src/TechnicsSim.Wpf/Rendering/ISceneRenderer.cs` — `Highlight` now takes
  `IReadOnlyCollection<string>`; added `SetMechanicalInstances`, `EmphasizeMechanics`,
  `GhostOpacity`.
- `src/TechnicsSim.Wpf/Rendering/HelixSceneRenderer.cs` — the bulk. `RebuildInstanceModels()`
  splits each batch into solid vs faded (ghost) instances; highlighted instances drawn as a
  solid bright-cyan copy of their own geometry (`RebuildHighlight`); `CreateGhostMaterial`
  desaturates + alpha-fades; ghost/context geometry is `IsHitTestVisible=false` so clicks pass
  through. Geometry buffers cached in `_buffers` across rebuilds.
- `src/TechnicsSim.Wpf/ViewModels/MainViewModel.cs` — `EmphasizeMechanics`, `GhostOpacity`
  (default **0.2**), `HasMechanicalInstances`; `HighlightFromPanel` now auto-enables isolation;
  computes mechanical set on load.
- `src/TechnicsSim.Wpf/ViewModels/MechanicsPanelViewModel.cs` — `SelectedRow` now maintains a
  **single selection across all four sections** and sets `IsSelected`; highlight callback is
  `Action<ImmutableArray<string>>`.
- `src/TechnicsSim.Wpf/ViewModels/MechanicsRows.cs` — added `IsSelected` +
  `HighlightInstanceIds` (plural). **A mesh row highlights BOTH gears.**
- `src/TechnicsSim.Wpf/MainWindow.xaml` — "Isolate drivetrain" checkbox + "Context" opacity
  slider in toolbar; row cards show selected styling via `DataTrigger` on `IsSelected`;
  `PreviewMouseLeftButtonDown="OnMechanicsRowClick"` on the ScrollViewer.
- `src/TechnicsSim.Wpf/MainWindow.xaml.cs` — `OnMechanicsRowClick` resolves the clicked row's
  DataContext and sets `SelectedRow`.
- `src/TechnicsSim.Mechanics/Shafts/ShaftModel.cs` — `ShaftGraph.MechanicalInstanceIds()`:
  shafts + gears + drivers + unsupported + uncatalogued, **excluding bearings** (bearings are the
  chassis a shaft turns in; including them re-solidifies most of the model).
- Tests updated in all three test files (fake renderer, panel tests, `ShaftGraphTests`).

## The unsolved bug — read carefully

**Symptom:** After `RebuildInstanceModels()` replaces `_sceneRoot`'s children, the viewport keeps
drawing the *previous* models until some unrelated event forces a repaint. On **8275 it repaints
automatically** (verified: isolate works via both row-click and checkbox). On **42100 it does
not** — the view stays unchanged until you toggle the Diagnostics checkbox, after which the
isolation renders **perfectly** (dramatic ghost effect, drivetrain standing out — confirmed by
screenshot).

**Two separate rendering bugs were found; ONE is fixed and confirmed, ONE is still open:**
1. FIXED & CONFIRMED: `GroupModel3D.Children.Clear()` raises a single `Reset` with no `OldItems`,
   so child SceneNodes never detach and the replacements never render. Replaced with `RemoveAll()`
   (removes one-by-one). Reverting to `Clear()` reproduces a stale viewport *even with correct
   invalidation* — this was directly tested.
2. OPEN: forcing the repaint. Tried, none worked on 42100: `_viewport.InvalidateRender()`,
   `_viewport.InvalidateSceneGraph()`, `_sceneRoot.InvalidateRender()`, both combined, and the
   current `_viewport.RenderHost?.InvalidatePerFrameRenderables()` (the `RequestRedraw()` method).
   Setting a group's `IsRendering` (what the Diagnostics toggle does) is the *only* thing observed
   to reliably force it.

**Current code state:** `RequestRedraw()` calls `_viewport.RenderHost?.InvalidatePerFrameRenderables()`.
It builds, but does **not** fix 42100.

**CRITICAL UNTESTED HYPOTHESIS — check this FIRST:** The screenshots are fully automated and
generate **no mouse movement or WPF events** after the toggle click. HelixToolkit's render host
may render on-demand and simply be **idle**. On 8275 the loop happened to wake; on the heavier
42100 it may need a real event. **Before writing more invalidation code, manually launch 42100,
toggle "Isolate drivetrain", and move the mouse over the viewport** — the effect may already
appear in real interactive use. If so, the fix is just to wake the render loop (investigate
`RenderHost.IsRendering` / `StartRendering()`, or the render-detection/on-demand mode), not the
invalidation type.

`InvalidateTypes` enum values: `Render=0, SceneGraph=1, PerFrameRenderables=2`. `IRenderHost` also
has `UpdateAndRender()`, `StartRendering()`, `IsRendering`, `Invalidate(InvalidateTypes)`.

## How to reproduce / debug (the harness that worked)
Scripts were in the session scratchpad (`shot.ps1` screenshot by pid, `click.ps1` window-relative
click, `diff.ps1` viewport pixel diff where 0% = no change). Launch:
`TechnicsSim.Wpf.exe --model Models/42100-1.mpd`.

**Red-probe technique** (very effective): set `CreateGhostMaterial`'s `DiffuseColor` to opaque
red — if the model stays its normal color after a rebuild, the frame didn't update; if it goes
red, it did. Probe logging via `System.IO.File.AppendAllText` inside `RebuildInstanceModels`
confirmed the graph is correct (159 ghost models / 1625 mechanical / 264 children on 42100).

## Loose ends
- `Models/42121-1.mechanics.json` (untracked) predates this task — not mine, leave it.
- Verify isolate + highlight on 42055 and 42121 too (only 8275 and 42100 were exercised).
- All probe/debug code has been removed from the source; verified clean via grep.

## Bottom line
Everything works on 8275. For 42100 the feature is fully built and correct — it only fails to
*auto-repaint* in the automated harness. Start by checking whether it repaints in real interactive
use (mouse move); that determines whether this is a genuine bug or a harness artifact.
