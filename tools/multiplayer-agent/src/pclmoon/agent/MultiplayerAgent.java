package pclmoon.agent;

import java.lang.instrument.Instrumentation;
import java.util.concurrent.atomic.AtomicBoolean;

public final class MultiplayerAgent {
    private static final AtomicBoolean STARTED = new AtomicBoolean();
    private MultiplayerAgent() { }
    public static void premain(String arguments, final Instrumentation instrumentation) {
        try {
            if (!STARTED.compareAndSet(false, true)) return;
            final String p = System.getenv("PCL_MOON_LINK_PORT"), token = System.getenv("PCL_MOON_LINK_TOKEN");
            if ((p == null || p.isEmpty()) && (token == null || token.isEmpty())) return;
            final int port = Protocol.port(p);
            if (token == null || !token.matches("[0-9a-fA-F]{64}")) throw new IllegalArgumentException("Invalid link token");
            final String path = System.getenv("PCL_MOON_LINK_MAPPINGS");
            final String runtimePath = System.getenv("PCL_MOON_LINK_RUNTIME_MAPPINGS");
            Thread worker = new Thread(new Runnable() {
                public void run() {
                    try {
                        Mappings m;
                        try { m = Mappings.load(path); }
                        catch (Exception e) {
                            System.err.println("[PCL-MOON agent] Cannot read mappings; trying named/SRG/intermediary aliases");
                            m = new Mappings();
                        }
                        try { m.loadRuntime(runtimePath); }
                        catch (Exception e) {
                            System.err.println("[PCL-MOON agent] Cannot read runtime mappings; trying known aliases only");
                        }
                        new Protocol(port, token, MinecraftBridge.provider(instrumentation, m)).run();
                    } catch (Throwable t) { System.err.println("[PCL-MOON agent] Disabled: " + Protocol.message(t)); }
                }
            }, "PCL-MOON-multiplayer-link");
            worker.setDaemon(true);
            worker.setUncaughtExceptionHandler(new Thread.UncaughtExceptionHandler() {
                public void uncaughtException(Thread thread, Throwable error) {
                    System.err.println("[PCL-MOON agent] Disabled: " + Protocol.message(error));
                }
            });
            worker.start();
        } catch (Throwable t) {
            System.err.println("[PCL-MOON agent] Disabled: " + Protocol.message(t));
        }
    }
}
