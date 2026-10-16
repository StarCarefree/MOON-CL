package mooncl.agent;

import java.io.*;
import java.lang.reflect.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;
import java.util.concurrent.Callable;

/** Loader-namespace regression fixtures. No Minecraft binaries. */
final class RuntimeMappingsHarness {
    enum InterType { OTHER, field_100, field_101 }
    public static class InterData {
        final InterType type;
        public InterData(String name, String ip, InterType type) { this.type = type; }
    }
    static BufferedReader reader(String text) { return new BufferedReader(new StringReader(text)); }
    static void check(boolean ok, String what) { AgentHarness.check(ok, what); }
    static Mappings official(boolean enumFixture) throws Exception {
        Mappings m = AgentHarness.mappings(AgentHarness.Connect5.class);
        if (enumFixture) m.parse(reader(
                "net.minecraft.client.multiplayer.ServerData -> xyz:\n" +
                "net.minecraft.client.multiplayer.ServerData$Type -> xyz$a:\n" +
                "    net.minecraft.client.multiplayer.ServerData$Type LAN -> a\n" +
                "    net.minecraft.client.multiplayer.ServerData$Type OTHER -> b\n" +
                "    net.minecraft.client.multiplayer.ServerData$Type REALM -> c\n"));
        return m;
    }
    static String loaderFile(Mappings m, String format, boolean enumFixture) {
        boolean tiny1 = format.equals("tiny1"), tiny2 = format.equals("tiny2"), tsrg2 = format.equals("tsrg2");
        StringBuilder out = new StringBuilder(tiny1 ? "v1\tofficial\tintermediary\n" : tiny2 ? "tiny\t2\t0\tofficial\tintermediary\n" : tsrg2 ? "tsrg2 obf srg id\n" : "");
        for (Map.Entry<String, String> e : m.classes.entrySet()) {
            String owner = e.getValue(), target = owner;
            if (enumFixture && owner.equals("xyz")) target = InterData.class.getName();
            if (enumFixture && owner.equals("xyz$a")) target = InterType.class.getName();
            String source = owner.replace('.', '/'), dest = target.replace('.', '/');
            out.append(tiny1 ? "CLASS\t" : tiny2 ? "c\t" : "").append(source)
                    .append(tiny1 || tiny2 ? "\t" : " ").append(dest).append(tsrg2 ? " 999" : "").append('\n');
            for (Mappings.Member member : m.members.get(e.getKey())) {
                boolean field = member.args == null;
                String runtime = member.runtime;
                if (member.name.equals("clearLevel")) runtime = "m_12345_";
                if (member.name.equals("disconnect")) runtime = "m_67890_";
                if (owner.equals("xyz$a")) runtime = member.name.equals("LAN") ? "OTHER" : member.name.equals("OTHER") ? "field_100" : "field_101";
                String desc = m.descriptor(member);
                if (tiny1) out.append(field ? "FIELD\t" : "METHOD\t").append(source).append('\t').append(desc).append('\t').append(member.runtime).append('\t').append(runtime).append('\n');
                else if (tiny2) {
                    out.append(field ? "\tf\t" : "\tm\t").append(desc).append('\t').append(member.runtime).append('\t').append(runtime).append('\n');
                    if (!field) out.append("\t\tp\t0\ta\tb\n\t\t\tc\tparameter comment\n\t\tv\t1\t2\t3\tx\ty\n");
                } else {
                    out.append('\t').append(member.runtime).append(' ');
                    if (!field) out.append(desc).append(' ');
                    out.append(runtime).append(tsrg2 ? " 123" : "").append('\n');
                    if (tsrg2 && !field) out.append("\t\tstatic\n\t\t0 o p_999_ 999\n");
                }
            }
        }
        return out.toString();
    }
    static void run() throws Exception {
        for (String format : new String[] {"tiny1", "tiny2", "tsrg1", "tsrg2"}) {
            Mappings m = official(true);
            m.link = RuntimeMappings.parse(reader(loaderFile(m, format, true)));
            int expectedMembers = 0;
            for (List<Mappings.Member> list : m.members.values()) expectedMembers += list.size();
            check(m.link.members.size() == expectedMembers, "v2 metadata does not become members " + format);
            check(m.link.srg == format.startsWith("tsrg"), "format namespace " + format);
            check(m.link.classes.get("xyz$a").equals(InterType.class.getName()), "inner enum class " + format);
            try (AgentHarness.Client c = new AgentHarness.Client()) {
                MinecraftBridge bridge = new MinecraftBridge(c, m);
                InterData data = (InterData) bridge.makeData("fixture.example");
                check(data.type == InterType.field_100, "OTHER enum chained, not ordinal or raw-name decoy " + format);
                check(!bridge.map.fieldName(InterType.class.getDeclaredField("OTHER"), "OTHER"), "unrelated runtime OTHER field rejected " + format);
                check(bridge.map.fieldName(InterType.class.getDeclaredField("field_100"), "OTHER"), "mapped enum OTHER recognized " + format);
                check(m.runtime("net.minecraft.client.multiplayer.ServerData").equals("xyz"), "source mapping unchanged " + format);
            }
            final Mappings cleanup = official(false);
            cleanup.link = RuntimeMappings.parse(reader(loaderFile(cleanup, format, false)));
            try (final AgentHarness.Client c = new AgentHarness.Client()) {
                final MinecraftBridge bridge = new MinecraftBridge(c, cleanup);
                Method clean = AgentHarness.Client.class.getDeclaredMethod("m_12345_", AgentHarness.Screen.class);
                check(bridge.map.methodName(clean, "clearLevel"), "chained SRG clearLevel descriptor " + format);
                check(!bridge.map.methodName(AgentHarness.Client.class.getDeclaredMethod("m_12345_", int.class), "clearLevel"), "wrong overload rejected " + format);
                check(!bridge.map.methodName(AgentHarness.Client.class.getDeclaredMethod("x", AgentHarness.Screen.class), "clearLevel"), "obf member not reused in runtime namespace " + format);
                AgentHarness.on(c, new Callable<Void>() { public Void call() throws Exception {
                    c.join("previous.example"); AgentHarness.World old = c.a;
                    bridge.connect("next.example");
                    check(old.leaves == 1 && c.cleans == 1 && c.starts == 1, "actual chained world disconnect and client cleanup before switch");
                    return null;
                }});
            }
        }
        Mappings mixed = new Mappings();
        mixed.parse(reader("net.minecraft.client.Minecraft -> abc:\n" +
                "    1:2:void clearLevel(java.lang.Object) -> a\n" +
                "    java.lang.String currentServer -> b\n"));
        mixed.link = RuntimeMappings.parse(reader("tsrg2 obf srg id\nabc net/minecraft/src/C_123_ 123\n\ta (Ljava/lang/Object;)V m_12345_ 1\n\t\t0 o p_1_ 1\n\tb f_12345_ 2\n"));
        Mappings selected = mixed.select(net.minecraft.client.Minecraft.class);
        check(selected.runtime(Mappings.CLIENT).equals(Mappings.CLIENT), "Forge named classes retained, not C_123_");
        check(selected.methodName(net.minecraft.client.Minecraft.class.getDeclaredMethod("m_12345_", Object.class), "clearLevel"), "Forge named class plus SRG method");
        check(selected.fieldName(net.minecraft.client.Minecraft.class.getDeclaredField("f_12345_"), "currentServer"), "Forge named class plus SRG field");
        check(!selected.methodName(net.minecraft.client.Minecraft.class.getDeclaredMethod("m_12345_", int.class), "clearLevel"), "Forge overload guard");
        check(!selected.fieldName(net.minecraft.client.Minecraft.class.getDeclaredField("f_12345_"), "ip"), "Forge unrelated field guard");
        check(!selected.fieldName(net.minecraft.client.Minecraft.class.getDeclaredField("currentServer"), "currentServer"), "raw named decoy not confused with SRG mapped field");
        Mappings noLink = new Mappings();
        check(noLink.select(net.minecraft.client.Minecraft.class) == noLink, "latest named no client_mappings still supported");
        Mappings named = new Mappings(); named.parse(reader("net.minecraft.client.Minecraft -> abc:\n    java.lang.String currentServer -> b\n"));
        Mappings namedView = named.select(net.minecraft.client.Minecraft.class);
        check(namedView.runtime(Mappings.CLIENT).equals(Mappings.CLIENT), "normal named selected even with official obf mappings supplied");
        check(!namedView.fieldName(net.minecraft.client.Minecraft.class.getDeclaredField("f_12345_"), "currentServer"), "SRG not guessed without loader map");
        check(namedView.fieldName(net.minecraft.client.Minecraft.class.getDeclaredField("currentServer"), "currentServer"), "normal named mapped field path retained");
        Mappings obfuscated = official(false);
        obfuscated.link = RuntimeMappings.parse(reader("v1\tofficial\tintermediary\nCLASS\t" + AgentHarness.Client.class.getName().replace('.', '/') + "\tnet/minecraft/class_310\n"));
        check(obfuscated.select(AgentHarness.Client.class) == obfuscated, "actual official obf client ignores unrelated loader namespace");
        check(obfuscated.methodName(AgentHarness.Client.class.getDeclaredMethod("x", AgentHarness.Screen.class), "clearLevel"), "official obf method remains recognized");
        check(mixed.select(AgentHarness.Client.class) == mixed, "unrecognized client does not select arbitrary namespace");
        check(Mappings.typeDescriptor("java.lang.String[][]").equals("[[Ljava/lang/String;"), "array descriptor");
        check(Mappings.typeDescriptor("boolean").equals("Z"), "primitive descriptor");
        final String[] invalid = {
                "tiny\t2\t0\tintermediary\tofficial\nc\tx\ty\n",
                "tiny\t2\t0\tofficial\tnamed\nc\tx\ty\n",
                "tiny\t2\t0\tofficial\tintermediary\n\tescaped-names\nc\tx\ty\n",
                "tsrg2 srg obf\nx y\n",
                "v1\tofficial\tintermediary\nCLASS\ta\tb\nCLASS\ta\tc\n"
        };
        for (final String text : invalid) AgentHarness.rejects(new AgentHarness.Checked() { public void run() throws Exception {
            RuntimeMappings.parse(reader(text));
        }}, "unsupported/conflicting runtime format");
        RuntimeMappings extraNamespace = RuntimeMappings.parse(reader("tiny\t2\t0\tofficial\tnamed\tintermediary\nc\ta\tNamed\tclass_1\n\tf\tLa;\tb\tField\tfield_1\n"));
        check(extraNamespace.classes.get("a").equals("class_1"), "Tiny namespace selected by header, not fixed column");
        check(extraNamespace.member("a", "b", "La;", true).equals("field_1"), "Tiny field descriptor source namespace");
        Path root = Paths.get(System.getenv("PI_SCRATCH_DIR") == null ? System.getProperty("java.io.tmpdir") : System.getenv("PI_SCRATCH_DIR"));
        Path file = Files.createTempFile(root, "mooncl-runtime-", ".tiny");
        try {
            Files.write(file, "v1\tofficial\tintermediary\nCLASS\ta\tb\n".getBytes(StandardCharsets.UTF_8));
            Mappings loaded = new Mappings(); loaded.loadRuntime(file.toString());
            check(loaded.link.classes.get("a").equals("b"), "optional runtime path loaded");
            loaded.loadRuntime(file.toString() + ".missing");
            check(loaded.link != null, "missing optional runtime path is harmless");
            Files.write(file, "bad-format".getBytes(StandardCharsets.UTF_8));
            final Mappings atomic = new Mappings(); final String path = file.toString();
            AgentHarness.rejects(new AgentHarness.Checked() { public void run() throws Exception { atomic.loadRuntime(path); } }, "bad runtime file");
            check(atomic.link == null, "failed runtime parse not partially installed");
        } finally { Files.deleteIfExists(file); }
    }
}
