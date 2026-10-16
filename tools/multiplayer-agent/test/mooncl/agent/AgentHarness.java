package mooncl.agent;

import java.io.*;
import java.lang.instrument.Instrumentation;
import java.lang.reflect.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.*;

/** Synthetic fixtures ONLY, not Minecraft binaries or an in-game test. */
public final class AgentHarness {
    static int assertions;
    static final String TOKEN = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    static void check(boolean ok, String what) { assertions++; if (!ok) throw new AssertionError(what); }
    interface Checked { void run() throws Exception; }
    static void rejects(Checked action, String what) throws Exception {
        try { action.run(); } catch (Exception expected) { assertions++; return; }
        throw new AssertionError("Expected rejection: " + what);
    }
    public static class Screen { }
    public static class World { int leaves; public void v() { leaves++; } public void m_67890_() { v(); } }
    public static class Player { }
    public static class Handler { }
    public static class Integrated { }
    public static class Data {
        public String n, z;
        public Data(String name, String ip, boolean lan) { n = name; z = ip; }
    }
    public static class EnumData {
        enum Type { LAN, OTHER, REALM }
        final Type type;
        public EnumData(String name, String ip, Type type) { this.type = type; }
    }
    public static class Address {
        final String value; Address(String value) { this.value = value; }
        public static Address a(String s) { return new Address(s); }
        public static Address a(int badOverload) { throw new AssertionError("Wrong parse overload"); }
    }
    public static class Transfer { }
    static int unwantedInitialization;
    public static class Uninitialized { static final Uninitialized INSTANCE = init(); static Uninitialized init() { unwantedInitialization++; return new Uninitialized(); } }
    public static class Client implements Executor, AutoCloseable {
        static Client INSTANCE;
        World a; Player b; Handler c; Integrated d; Screen e = new Screen(); Data f;
        String requested; int starts, cleans, setScreens; boolean hold;
        final BlockingQueue<Runnable> queue = new LinkedBlockingQueue<Runnable>();
        final Thread main;
        volatile boolean alive = true;
        Client() {
            main = new Thread(new Runnable() { public void run() {
                while (alive) try { queue.take().run(); } catch (InterruptedException ignored) { }
            } }, "fixture-minecraft-main"); main.setDaemon(true); main.start();
        }
        public void execute(Runnable r) { if (!hold) queue.add(r); else held.add(r); }
        final List<Runnable> held = new ArrayList<Runnable>();
        void guard() { if (Thread.currentThread() != main) throw new AssertionError("Not Minecraft main thread"); }
        public Handler q() { guard(); return c; }
        public Data s() { guard(); return f; }
        public boolean l() { guard(); return d != null; }
        public boolean o() { guard(); return d != null; }
        public void x(Screen s) { guard(); cleans++; a = null; b = null; c = null; f = null; e = s; }
        public void disconnect(Screen s, boolean transfer) { guard(); check(!transfer, "modern disconnect is not a transfer"); x(s); }
        public void m_12345_(Screen s) { x(s); }
        public void m_12345_(int wrong) { throw new AssertionError("Wrong SRG overload"); }
        public void t(Screen s) { guard(); setScreens++; e = s; }
        void start(Data data, Address address, Screen screen) {
            guard(); check(a == null && b == null, "cleanup before connection");
            requested = address.value; starts++; e = screen;
        }
        void join(String actual) { guard(); a = new World(); b = new Player(); c = new Handler(); f = new Data("Actual", actual, false); e = null; }
        public void close() { alive = false; main.interrupt(); }
    }
    public static class Connect5 extends Screen {
        public static void a(Screen parent, Client c, Address a, Data d, boolean quick) { c.start(d, a, new Connect5()); }
        public static void a(Screen parent, Client c, String wrong, Data d, boolean quick) { throw new AssertionError("Wrong connect overload"); }
    }
    public static class Connect6 extends Screen {
        public static void a(Screen parent, Client c, Address a, Data d, boolean quick, Transfer transfer) {
            check(transfer == null && !quick, "non-transfer normal join"); c.start(d, a, new Connect6());
        }
    }
    public static class Legacy extends Screen {
        public Legacy(Screen parent, Client c, Data d) { c.start(d, new Address(d.z), this); }
    }
    public static class Bad extends Screen {
        public static void a(Screen parent, Client c, String wrong, Data d, boolean quick) { throw new AssertionError("Unknown signature invoked"); }
    }
    public static class Ambiguous extends Screen {
        public static void a(Screen p, Client c, Address a, Data d, boolean q) { throw new AssertionError(); }
        public static void b(Screen p, Client c, Address a, Data d, boolean q) { throw new AssertionError(); }
    }
    static Mappings mappings(Class<?> connect) throws Exception {
        String[][] classes = {
            {"net.minecraft.client.Minecraft", Client.class.getName()},
            {"net.minecraft.client.gui.screens.Screen", Screen.class.getName()},
            {"net.minecraft.client.multiplayer.ServerData", Data.class.getName()},
            {"net.minecraft.client.multiplayer.resolver.ServerAddress", Address.class.getName()},
            {"net.minecraft.client.multiplayer.ClientLevel", World.class.getName()},
            {"net.minecraft.client.player.LocalPlayer", Player.class.getName()},
            {"net.minecraft.client.multiplayer.ClientPacketListener", Handler.class.getName()},
            {"net.minecraft.client.server.IntegratedServer", Integrated.class.getName()},
            {"net.minecraft.client.gui.screens.ConnectScreen", connect.getName()},
            {"net.minecraft.client.multiplayer.TransferState", Transfer.class.getName()}
        };
        StringBuilder text = new StringBuilder("# Official ProGuard syntax fixture\n");
        for (String[] c : classes) {
            text.append(c[0]).append(" -> ").append(c[1]).append(":\n");
            if (c[1].equals(Client.class.getName())) text.append(
                "    1:2:net.minecraft.client.multiplayer.ClientPacketListener getConnection():3:4 -> q\n" +
                "    1:2:net.minecraft.client.multiplayer.ServerData getCurrentServer() -> s\n" +
                "    3:4:boolean isLocalServer() -> l\n    5:6:boolean hasSingleplayerServer() -> o\n" +
                "    7:8:void clearLevel(net.minecraft.client.gui.screens.Screen) -> x\n" +
                "    9:10:void setScreen(net.minecraft.client.gui.screens.Screen) -> t\n");
            if (c[1].equals(Data.class.getName())) text.append("    java.lang.String name -> n\n    java.lang.String ip -> z\n");
            if (c[1].equals(World.class.getName())) text.append("    1:2:void disconnect() -> v\n");
        }
        Mappings m = new Mappings(); m.parse(new BufferedReader(new StringReader(text.toString()))); return m;
    }
    static <T> T on(Client c, Callable<T> action) throws Exception {
        FutureTask<T> task = new FutureTask<T>(action); c.execute(task); return task.get(3, TimeUnit.SECONDS);
    }
    static void parserAndAddress() throws Exception {
        Mappings m = new Mappings(); m.parse(new BufferedReader(new StringReader(
            "# comment\nexample.Owner -> a:\n    java.lang.String ip -> b\n" +
            "    12:20:void connect(java.lang.String,int):3:4 -> c\n" +
            "    22:23:void connect(int) -> d\nexample.Value -> e:\n")));
        check(m.runtime("example.Owner").equals("a"), "class mapping");
        check(m.runtime("example.Value[][]").equals("e[][]"), "array mapping");
        check(m.members.get("example.Owner").size() == 3, "field and overloaded methods parsed");
        check(m.members.get("example.Owner").get(1).args.equals(Arrays.asList("java.lang.String", "int")), "line-range method signature");
        Mappings f = mappings(Connect5.class);
        check(f.methodName(Client.class.getDeclaredMethod("x", Screen.class), "clearLevel"), "mapped method full signature");
        check(!f.methodName(Client.class.getDeclaredMethod("x", Screen.class), "getCurrentServer"), "unrelated alias rejected");
        check(f.fieldName(Data.class.getDeclaredField("z"), "ip"), "mapped field alias");
        check(Mappings.load("Z:/mooncl-nonexistent-mappings.txt").classes.isEmpty(), "missing mapping fallback");
        for (String s : new String[] {"localhost", "example.org:25565", "127.0.0.1", "[::1]:25565", "2001:db8::1", "例子.测试", "mc.example.org."})
            check(Protocol.address(s).equals(s), "accepted " + s);
        for (final String s : new String[] {"", " host", "host ", "http://host", "host/abc", "user@host", "host:0", "host:65536", "host:", "999.1.1.1", "[bad]:1", "host\nxx", "a..b", "::g", "-bad.example", "[::1]junk"})
            rejects(new Checked() { public void run() { Protocol.address(s); } }, "invalid address " + s);
        rejects(new Checked() { public void run() throws Exception { Protocol.decode("/w=="); } }, "malformed UTF8");
        rejects(new Checked() { public void run() { Protocol.port("+123"); } }, "nondecimal port");
        rejects(new Checked() { public void run() { new Protocol(123, "short", null); } }, "short token");
        byte[] max = new byte[Protocol.MAX_LINE + 1]; Arrays.fill(max, (byte)'a'); max[max.length - 1] = '\n';
        check(new Protocol.Lines(new ByteArrayInputStream(max)).read().length() == Protocol.MAX_LINE, "16KiB line accepted");
        final byte[] over = new byte[Protocol.MAX_LINE + 2]; Arrays.fill(over, (byte)'a'); over[over.length - 1] = '\n';
        rejects(new Checked() { public void run() throws Exception { new Protocol.Lines(new ByteArrayInputStream(over)).read(); } }, "oversize line");
        check(new Protocol.Lines(new ByteArrayInputStream("OK\r\n".getBytes(StandardCharsets.UTF_8))).read().equals("OK"), "CRLF");
    }
    static void adapters() throws Exception {
        for (final Class<?> connect : new Class<?>[] {Connect5.class, Connect6.class, Legacy.class}) {
            try (final Client c = new Client()) {
                final MinecraftBridge b = new MinecraftBridge(c, mappings(connect));
                on(c, new Callable<Void>() { public Void call() throws Exception {
                    check(b.observe().state.equals("ready"), "initial ready");
                    check(b.connect("request.example").state.equals("connecting"), "connect accepted is not connected");
                    check(b.observe().state.equals("connecting"), "actual connect screen");
                    rejects(new Checked() { public void run() throws Exception { b.connect("reentrant.example"); } }, "connection screen reentry");
                    rejects(new Checked() { public void run() throws Exception { b.disconnect(); } }, "connection must be cancelled manually");
                    check(c.starts == 1 && c.requested.equals("request.example"), "correct overload/constructor");
                    check(connect != Legacy.class || c.setScreens == 1, "legacy constructor then setScreen");
                    c.join("actual.example:25566");
                    Protocol.State state = b.observe();
                    check(state.state.equals("connected") && state.address.equals("actual.example:25566"), "actual server, not requested address");
                    World old = c.a;
                    b.connect("second.example");
                    check(old.leaves == 1 && c.cleans == 1 && c.starts == 2, "multiplayer switch disconnects and cleans first");
                    c.join("manual.example");
                    check(b.observe().address.equals("manual.example"), "manual join recognized");
                    check(b.disconnect().state.equals("disconnected"), "disconnect actual world");
                    c.join("singleplayer"); c.d = new Integrated(); World single = c.a;
                    check(b.observe().state.equals("unsupported"), "singleplayer reported");
                    rejects(new Checked() { public void run() throws Exception { b.connect("unsafe.example"); } }, "singleplayer switch rejected");
                    rejects(new Checked() { public void run() throws Exception { b.disconnect(); } }, "singleplayer disconnect rejected");
                    check(c.a == single && single.leaves == 0 && c.starts == 2, "singleplayer untouched");
                    c.a = null; c.b = null; c.c = null;
                    rejects(new Checked() { public void run() throws Exception { b.connect("unsafe-loading.example"); } }, "integrated server without world is protected");
                    c.d = null; c.c = new Handler();
                    check(b.observe().state.equals("connecting"), "handler alone never connected");
                    rejects(new Checked() { public void run() throws Exception { b.connect("overlap.example"); } }, "network transition prevents switch");
                    return null;
                }});
            }
        }
        for (final Class<?> unsupported : new Class<?>[] {Bad.class, Ambiguous.class}) {
            try (final Client c = new Client()) {
                final MinecraftBridge b = new MinecraftBridge(c, mappings(unsupported));
                on(c, new Callable<Void>() { public Void call() throws Exception {
                    c.join("existing.example"); final World w = c.a;
                    rejects(new Checked() { public void run() throws Exception { b.connect("new.example"); } }, "unknown signature");
                    check(c.a == w && w.leaves == 0 && c.cleans == 0, "unsupported plan leaves existing server intact"); return null;
                }});
            }
        }
        try (Client c = new Client()) {
            Mappings m = mappings(Connect5.class); m.classes.put("net.minecraft.client.multiplayer.ServerData", EnumData.class.getName());
            check(((EnumData)new MinecraftBridge(c, m).makeData("example.org")).type == EnumData.Type.OTHER, "enum ServerData constructor");
        }
        check(!MinecraftBridge.initialized(Uninitialized.class) && unwantedInitialization == 0, "loaded class not initialized by probe");
        check(MinecraftBridge.singletonValue(Uninitialized.class.getDeclaredField("INSTANCE")) == null && unwantedInitialization == 0,
                "raw singleton storage read must not trigger class initialization");
        try (final Client c = new Client()) {
            Mappings modern = mappings(Connect6.class);
            modern.members.get(Mappings.CLIENT).removeIf(member -> member.name.equals("clearLevel"));
            modern.members.get(Mappings.CLIENT).add(new Mappings.Member("void", "disconnect", "disconnect",
                    Arrays.asList("net.minecraft.client.gui.screens.Screen", "boolean")));
            final MinecraftBridge bridge = new MinecraftBridge(c, modern);
            on(c, new Callable<Void>() { public Void call() throws Exception {
                c.join("modern.example"); World old = c.a;
                bridge.disconnectWorld();
                check(c.cleans == 1 && old.leaves == 0 && c.a == null && c.b == null && c.c == null,
                        "modern Minecraft teardown owns network/world cleanup without old ClientLevel.disconnect");
                return null;
            }});
        }
        try (Client c = new Client()) {
            Client.INSTANCE = c; final Mappings m = mappings(Connect5.class);
            Instrumentation i = (Instrumentation) java.lang.reflect.Proxy.newProxyInstance(AgentHarness.class.getClassLoader(), new Class<?>[] {Instrumentation.class},
                new InvocationHandler() { public Object invoke(Object p, Method method, Object[] args) {
                    if (method.getName().equals("getAllLoadedClasses")) return new Class<?>[] {Uninitialized.class, Client.class};
                    throw new AssertionError(method.getName());
                }});
            check(MinecraftBridge.provider(i, m).discover().client == c, "instrumentation initialized singleton discovery");
            Client.INSTANCE = null;
            check(MinecraftBridge.provider(i, m).discover() == null, "null singleton means waiting");
        }
        try (Client c = new Client()) {
            MinecraftBridge b = new MinecraftBridge(c, mappings(Connect5.class));
            Protocol.Job cancelled = new Protocol.Job(b, new Protocol.Command("cancel", "late.example"));
            cancelled.phase.set(3); cancelled.run();
            check(c.starts == 0 && cancelled.phase.get() == 2, "cancelled CONNECT runnable is inert when eventually drained");
        }
    }
    static final class Session implements AutoCloseable {
        final ServerSocket listener; final Protocol protocol; final Thread worker; final Socket socket;
        final BufferedReader in; final PrintWriter out;
        Session(final MinecraftBridge bridge, String reply, long timeout) throws Exception {
            listener = new ServerSocket(0, 1, InetAddress.getByAddress(new byte[] {127,0,0,1})); listener.setSoTimeout(3000);
            protocol = new Protocol(listener.getLocalPort(), TOKEN, new MinecraftBridge.Provider() { public MinecraftBridge discover() { return bridge; } }, timeout);
            worker = new Thread(new Runnable() { public void run() { protocol.run(); } }, "fixture-agent-link"); worker.setDaemon(true); worker.start();
            socket = listener.accept(); socket.setSoTimeout(3500);
            in = new BufferedReader(new InputStreamReader(socket.getInputStream(), StandardCharsets.UTF_8));
            out = new PrintWriter(new OutputStreamWriter(socket.getOutputStream(), StandardCharsets.UTF_8), true);
            check(in.readLine().equals("HELLO\t1\t" + TOKEN), "HELLO version and exact token");
            out.println(reply);
        }
        String[] state(String id, String value) throws Exception {
            long end = Protocol.now() + 4000;
            while (Protocol.now() < end) {
                String line = in.readLine(); if (line == null) throw new AssertionError("Unexpected EOF");
                String[] p = line.split("\t", -1);
                check(p.length == 5 && p[0].equals("STATE"), "STATE framing");
                Protocol.decode(p[3]); Protocol.decode(p[4]);
                if (p[1].equals(id) && p[2].equals(value)) return p;
            }
            throw new AssertionError("Missing state " + id + " / " + value);
        }
        public void close() throws Exception { socket.close(); protocol.close(); listener.close(); worker.join(2000); check(!worker.isAlive(), "link exits with no reconnect"); }
    }
    static void protocol() throws Exception {
        try (final Client c = new Client()) {
            final MinecraftBridge b = new MinecraftBridge(c, mappings(Connect5.class));
            try (Session s = new Session(b, "NO", 1000)) {
                check(s.in.readLine() == null, "bad authentication sends no STATE/executes no command");
                check(c.starts == 0, "authentication gate");
            }
            try (Session s = new Session(b, "OK", 15000)) {
                s.state("", "waiting"); s.state("", "ready");
                s.out.println("CONNECT\tr1\t" + Protocol.b64("requested.example"));
                String[] ack = s.state("r1", "connecting");
                check(Protocol.decode(ack[3]).isEmpty(), "ack does not pretend request is actual address");
                on(c, new Callable<Void>() { public Void call() { c.join("real.example"); return null; }});
                check(Protocol.decode(s.state("", "connected")[3]).equals("real.example"), "polled actual connected address");
                s.out.println("CONNECT\tr2\t" + Protocol.b64("second.example")); s.state("r2", "connecting");
                check(c.cleans == 1 && c.starts == 2, "protocol switch ran on main thread");
                on(c, new Callable<Void>() { public Void call() { c.join("manual.example"); return null; }});
                s.state("", "connected");
                s.out.println("DISCONNECT\tr3"); s.state("r3", "disconnected");
                s.out.println("DISCONNECT\tr3"); s.state("r3", "error");
                s.out.println("CONNECT\tbad\t%%%notbase64"); s.state("bad", "error");
                on(c, new Callable<Void>() { public Void call() { c.join("manual2.example"); return null; }});
                check(Protocol.decode(s.state("", "connected")[3]).equals("manual2.example"), "manual server entry feedback");
                on(c, new Callable<Void>() { public Void call() { c.x(null); return null; }});
                s.state("", "disconnected");
            }
        }
        try (final Client c = new Client()) {
            MinecraftBridge b = new MinecraftBridge(c, mappings(Connect5.class)); c.hold = true;
            try (Session s = new Session(b, "OK", 150)) {
                s.state("", "waiting");
                s.out.println("CONNECT\tqueued\t" + Protocol.b64("too-late.example"));
                s.out.println("DISCONNECT\toverflow"); s.state("overflow", "error");
                s.state("queued", "error");
                Thread.sleep(450);
                check(c.held.size() == 1, "stalled executor queue stays bounded");
                for (Runnable r : c.held) r.run();
                check(c.starts == 0, "timed-out queued task never connects later");
            }
        }
        try (Session s = new Session(null, "OK", 500)) {
            s.state("", "waiting"); s.out.println("DISCONNECT\tnotready"); s.state("notready", "error");
        }
    }
    public static void main(String[] args) throws Exception {
        parserAndAddress(); adapters(); protocol(); RuntimeMappingsHarness.run(); PremainProbe.run();
        System.out.println("PASS " + assertions + " assertions (synthetic clients; NOT real-game validation)");
    }
}
