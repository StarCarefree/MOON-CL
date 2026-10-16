package mooncl.agent;

import java.lang.instrument.Instrumentation;
import java.util.concurrent.atomic.AtomicBoolean;

public final class MultiplayerAgent {
    private static final AtomicBoolean STARTED = new AtomicBoolean();
    private MultiplayerAgent() { }
    public static void premain(String arguments, final Instrumentation instrumentation) {
        try {
            if (!STARTED.compareAndSet(false, true)) return;
            final String p = System.getenv("MOONCL_LINK_PORT"), token = System.getenv("MOONCL_LINK_TOKEN");
            if ((p == null || p.isEmpty()) && (token == null || token.isEmpty())) return;
            final int port = Protocol.port(p);
            if (token == null || !token.matches("[0-9a-fA-F]{64}")) throw new IllegalArgumentException("Invalid link token");
            final String path = System.getenv("MOONCL_LINK_MAPPINGS");
            final String runtimePath = System.getenv("MOONCL_LINK_RUNTIME_MAPPINGS");
            Thread worker = new Thread(new Runnable() {
                public void run() {
                    try {
                        Mappings m;
                        try { m = Mappings.load(path); }
                        catch (Exception e) {
                            System.err.println("[MoonCL agent] Cannot read mappings; trying named/SRG/intermediary aliases");
                            m = new Mappings();
                        }
                        try { m.loadRuntime(runtimePath); }
                        catch (Exception e) {
                            System.err.println("[MoonCL agent] Cannot read runtime mappings; trying known aliases only");
                        }
                        new Protocol(port, token, MinecraftBridge.provider(instrumentation, m)).run();
                    } catch (Throwable t) { System.err.println("[MoonCL agent] Disabled: " + Protocol.message(t)); }
                }
            }, "MoonCL-multiplayer-link");
            worker.setDaemon(true); worker.start();
        } catch (Throwable t) {
            System.err.println("[MoonCL agent] Disabled: " + Protocol.message(t));
        }
    }
}
