# Runtime capabilities backlog

Package tasks only; product tasks live in the product's `docs/BACKLOG.md`. Keep it short; mark work done only with current evidence.

## Open

1. **Per-frame GC in `CapabilityRuntime.Update`** (about 22 KB/frame measured in the gallery, 2026-11-11). Profile and remove the allocations.
2. **Checkpointing gaps:** contacts, plugin internals, playing audio and pending tree/Script waits are not saved; tasks restart on reload (RUNTIME-CAPABILITIES limits). Decide what must persist.
3. **Contract design targets not yet implemented:** cancellable/superseded asynchronous generation and the remaining parts of CAPABILITY-CONTRACT marked as targets.

## Decisions

- 2026-11-13: Split from the product repo into its own repo (`Ecaty/capabilities-unity`) with history; same package name, path and GUIDs.
