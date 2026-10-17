# MOONCL loading progress

The launcher owns this progress. It does not load, connect to, or rewrite `mod-loading-screen`.

For Fabric/Quilt the launcher prepends this Java agent. The agent reads `mods/*.jar` metadata (`fabric.mod.json` or `quilt.mod.json`) and reports every entrypoint type as its own phase, in Fabric/Quilt order:

`preLaunch` -> `main` -> `client` / `server` -> `client_init` / `server_init` -> other declared types.

Each phase reports the real entrypoint count (`current/total`) and the current mod name plus id. The stage line matches the original loading bar: `Loading 'client' (ClientModInitializer) — 81/83 — Wifi Card Games`. Known Fabric/Quilt keys use the initializer interface simple name; other declared types keep a readable fallback and are appended rather than dropped.

The launcher passes `MOONCL_MLS_TARGET` (instance name) for the title, `MOONCL_MLS_PROGRESS_FILE` for the snapshot and `MOONCL_MLS_PROGRESS_TOKEN` as the per-launch identity.

This is a declaration-driven walk: the phase order and every entrypoint come from the pack, but the pacing follows the walk rather than the loader's own callbacks. Forge and NeoForge are unchanged and still finish from the game window without an invented percentage.

The agent never changes loader methods, mod jars, PeerGuard, checksums, or the game classpath. Closing the launcher window only stops reading the temporary progress file.
