# Runtime capability contract

2026-11-06. Initial implementation is described in [RUNTIME-CAPABILITIES](RUNTIME-CAPABILITIES.md). Remaining details below are design targets, not a claim that every part is implemented. Existing Ecaty command/result and source-edit patterns remain the baseline. No replacement ECS, physics solver or arbitrary plugin reflection bridge is proposed.

## Common shape

| Part | Meaning |
|---|---|
| Identity and type | Stable entity/asset identifiers, capability name and schema version; no serialized Unity instance IDs or native pointers |
| Authored configuration | Source-backed parameters, seed, targets/part roles, materials/assets and limits; saved and editable by spatial tools, browser or AI through the same validated operations |
| Commands | Explicit requests such as move-to, drag-target, grip, play-sound, rebuild or cancel; transient commands are distinct from recipe configuration |
| Observed output | Actual pose, contacts, task status, playing state, generated bounds, errors and applied revision; does not overwrite the authored recipe |
| Lifecycle | Create, change, remove; asynchronous generation reports pending/ready/failed and can be cancelled or superseded |
| Save/rebuild | Save source/configuration, referenced assets and explicitly persistent state; reconstruct plugin instances/graphs on reload. Pending operations restart according to their declared policy |

Flecs schemas expose field types, units/ranges, target roles, defaults, supported operations and available asset references. Runtime Script can compose and define higher-level types, relationships and behavior over this contract. New plugin operations still need compiled implementations. Avoid one giant component exposing arbitrary Inspector fields.

## Write ownership

Each root has one movement owner: authored placement, a CharacterController motor, or a dynamic Rigidbody. Behaviors/user input submit intent to that owner. Picking up a physical object supplies a drag/grip target to the selected physical/kinematic manipulation policy; it does not let the input handler and Rigidbody independently overwrite its Transform. End/cancel restores the previous mode and publishes the release event/result. A gameplay teleport is a separate explicit command.

Model root motion is separate from part pose. Assign each limb/branch to the appropriate procedural solver; mix authored/additive channels only at declared stages. Keep existing FImpossible legs, Boing secondary branches and Flecs PoseOffset layering. Add Animation Rigging to selected constraints without having two IK solvers own one limb. MudBun consumes the final pose; observed transforms return to Flecs.

## Rebuilding and propagation

1. A validated source-backed edit changes recipe data and its revision in Flecs. Native coordination records the affected entities and old/new spatial bounds.
2. Relations identify dependent capabilities. Spatial overlap discovers additional affected entities only where a declared rule requires it. Clipper computes footprints/intersections when needed; it does not infer these rules.
3. Schedule the necessary adapters in dependency order. Geometry, masks and placement precede collision/navigation refresh where those consume the results. Coalesce repeated drag edits and discard outputs for obsolete revisions.
4. An adapter stages and validates its own generated result before replacing the old representation. Keep the last valid result on generation failure where possible. This is not a claim of transactional whole-world Flecs Script updates.
5. Publish actual results/events and applied revisions. Downstream work proceeds from current successful results, not stale jobs. No model inference is required per frame or drag update.

For a road: authored spline/width/profile -> road mesh and footprint -> optional ground modifier + texture/exclusion masks -> vegetation reconciliation -> collider and navigation updates. Store base ground plus modifiers so removing/moving a road restores the former area. Spline Mesher's installed curve conforming projects to existing colliders; its source includes generated collision meshes. No TerrainData height-writing path was found in this package, so ground deformation is additional work, not a presumed built-in feature.

## Native versus Unity

Flecs/native owns recipe data, dependency/dirty-region coordination, behavior decisions and selected portable algorithms. Unity owns runtime plugin calls, meshes, rendering, PhysX collision solving, existing navigation and audio playback. Put the geometry algorithm in native C++ only when there is an actual reuse/performance/portability reason; another ABI crossing is not inherently easier.

Clipper2 is a polygon operation/triangulation utility, not a spline mesher, general 3D modeler, collision solver, terrain engine or understanding agent. Use the current PolyClipping repository. It does not require native colliders or standalone Detour. Meshoptimizer remains optional for baked meshes after profiling.

## Sound, style and baking

- Native audio capability supports sample-bank synthesis through TinySoundFont, ordinary sample references, and potentially procedural DSP voices. Noise/oscillators/envelopes/filters can produce wind-like sounds, bubbles and stylized chirps; they do not guarantee arbitrary realistic acoustics. STK is an optional existing C++ DSP toolkit to evaluate instead of rebuilding every synthesis primitive. Unity handles playback/spatialization/mixing/effects. Keep audio callbacks isolated from live Flecs mutation/querying.
- UniStorm is imported. Runtime manager has cloud type/quality/weather controls; cloud profiles and gradients expose style parameters. Start with a configurable style, but do not claim arbitrary cartoon rendering from a parameter change. Keep world weather intent separate from rendering so stylized cloud meshes/particles or another sky renderer can replace the presentation without changing rules. Check global/singleton assumptions before multiple independent worlds.
- For MudBun baking, retain the brush recipe and part/rig references as the editable source. Generate a conventional mesh, optionally simplify/create LODs, and choose/update its collider representation separately. Re-editing regenerates from the recipe. Start with static props; animated rigs require preserved pose/part/weight semantics. Baking is not yet an implemented Ecaty workflow, though local MudBun exposes mesh generation and locking APIs.

## Required imports

No further imports are required to define this contract or start core adapter work. Fetch/pin TinySoundFont, BehaviorTree.CPP and any selected Clipper2 code as native dependencies during their implementation tasks. Choose a redistributable SoundFont bank as part of audio setup. MicroSplat and TVE are excluded by the current decision. No Detour, extra collider library, FastNoise or additional terrain package is required now.

Sources: [current Clipper2](https://github.com/PolyClipping/Clipper2), [TinySoundFont](https://github.com/schellingb/TinySoundFont), [STK DSP/synthesis classes](https://github.com/thestk/stk), [UniStorm documentation](https://github.com/Black-Horizon-Studios/UniStorm-Weather-System/wiki), [meshoptimizer](https://github.com/zeux/meshoptimizer). Local source checks: Spline Mesher Runtime/Curve/CurveMeshSettings.cs and SplineMeshSegment.cs; UniStorm Scripts/System/UniStormManager.cs and CloudProfile.cs; MudBun Script/MudRenderer.cs.
