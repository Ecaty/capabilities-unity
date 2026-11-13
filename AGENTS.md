# Runtime capabilities: start here

Unity package `com.runtime.capabilities`: reusable Flecs capability contracts, lifecycle, registered assets and Unity adapters. It is part of the runtime engine every product builds on, so it must work for any product type and contain no product assumptions. This repo stands alone: this file, [BACKLOG](BACKLOG.md) and `Documentation~/` are enough to work here. In a product checkout it is the `Packages/com.runtime.capabilities` submodule; the product repo's `docs/README.md` is the cross-repo overview and links every repo's backlog.

## Read on demand

- [BACKLOG](BACKLOG.md): open tasks and decisions for this package.
- [Documentation~/RUNTIME-CAPABILITIES](Documentation~/RUNTIME-CAPABILITIES.md): flow, contract, lifecycle, persistence and limits as implemented.
- [Documentation~/CAPABILITY-CONTRACT](Documentation~/CAPABILITY-CONTRACT.md): the common capability shape and design targets.
- Native side (coordination, footprints, behavior trees): `src/RuntimeCapabilities.cpp` in [flecs-native](https://github.com/Ecaty/flecs-native) and its [FLECS-NOTES](https://github.com/Ecaty/flecs-native/blob/main/docs/FLECS-NOTES.md).

## Rules

- Flecs is authoritative; bindings are disposable adapters. One registered capability per binding entity, explicit dependencies (`CapDependsOn`), a single transform owner, disposable owned instances.
- Accept application assets and services through explicit references/providers. Never depend on a consuming application's `Assets/` paths or feature types; optional vendor integrations stay in the host and independently gated.
- No arbitrary Inspector reflection and no second ECS. Clone persistent inputs before vendor APIs that may destroy them.
- Preserve namespaces, `.meta` GUIDs and the stable C ABI. Package docs go in `Documentation~/` (ignored by Unity, so no `.meta` files).
- Verify real outputs and reconstruction, not only component existence.

## Working across repos

- Package tasks go in this BACKLOG; product tasks in the product's `docs/BACKLOG.md`. Keep both short; mark work done only with current evidence.
- Commit and push a feature branch here first, then update the pointer in the product repo.
- Verify through the host product: its capability native smoke (`Verification/capability_native_smoke.py`), its Unity EditMode/PlayMode suites and its capability verifier.
- Links to `https://github.com/Ecaty/Ecaty/blob/main/...` are the first product's docs; in a composed checkout they are at `../../`.
