# MOONCL / Mod Loading Screen native progress bridge

A 9 KiB Java 8 agent plus headless IPC relay. Sources are maintained here; the generated JAR is embedded as a launcher WPF resource. No Fabric/ASM dependency is shipped by this bridge.

## Integration

Compatible producer: `G:\exmo\mod-loading-screen`, Gaming32 Mod Loading Screen derivative, local built version 1.0.5. Only Fabric/Quilt are supported by that producer. The producer must already be installed as a mod or configured as a Java agent. MOONCL does not install or replace it automatically.

When the native loading window is enabled, MOONCL prepends `-javaagent:<bridge.jar>` for Fabric/Quilt and provides two process-scoped environment variables:

- `MOONCL_MLS_PROGRESS_FILE`: unique temporary snapshot file.
- `MOONCL_MLS_PROGRESS_TOKEN`: random per-launch identity.

The agent changes the `CONSTANT_String` holding MLS's **display subprocess main class**, not the class definition or game entrypoints. It adds the bridge JAR to the `java.class.path` property used by MLS to build that child's command. The original game's class loader/classpath is not replaced. Original MLS still produces and owns the pipe. The replacement child consumes all packets, including after the user dismisses MOONCL's window, so a closed native window cannot break the producer's pipe and terminate the game.

Unknown class variants fail open. Headless, Android or `mod-loading-screen.disableIpc=true` do not produce the compatible display pipe; no game percentage is claimed. The bridge does not alter PeerGuard, updater, checksums, authentication, or mod transformations. Packs with policies forbidding additional Java agents must permit the bridge themselves; no integrity policy is bypassed.

## Original private IPC

Java `DataOutputStream`: byte packet ID, byte argument count, then `writeUTF` strings (modified UTF-8). It is NOT normal UTF-8, a socket, or game log text.

Handled: 0 entrypoint begin/count, 1 mod entrypoint invocation, 2 entrypoint end, 4 custom bar creation, 5 custom bar operations, 6 title, 7 updater activity, 255 final-entrypoint completion. Packet 3 memory and 8 source dates do not advance loading percentages. Unknown totals are indeterminate. Each entrypoint type/custom bar has its own percentage, not an invented whole-game estimate.

The relay atomically writes bounded JSON protocol 1 snapshots with `token`, `status` (`loading`, `ready`, `unavailable`), `stage`, `detail`, `current`, `total`. It publishes a 1 s heartbeat while the pipe is open. EOF is a disconnected feed, not successful completion. MLS's packet 255 means its final entrypoint completed; MOONCL also waits for its existing game-window readiness signal. This does not measure subsequent world loading or guarantee all resource reload work has completed.

## Build / verify

From the repository root:

```powershell
.\scripts\Build-LoadingBridge.ps1 -JavaHome 'C:\Program Files\Zulu\zulu-21'
.\scripts\Test-LoadingBridge.ps1 -JavaHome 'C:\Program Files\Zulu\zulu-21' `
  -MlsJar 'G:\exmo\mod-loading-screen\build\libs\mod-loading-screen-1.0.5.jar' `
  -FabricLoaderJar '<path to compatible fabric-loader.jar>'
```

JDK 9+ is required to build with `--release 8`; the result runs on Java 8+. Test calls the real producer JAR's own IPC methods in a scratch working directory, checks 1/4 entrypoint and 42/100 custom progress, modified UTF Unicode/NUL, and completion. It does not start Minecraft or run the reference agent's updater/guard.

Protocol reference: `src/main/java/io/github/gaming32/modloadingscreen/ActualLoadingScreen.java` in the supplied MIT-licensed reference project (Gaming32 Mod Loading Screen). No reference JAR or third-party bytecode library is bundled here.
