# TechnicsSimulator implementation plan

## Goal

Build a Windows desktop application that loads the three supplied LDraw MPD models, renders them efficiently, reconstructs their high-confidence mechanical connections, and animates selected rotary drivetrains with auditable gear ratios.

The first release is a kinematic simulator, not a rigid-body dynamics engine. It models pose and velocity constraints, not mass, torque, friction, gravity, backlash, or material deformation.

The honest MVP is narrower than "make every moving part in all three models work":

- Load and inspect all three models.
- Show inferred connection features, shafts, bearings, gear meshes, confidence, and unsupported boundaries.
- Animate validated spur, bevel, crown, and worm gear paths from a user-selected input.
- Keep tracks, hoses, springs, and most linkages static.
- Treat differentials, racks, exact universal-joint motion, and torque-dependent clutch slip as explicit later work rather than silently guessing.

## Review findings and corrections

The original plan had the right central insight: LDraw supplies geometry but no mechanism graph, so the hard problem is reconstructing mechanical constraints. The following corrections materially change the implementation order and data model:

1. The coverage spike must not be throwaway code. Correct coverage requires the same MPD resolver, part-tree traversal, shadow overlay, transform logic, grid expansion, and inheritance rules as the product. It becomes a permanent CLI and regression suite.
2. A `.dat` suffix does not prove that a reference is an external library part. `8275-1.mpd` embeds `8275 - LS70.dat` as an `Unofficial_Part`. MPD-local files must win resolution before external library files.
3. Snap cylinders are axial spans made of section profiles, not points. Matching only positions within 2 LDU would miss ordinary axle-through-hole connections whose feature origins are separated along the common axis.
4. Shadow metadata provides useful geometry, but it does not completely determine mechanics. An axle-shaped male in an axle hole is keyed, and an axle in a round hole is a bearing; however, a round pin in a round hole, a clip, or fingers may be structural or may form a hinge. Those cases need confidence-ranked inference and model overrides.
5. `SNAP_INCL` is defined as non-recursive. Shadow inheritance also has `id`, `group`, `scale`, and `mirror` behavior that cannot be omitted. `SNAP_CLEAR` must operate in file order after inherited child features.
6. Rigidly unioning all "fixed" snap pairs would over-constrain real Technic mechanisms. The MVP should construct rotary shaft assemblies first and leave ambiguous body grouping visible and overridable.
7. A differential is not an equal-and-opposite graph conflict. It is a multi-shaft constraint with an extra degree of freedom. Detect it from known part semantics and stop or invoke a later differential solver.
8. Worm non-backdrivability and clutch slip are torque/friction effects. A pure kinematic solver may propagate the worm velocity constraint in either direction and may expose a clutch as locked/free, but it must not claim to predict load-dependent behavior.
9. The viewer belongs early in the project. Feature axes, overlapping spans, candidate meshes, and bad inferred connections are much faster to debug visually than from logs alone.

## Verified model baseline

The following is an independent lexical/graph audit of the files currently in `Models/`. "Logical instances" expands model/submodel references but stops at actual part boundaries, including the embedded LS70 part.

| Model | MPD sections | Physical type-1 lines | Expanded logical part instances | Distinct logical parts |
| --- | ---: | ---: | ---: | ---: |
| 8275 Motorized Bulldozer | 157 | 3,021 | 3,029 | 138 |
| 8458 Silver Truck (B) | 29 | 2,093 | 2,068 | 116 |
| 8458 Street Sensation (Web) | 50 | 2,289 | 2,240 | 117 |

These numbers describe different things and must remain separate in reports. In particular, 3,021 is not the flattened 8275 part count. Fully descending into the embedded LS70 geometry produces tens of thousands of primitive references; those primitives are geometry, not independent LEGO part instances.

Other scope-relevant facts found in the supplied files:

- 8275 uses 1,630 instances of the embedded LS70 track-link part, two motors, spur/bevel gears, a worm, clutch gears, universal joints, and sprockets.
- Both 8458 variants contain a 6573 differential and gear racks. A complete 8458 drivetrain therefore requires differential and rotary-to-linear constraints beyond the MVP.
- The 8458 MPDs contain large generated fallback meshes for hoses and springs. The Silver model has 11,576 conditional lines; the Web model has 22,624. The first renderer should draw the solid fallback geometry and may defer camera-dependent conditional edges.
- The current shadow checkout has direct data for many critical parts, but not every one. For example, 3647, 32270, motors, and several clutch/worm parts have no direct shadow file and must obtain features through primitive inheritance or the mechanics catalog.

## Repository and external data

Target `net8.0` for libraries and `net8.0-windows` for WPF. The installed .NET 10 SDK can build those targets, and the machine has the .NET 8 desktop runtime. Use HelixToolkit.Wpf.SharpDX 3.1.2 behind a small renderer adapter; verify its instancing and hit-testing APIs in the first visual slice rather than spreading toolkit types through the core.

Use five projects:

```text
src/TechnicsSim.LDraw/       LDraw parsing, file sources, resolution, colors, geometry
src/TechnicsSim.Mechanics/   shadow features, matching, catalog, graph, ratio solver
src/TechnicsSim.Wpf/         Helix-based viewer and diagnostics UI
tools/TechnicsSim.Cli/       coverage, graph dumps, golden reports, troubleshooting
tests/TechnicsSim.Tests/     unit, fixture, golden, and opt-in real-model tests
```

External libraries stay under `Library/` and are ignored by Git.

- `Library/LDCadShadowLibrary/` is already cloned from the upstream repository at commit `15aa1e718b6a8da37d24fc7af5e52e262c041bfb` (2026-03-15).
- `C:\Program Files\LeoCAD\library.bin` is a ZIP containing an `ldraw/` tree and can be used as a read-only official-library source. Its content is around the 2025-08 update, so prefer a current `complete.zip` for reproducible release results.
- Add `scripts/bootstrap-libraries.ps1` during Phase 0. It should download or update `complete.zip` and the shadow checkout, support an existing directory or LeoCAD ZIP, print source versions/hashes, and never silently change test baselines.

Define `ILDrawFileSource` implementations for a directory, ZIP archive, and MPD-local files. This avoids extracting a 480+ MB library just to read a small working set and makes command-line/CI configuration explicit.

Suggested configuration order:

1. Command-line option or `TECHNICSSIM_LDRAW_PATH`.
2. `Library/complete.zip` or `Library/LDraw/`.
3. Known LeoCAD `library.bin` location as a convenience fallback.

Always display the chosen official-library source, its hash/update, and the shadow commit in CLI reports and the About/Diagnostics UI. Preserve the LDraw model licenses and include the LDCad Shadow Library's CC BY-SA 4.0 attribution and license when distributing its data or derivatives.

## Core technical design

### 1. LDraw document and resolution model

Parse definitions before expanding instances:

- Split MPDs with `0 FILE` and `0 NOFILE`; preserve names with spaces and compare canonical names case-insensitively.
- Treat the first MPD file as the root unless the caller selects another.
- Resolve MPD-local definitions first, then the current model location, then configured LDraw sources. Normalize slash direction but retain the original name for diagnostics.
- Support normal part, primitive, `s/`, `8/`, and `48/` lookups. Detect missing, ambiguous, cyclic, and degenerate references and report the full reference chain.
- Do not classify model versus part by extension alone. Use provider origin and `!LDRAW_ORG` where available.
- Give every expanded logical part a stable hierarchical instance ID based on MPD definition and reference occurrence. Sidecar overrides refer to these IDs and include a part/position fingerprint so stale overrides can be detected.

Parse line types 0 through 5 into a reusable AST. Geometry expansion and logical part expansion are different traversals: geometry descends through part primitives, while the mechanical scene stops at logical part boundaries and attaches the resulting mesh/features to that instance.

Use LDraw coordinates and LDU in every core library. For `System.Numerics.Matrix4x4`, map a type-1 line as:

```text
| a d g 0 |
| b e h 0 |
| c f i 0 |
| x y z 1 |
```

Lock multiplication order with nested-transform tests; do not rely on visual inspection. Reject or quarantine singular matrices. Keep the renderer-axis conversion at one documented boundary.

### 2. Geometry and rendering

- Parse `LDConfig.ldr`, inherited color 16, edge color 24, and direct colors.
- Implement BFC state (`CERTIFY`, winding, `CLIP`/`NOCLIP`, `INVERTNEXT`) and determinant reversal correctly. Render uncertified geometry double-sided instead of pretending it is certified.
- Triangulate type-4 quads deterministically. Render type-2 edges as an optional pass. Defer true camera-dependent type-5 edges until solid rendering and mechanics are stable.
- Ignore LDCad `PATH_*` and `SPRING_*` semantics initially and render their generated LDraw fallback geometry as static content. Avoid double-instantiating generator metadata.
- Cache parsed definitions by canonical source key and source version. Cache meshes with color slots rather than baking inherited color into every vertex buffer.
- Instance repeated logical parts by mesh/material/handedness where the toolkit permits it. Keep a mapping from rendered instance index back to logical instance ID for selection.
- Treat tracks and generated flex geometry as static decorative assemblies in the MVP.

### 3. Effective shadow-feature extraction

Shadow files are patches appended to matching official LDraw files. The extractor must reproduce those semantics, not merely scan top-level shadow files:

- Descend the official part/primitive tree, transform child features into the containing part's coordinates, and then apply the matching shadow patch in order.
- Parse `SNAP_CYL`, `SNAP_CLP`, `SNAP_FGR`, `SNAP_GEN`, `SNAP_INCL`, and `SNAP_CLEAR`. Leave a typed extension point for other metas.
- Implement `SNAP_INCL` as the documented non-recursive include and resolve it only against allowed local shadow references.
- Honor `id`, `group`, `pos`, `ori`, `scale`, `mirror`, `caps`, `center`, `slide`, and both `SNAP_CYL` and `SNAP_INCL` grids.
- Apply snap `scale`/`mirror` inheritance policies when official type-1 transforms scale or reflect a child. Do not inherit a feature when its policy rejects the transform.
- Preserve complete `secs` profiles, including round, axle, square, and flexible-lip section tokens, lengths, caps, and provenance. A feature is an oriented finite shape, not a point.
- Make `SNAP_CLEAR [id=...]` remove only matching inherited IDs and empty `SNAP_CLEAR` remove all inherited features accumulated so far.
- Cache effective features per canonical part plus official/shadow source revisions.

Every feature and inferred mate carries provenance: logical instance, official/shadow file, source line, transforms, and the rule that classified it. The diagnostics UI must be able to explain "why are these connected?"

### 4. Feature mating and confidence

Use a broad-phase spatial index over feature bounds (capsule/section AABBs), followed by narrow-phase geometry:

- Exclude features belonging to the same logical part instance unless a specific semantic rule requires self-composition.
- Require compatible genders/groups, nearly collinear axes where appropriate, compatible cross-sections, overlapping projected axial intervals, and cap/section clearance.
- Compare radial and axial tolerances separately. Start with conservative configurable values and record the actual residuals for tuning.
- Resolve duplicate section-level overlaps into one mate between the two logical features.
- Detect ambiguous many-to-many candidates and surface them instead of choosing by iteration order.

Classify only high-confidence mechanical meaning automatically:

| Geometry pair | Initial mechanical interpretation |
| --- | --- |
| Male axle profile to female axle profile | Keyed coaxial; same angular velocity, axial sliding ignored |
| Male axle profile to female round bore | Revolute bearing; no torque transfer |
| Male round pin to female round hole | Geometric mate, mechanically ambiguous |
| Stud/anti-stud with unambiguous seating | Fixed candidate |
| Clip/finger/ball/generic | Typed joint candidate, not automatically fixed |

`slide=true` describes snap-placement behavior and is not enough to infer a kinematic prismatic joint. Friction pin data is also insufficient to decide fixed versus hinge. Put heuristic decisions behind named rules and confidence levels; allow them to be disabled.

### 5. Mechanics catalog and model sidecars

The shadow library describes connection geometry, not gear teeth, pitch surfaces, motor outputs, clutch state, or differential equations. Add two explicit data layers:

`data/parts-mechanics.json` contains reusable part semantics:

- Gear type, tooth count, local rotation axis, pitch center/radius, tooth-face width, compatible mesh types, and optional handedness/starts.
- Motor output feature and default input label.
- Worm, crown gear, sprocket, clutch, universal-joint, differential, and rack component type.
- Animation ownership hints for compound parts when a logical LDraw part contains moving internal geometry that cannot be separated.

Bootstrap tooth counts from official descriptions, but hand-review every part used by the three models. Start with the actual inventory, not an arbitrary 40-gear catalog. Schema-validate the JSON and include a source/note for every manual entry.

`Models/<model>.mechanics.json` contains model-specific, reviewable corrections:

- Ground/static selection.
- Accepted/rejected ambiguous mates.
- Shaft joins or splits.
- Confirmed/disabled gear meshes.
- Driver definitions and clutch locked/free state.
- Unsupported mechanism annotations.

The UI should be able to export this sidecar after interactive corrections. Automatic inference remains useful, while the shipped demonstrations are deterministic and do not depend on tolerance luck.

### 6. Rotary graph and solver

Build the MVP around shaft nodes rather than attempting a perfect rigid-body decomposition first:

- Join collinear overlapping axle segments and keyed axle-hole components into a shaft assembly.
- Bearings attach a shaft to support geometry but do not transfer angular velocity.
- A gear, bush, motor output, or keyed connector belongs to the shaft selected by its keyed feature.
- Leave ambiguous pin-based chassis grouping out of the rotary graph unless a sidecar confirms it.

Gear candidate detection uses catalog semantics plus geometry:

- Check axis relationship, radial center distance, axial tooth-face overlap, pitch radii, and compatible gear types.
- For ordinary Technic spur gears use `pitchRadiusLdu = teeth * 1.25`, validated against known 8:24 and 8:40 spacing fixtures.
- Derive the signed ratio from contact geometry and chosen shaft-axis conventions. Do not hard-code a negative sign for every mesh; perpendicular bevel/crown axes need a consistent contact-frame calculation.
- Store ordinary tooth ratios exactly as rational numbers and convert to floating point only for display/animation.
- Model a worm as `starts / drivenTeeth` with handedness/direction metadata. Do not add non-backdrivability to the kinematic solver.
- Never accept multiple plausible mesh partners silently. Require a confidence winner or sidecar confirmation.

Represent simple constraints as `omegaB = ratio * omegaA`. A weighted graph/union structure can propagate ratios and detect inconsistent closed loops. Support more than one assigned driver because 8275 has multiple motors. Report over-constraints with both source paths and residuals.

Do not infer a differential from a pairwise conflict. A known differential introduces a multi-variable equation such as `2 * carrier = left + right`; add a small linear-constraint solver when differential support is implemented. Until then, mark the differential as an unsupported boundary and stop propagation through it.

Animation applies each solved shaft angle around its initial world-space axis while retaining the original assembly pose. Parts on one shaft share angle and axis convention. Exact articulated motion of universal-joint centers, steering/suspension linkages, racks, actuators, and tracks is deferred.

## Delivery phases and gates

### Phase 0 - Reproducible audit foundation

Create the solution, core AST/resolver, file-source abstraction, library bootstrap script, and permanent CLI. The first CLI commands are:

```text
technicssim library-info
technicssim inspect-model <model.mpd>
technicssim coverage <model.mpd> --json <report.json>
```

Coverage must report both unique logical parts and weighted logical instances, direct versus inherited features, feature-type histograms, unresolved references, rejected scale/mirror inheritance, and high-use uncovered parts. Store source hashes/versions in JSON.

Gate:

- All three MPDs resolve with no cycles or missing references against the chosen official library.
- Parser results reproduce the baseline table or explain any intentionally different classification.
- Critical 8275 axle/gear/motor parts have effective shaft features, whether inherited or cataloged.
- No production code from this phase is thrown away.

### Phase 1 - Loader and visual vertical slice

Finish solid mesh building, colors, BFC, caching, Helix adapter, camera controls, instance selection, and a model tree. Add a diagnostic mode that draws axes/bounds even before shadow extraction is complete.

Gate:

- All three models load and remain interactively navigable on the target machine.
- 8275 track links are instanced/static rather than expanded into independent vertex buffers.
- Clicking a rendered instance returns the correct hierarchical logical instance ID.
- Automated transform/color fixtures and a manual render checklist pass.

### Phase 2 - Shadow features and mate diagnostics

Implement full effective-feature extraction, span-aware mating, confidence/provenance, CLI graph dumps, and viewer overlays for sections, axes, mates, unmatched features, and ambiguous candidates.

Gate fixtures:

- Axle through a round beam hole: one bearing and no keyed relation.
- Axle through a 24-tooth gear: one keyed relation.
- Axle whose origin is far from the hole origin but whose spans overlap: still mates.
- Parallel neighboring axle/hole features outside radial tolerance: do not mate.
- Mirrored/scaled child features obey their inheritance policies.
- `SNAP_CLEAR`, centered grids, uncentered grids, and `SNAP_INCL` match expected coordinates.

### Phase 3 - Catalog, sidecars, and shaft graph

Add catalog schemas/data for every drivetrain-relevant part in the supplied models, sidecar loading/export, shaft assembly extraction, gear candidate detection, and manual confirmation UI.

Gate:

- A reviewed 8275 shaft/gear subgraph is deterministic from automatic data plus its committed sidecar.
- Every included gear mesh shows tooth counts, measured center/face residuals, confidence, and provenance.
- Unsupported clutch/universal/sprocket boundaries are visible rather than treated as ordinary gears.

### Phase 4 - Solver and first end-to-end animation

Add exact pairwise ratio propagation, multiple drivers, conflict explanations, animation transforms, timeline/slider controls, and selected-shaft ratio readouts.

MVP gate:

- A chosen 8275 motor or axle drives at least one reviewed multi-gear path at hand-verified ratios and directions.
- A three-gear fixture composes ratio and sign exactly.
- Conflicting closed-loop and multiple-driver fixtures produce understandable diagnostics.
- Tracks and unsupported mechanisms remain static and labeled; the app never implies they were solved.
- The same graph and ratios are emitted by the CLI and UI.

### Phase 5 - Expand mechanism coverage

Add features only behind separate tests and acceptance examples:

1. Clutch locked/free state and better motor templates.
2. Average-ratio universal joints, followed by phase-correct articulated animation if needed.
3. Differentials using multi-variable constraints.
4. Rack-and-pinion and linear actuators using translational degrees of freedom.
5. Steering/suspension closed-loop pose solving.
6. Sprocket/track path animation and flexible-part abstractions.
7. Torque/dynamics only as a distinct future subsystem; do not mix it into the velocity graph incrementally.

## Test strategy

Use three layers:

1. Small committed LDraw/shadow fixtures test parser, transforms, BFC, inheritance, section expansion, mating, shaft construction, gear contact, exact ratios, and conflict paths.
2. Golden CLI JSON reports test the supplied MPDs. Normalize paths and omit timestamps so diffs remain useful. Record both official-library and shadow revisions.
3. WPF smoke tests cover load/cancel/reload, selection mapping, graph-to-render ownership, and animation transforms. Keep renderer-independent mechanics tests in the core test project.

Real-model tests that require the external LDraw library should skip with a clear message when the configured library is absent. CI should always run unit fixtures and can cache a pinned library snapshot for scheduled integration runs.

Performance measurements should separately record parse time, definition-cache hit rate, geometry expansion, feature extraction, mating candidate count, GPU instance count, peak memory, and first-frame time. This prevents a fast renderer from hiding an accidentally quadratic connectivity pass.

## MVP acceptance criteria

- Clean setup instructions work with a current complete ZIP, an extracted LDraw directory, or LeoCAD's `library.bin`.
- The app loads all three supplied MPDs without unresolved files and explains which content is static or unsupported.
- Model tree selection, rendered selection, logical instance IDs, feature diagnostics, and CLI reports agree.
- High-confidence axle/keyed/bearing relations are span-aware and covered by fixtures.
- At least one reviewed 8275 multi-gear drivetrain animates with a hand-checked exact ratio and direction.
- Ambiguities, graph conflicts, missing catalog data, and unsupported differential/rack/clutch/track behavior are visible and never silently guessed.
- External data provenance and required license attribution are present in diagnostics/About and release packaging.

## Primary references

- LDraw file format: https://www.ldraw.org/article/218.html
- LDraw BFC extension: https://www.ldraw.org/article/415.html
- Current LDraw library updates: https://library.ldraw.org/updates
- LDCad shadow repository: https://github.com/RolandMelkert/LDCadShadowLibrary
- LDCad snap meta semantics: https://www.melkert.net/LDCad/tech/meta
- HelixToolkit.Wpf.SharpDX 3.1.2: https://www.nuget.org/packages/HelixToolkit.Wpf.SharpDX/3.1.2

