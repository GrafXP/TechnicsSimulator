# TechnicsSimulator

TechnicsSimulator is an experimental kinematic simulator for LEGO Technic models stored in the [LDraw](https://www.ldraw.org/) format.

Most LDraw applications stop at rendering. This project aims to go further: infer axles, bearings, keyed connections, shafts, and gear meshes from model geometry, then propagate rotation through the reconstructed drivetrain with explainable gear ratios.

> [!IMPORTANT]
> Phases 0 and 1 are complete: the LDraw parser, resolver, expander, geometry pipeline, audit CLI, and an interactive WPF viewer with part selection all work. Mechanism reconstruction — snap features, shafts, gears, and animation — has not started.

## Project goals

The first useful release will:

- Load and render the three supplied MPD models efficiently.
- Inspect logical parts, connection features, shafts, bearings, and candidate gear meshes.
- Explain where each inferred connection came from and how confident it is.
- Let the user choose a driving shaft or motor output.
- Animate reviewed rotary gear paths with exact, auditable ratios.
- Clearly identify ambiguous and unsupported mechanisms instead of silently guessing.

This is initially a **kinematic** simulator. It will model positions and velocity relationships, not mass, torque, friction, gravity, backlash, or material deformation.

## How it is intended to work

```mermaid
flowchart LR
    MPD[LDraw MPD model] --> Resolver[LDraw parser and resolver]
    Parts[Official LDraw library] --> Resolver
    Resolver --> Geometry[Meshes and logical part instances]
    Geometry --> Viewer[WPF 3D viewer]

    Shadow[LDCad Shadow Library] --> Features[Effective snap features]
    Resolver --> Features
    Features --> Mates[Span-aware feature mating]
    Catalog[Mechanics catalog and model sidecars] --> Graph[Shaft and gear graph]
    Mates --> Graph
    Graph --> Solver[Ratio and conflict solver]
    Solver --> Viewer
```

LDraw type-1 references contain transforms and geometry names, but they do not say which axle drives which gear. The [LDCad Shadow Library](https://github.com/RolandMelkert/LDCadShadowLibrary) adds snapping metadata for many official parts. TechnicsSimulator will combine that metadata with a reviewed mechanics catalog and optional per-model corrections.

Automatic inference is deliberately conservative. For example, an axle-shaped shaft in an axle-shaped hole is a high-confidence keyed connection, while a round pin in a round hole may be either structural or an intentional hinge.

## Supplied models

The repository includes four LDraw MPDs in [Models/](Models/):

| Model | MPD sections | Source type-1 lines | Expanded logical parts | Notable mechanisms |
| --- | ---: | ---: | ---: | --- |
| 8275 Motorized Bulldozer | 157 | 3,021 | 3,029 | Two motors, gears, worm, clutches, universal joints, sprockets, tracks |
| 42055 Bucket Wheel Excavator | 79 | 4,385 | 3,928 | Dense gearbox, worm, clutch gears, changeover catches, turntables, hoses |
| 42100 Liebherr R 9800 | 186 | 8,655 | 7,279 | Gears, turntables, universal joints, tracks |
| 42121 Heavy-Duty Excavator | 57 | 1,215 | 576 | Gears, universal joints, tracks |

The distinction between source lines and logical parts matters, and it runs in both directions. The 8275 MPD embeds an unofficial LS70 track-link part and uses it 1,630 times; recursively expanding that part's rendering primitives produces tens of thousands of references, but those primitives are not separate LEGO parts. The 42xxx MPDs go the other way: their section lists include embedded parts and primitives, so they contain more source type-1 lines than the model has actual parts.

8275 is the reference drivetrain for the MVP because it is the only supplied model with a motor.

## MVP scope

Planned for the first release:

- LDraw MPD/LDR/DAT parsing and library resolution.
- Solid rendering, colors, BFC handling, caching, instancing, and part selection.
- LDCad shadow overlay and inherited snap-feature extraction.
- Axial-span-aware connection matching.
- Shaft reconstruction and bearing/keyed-connection diagnostics.
- Spur, bevel, crown, and worm gear constraints.
- Multiple driver inputs, exact tooth ratios, and closed-loop conflict reporting.
- One hand-verified, end-to-end 8275 drivetrain demonstration.

Explicitly deferred:

- Moving track loops and flexible hoses.
- Spring, suspension, and general linkage pose solving.
- Rack-and-pinion translation and linear actuators.
- Differential equations.
- Exact articulated universal-joint animation.
- Torque-dependent clutch slip and worm self-locking.
- Rigid-body dynamics, collision response, and structural stress.

Unsupported mechanisms will remain static and be labeled in the UI.

## Planned architecture

The implementation targets .NET 8 and Windows WPF:

```text
src/TechnicsSim.LDraw/       Parsing, file sources, transforms, colours, geometry  [exists]
src/TechnicsSim.Mechanics/   Snap features, matching, catalog, graph, solver       [exists]
src/TechnicsSim.Wpf/         Helix-based viewer and diagnostics UI                 [exists]
tools/TechnicsSim.Cli/       Coverage reports and graph diagnostics                [exists]
tests/TechnicsSim.Tests/     Fixtures, golden reports, and integration tests       [exists]
tests/TechnicsSim.Wpf.Tests/ Selection mapping, axis conversion, scene batching    [exists]
```

Report building lives in the core library rather than the CLI, so the command line and the eventual diagnostics UI cannot drift into emitting different numbers for the same model.

The renderer sits behind `ISceneRenderer`, so no HelixToolkit type reaches the view model, the mechanism code, or the tests. The LDraw-to-renderer axis change lives at exactly one documented boundary (`LDrawAxes`) and is a rotation rather than a mirror, because a mirror would invert every face and quietly undo the BFC work done while building meshes.

The renderer is planned around [HelixToolkit.Wpf.SharpDX](https://www.nuget.org/packages/HelixToolkit.Wpf.SharpDX/3.1.2). Core parsing and mechanics will remain independent of WPF so they can be tested and used from the CLI.

See [PLAN.md](PLAN.md) for the reviewed technical design, delivery gates, data model, known pitfalls, and test strategy.

## Development status

- [x] Collect representative LDraw models.
- [x] Audit MPD structure and logical instance counts.
- [x] Review LDraw and LDCad shadow semantics.
- [x] Write the implementation and verification plan.
- [x] Establish the external shadow-library source and revision.
- [x] Phase 0: solution, resolver, permanent audit CLI, and library bootstrap.
- [x] Phase 1: loader and visual vertical slice.
- [x] Phase 2: shadow features and connection diagnostics.
- [ ] Phase 3: mechanics catalog, sidecars, and shaft graph.
- [ ] Phase 4: solver and first validated drivetrain animation.

## Getting started

```powershell
./scripts/bootstrap-libraries.ps1          # fetch the shadow library, locate a parts library
dotnet build
dotnet test
```

Run the viewer:

```powershell
dotnet run --project src/TechnicsSim.Wpf
```

It opens the first model in `Models/` and lets you orbit, select parts, and inspect the tree. Clicking a part shows its full hierarchical instance ID; the tree and the viewport select each other. `--model <path>`, `--diagnostics`, and `--edges` set the startup state so a specific view can be reproduced without clicking through the UI.

Audit a model from the command line:

```powershell
dotnet run --project tools/TechnicsSim.Cli -- library-info
dotnet run --project tools/TechnicsSim.Cli -- inspect-model "Models/8275-1.mpd"
dotnet run --project tools/TechnicsSim.Cli -- coverage "Models/8275-1.mpd" --json reports/8275.json
dotnet run --project tools/TechnicsSim.Cli -- mesh-stats "Models/8275-1.mpd"
dotnet run --project tools/TechnicsSim.Cli -- connections "Models/8275-1.mpd" --json reports/8275-connections.json
```

`library-info` prints the exact parts-library revision and shadow commit every other report depends on. `inspect-model` summarizes sections, the three instance counts, and any resolution failure. `coverage` adds shadow-feature availability per part, weighted by instance count. `mesh-stats` builds the whole render scene headlessly and reports batching, instancing, and triangle counts, so rendering claims can be checked in CI rather than eyeballed.

`connections` replays effective shadow patches through the official part tree, places finite snap sections on logical instances, and emits span-aware mate candidates. Its JSON includes residuals, confidence, ambiguities, rejected scale/mirror inheritance, and the shadow line plus transform chain behind every feature. In the viewer, enable **Diagnostics** to see feature axes and section profiles; cyan is matched, orange unmatched, magenta ambiguous, and green lines mark unambiguous mate candidates. Selecting a part shows the named rule and residuals behind its first candidate.

Exit codes are `0` for success, `1` for a configuration or usage error, and `2` when a model has unresolved references.

## External data setup

The official parts library and shadow library are intentionally not committed because they are independently maintained datasets. `scripts/bootstrap-libraries.ps1` sets both up and prints the revisions in use; the rest of this section describes what it does.

### LDraw parts library

Tools accept any of the following, in this resolution order:

1. A path supplied through `--ldraw` or `TECHNICSSIM_LDRAW_PATH`.
2. A current `complete.zip` from the [LDraw library updates page](https://library.ldraw.org/updates), placed at `Library/complete.zip`, or an extracted tree at `Library/LDraw/`.
3. LeoCAD's `library.bin`, which is a ZIP containing an `ldraw/` tree.

ZIP sources are read in place, so a 480+ MB library never has to be extracted to inspect a few hundred parts. Run `./scripts/bootstrap-libraries.ps1 -Source Download` to fetch a current `complete.zip`; LeoCAD's snapshot works for local development but is not a tracked release.

### LDCad Shadow Library

The bootstrap script clones this into the ignored `Library/` directory. Manually:

```powershell
New-Item -ItemType Directory -Path Library -Force
git clone https://github.com/RolandMelkert/LDCadShadowLibrary.git Library/LDCadShadowLibrary
```

The project audit and the committed golden reports use shadow commit `15aa1e718b6a8da37d24fc7af5e52e262c041bfb` from 2026-03-15. Every report records the official-library version and shadow revision that produced it.

Do not commit `Library/`; it is covered by [.gitignore](.gitignore).

## Testing philosophy

The mechanics must be explainable and independently verifiable.

Implemented so far:

- Line-type, MPD-splitting, and header-classification fixtures.
- Nested matrix composition and color-inheritance fixtures, which lock multiplication order numerically rather than by visual inspection.
- Resolution ordering fixtures, including an MPD-local `.dat` that must outrank the official library.
- Cycle detection, stable hierarchical instance IDs, and shadow-meta field parsing.
- Baseline tests pinning all three MPDs to the audited section, line, instance, and part counts.
- Golden coverage reports per model, compared with source paths excluded so a diff means a real interpretation change.
- BFC fixtures: winding declarations, mid-file winding changes, `INVERTNEXT`, reflecting transforms, and the case where the two cancel.
- Colour fixtures covering inherited colour 16, edge colour 24, direct colours, and material clauses that must not overwrite a surface colour.
- Scene batching assertions: repeated parts collapse to one vertex buffer, colour variants share it, and uploaded triangles stay separate from drawn triangles.
- Selection mapping from a rendered instance index back to its hierarchical logical instance ID, and the axis conversion's handedness.
- Effective shadow inheritance with finite `SNAP_CYL`, clip, finger, and generic shapes; `SNAP_CLEAR`; non-recursive `SNAP_INCL`; centered/uncentered grids; and scale/mirror policies.
- Span-aware axle/bore mating, separate radial/axial/angular residuals, named confidence rules, same-instance exclusion, and explicit many-to-many ambiguity.
- CLI JSON provenance and viewer overlays for section profiles, axes, mates, unmatched features, and ambiguous candidates.

Planned:

- 8:24 and three-gear ratio fixtures with exact sign composition.
- Hand-checked ratios for the first animated 8275 drivetrain.

Tests that need the external libraries skip with an explicit reason when those are absent, so a clean checkout still runs the full fixture suite. Golden baselines are regenerated only with `TECHNICSSIM_UPDATE_GOLDEN=1`, never as a side effect.

Every inferred feature and constraint will retain provenance so the CLI and UI can answer why it exists.

## Contributing

The project is at the stage where design review and focused prototypes are especially valuable. Useful contribution areas include:

- LDraw resolution, BFC, and transform edge cases.
- LDCad shadow-meta interpretation and test fixtures.
- Technic gear, clutch, differential, and universal-joint semantics.
- Efficient WPF/DirectX instancing and selection.
- Small, physically verifiable drivetrain examples.

Before implementing a large subsystem, read [PLAN.md](PLAN.md) and open an issue describing the proposed scope and acceptance fixture. Keep external part libraries out of commits.

## Data, licensing, and trademarks

- The supplied MPD files retain their embedded author, history, and CCAL 2.0 license declarations.
- The LDCad Shadow Library is distributed separately under CC BY-SA 4.0 and requires attribution when its data or derivatives are distributed.
- The official LDraw library has its own contributor and redistribution terms.
- A project-wide software license has not yet been selected. Until one is added, the repository should not be assumed to grant general reuse rights for future source code.

LEGO is a trademark of the LEGO Group, which does not sponsor, authorize, or endorse this project. LDraw is an independently maintained community format.

## References

- [LDraw file-format specification](https://www.ldraw.org/article/218.html)
- [LDraw BFC extension](https://www.ldraw.org/article/415.html)
- [Official LDraw library updates](https://library.ldraw.org/updates)
- [LDCad Shadow Library](https://github.com/RolandMelkert/LDCadShadowLibrary)
- [LDCad meta documentation](https://www.melkert.net/LDCad/tech/meta)

