package pclmoon.agent;

import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;
import java.util.concurrent.TimeUnit;

/** Real premain/Instrumentation/socket lifecycle, still using a SYNTHETIC client. */
public final class PremainProbe {
    public static void main(String[] args) throws Exception {
        AgentHarness.Client.INSTANCE = new AgentHarness.Client();
        Thread.sleep(2500);
        // The fixture client main thread and every agent thread must be daemon: JVM exits here.
    }
    static void run() throws Exception {
        String root = System.getenv("PI_SCRATCH_DIR");
        Path mapping = root == null || root.isEmpty() ? Files.createTempFile("pcl-moon-fixture-", ".txt")
                : Files.createTempFile(Paths.get(root), "pcl-moon-fixture-", ".txt");
        Path runtimeMapping = root == null || root.isEmpty() ? Files.createTempFile("pcl-moon-runtime-fixture-", ".tiny")
                : Files.createTempFile(Paths.get(root), "pcl-moon-runtime-fixture-", ".tiny");
        Process process = null;
        try (ServerSocket listener = new ServerSocket(0, 1, InetAddress.getByAddress(new byte[] {127,0,0,1}))) {
            listener.setSoTimeout(5000);
            Mappings m = AgentHarness.mappings(AgentHarness.Connect6.class);
            Files.write(runtimeMapping, RuntimeMappingsHarness.loaderFile(m, "tiny2", false).getBytes(StandardCharsets.UTF_8));
            try (BufferedWriter w = Files.newBufferedWriter(mapping, StandardCharsets.UTF_8)) {
                for (Map.Entry<String, String> e : m.classes.entrySet()) {
                    w.write(e.getKey() + " -> " + e.getValue() + ":\n");
                    List<Mappings.Member> members = m.members.get(e.getKey());
                    if (members == null) continue;
                    for (Mappings.Member member : members) {
                        w.write("    " + member.type + " " + member.name);
                        if (member.args != null) {
                            w.write("(");
                            for (int i = 0; i < member.args.size(); i++) { if (i > 0) w.write(","); w.write(member.args.get(i)); }
                            w.write(")");
                        }
                        w.write(" -> " + member.runtime + "\n");
                    }
                }
            }
            String agent = Paths.get(MultiplayerAgent.class.getProtectionDomain().getCodeSource().getLocation().toURI()).toString();
            String java = Paths.get(System.getProperty("java.home"), "bin", System.getProperty("os.name").startsWith("Windows") ? "java.exe" : "java").toString();
            ProcessBuilder builder = new ProcessBuilder(java, "-javaagent:" + agent, "-cp", System.getProperty("java.class.path"), PremainProbe.class.getName());
            builder.environment().put("PCL_MOON_LINK_PORT", String.valueOf(listener.getLocalPort()));
            builder.environment().put("PCL_MOON_LINK_TOKEN", AgentHarness.TOKEN);
            builder.environment().put("PCL_MOON_LINK_MAPPINGS", mapping.toString());
            builder.environment().put("PCL_MOON_LINK_RUNTIME_MAPPINGS", runtimeMapping.toString());
            builder.redirectErrorStream(true); process = builder.start();
            try (Socket socket = listener.accept()) {
                socket.setSoTimeout(5000);
                BufferedReader reader = new BufferedReader(new InputStreamReader(socket.getInputStream(), StandardCharsets.UTF_8));
                PrintWriter writer = new PrintWriter(new OutputStreamWriter(socket.getOutputStream(), StandardCharsets.UTF_8), true);
                AgentHarness.check(("HELLO\t1\t" + AgentHarness.TOKEN).equals(reader.readLine()), "real premain HELLO");
                writer.println("OK");
                String line;
                boolean ready = false, started = false;
                try { while ((line = reader.readLine()) != null) {
                    String[] p = line.split("\t", -1);
                    AgentHarness.check(p.length == 5 && p[0].equals("STATE"), "real premain STATE");
                    if (p[2].equals("ready") && !ready) {
                        ready = true; writer.println("CONNECT\tpremain\t" + Protocol.b64("fixture.example"));
                    }
                    if (p[1].equals("premain") && p[2].equals("connecting")) started = true;
                    AgentHarness.check(!p[2].equals("connected"), "no fake connected state in premain probe");
                } } catch (SocketException exitReset) {
                    // Windows may reset rather than FIN when a process exits with a daemon socket open.
                    if (!ready || !started) throw exitReset;
                }
                AgentHarness.check(ready && started, "real getAllLoadedClasses discovery and main-thread CONNECT");
            }
            AgentHarness.check(process.waitFor(5, TimeUnit.SECONDS) && process.exitValue() == 0, "agent daemon threads do not prevent JVM exit");
            listener.setSoTimeout(200);
            try { listener.accept().close(); throw new AssertionError("Unexpected reconnect"); }
            catch (SocketTimeoutException expected) { AgentHarness.check(true, "no reconnect after JVM exit"); }
        } finally {
            if (process != null && process.isAlive()) process.destroyForcibly();
            Files.deleteIfExists(mapping);
            Files.deleteIfExists(runtimeMapping);
        }
    }
}
