package mooncl.agent;

import java.lang.instrument.Instrumentation;
import java.lang.reflect.*;
import java.util.*;
import java.util.concurrent.Executor;

/** Conservative, signature-checked reflection. All instance access happens inside Executor.execute. */
final class MinecraftBridge {
    static final class Unsupported extends Exception { Unsupported(String message) { super(message); } }
    interface Provider { MinecraftBridge discover() throws Exception; }
    final Object client;
    final Executor executor;
    final Mappings map;
    final ClassLoader loader;
    final Class<?> mc, screen, data, address, world, player, handler, integrated;
    boolean seenSession;

    static Provider provider(final Instrumentation instrumentation, final Mappings mappings) {
        return new Provider() {
            public MinecraftBridge discover() throws Exception {
                for (Class<?> c : instrumentation.getAllLoadedClasses()) {
                    if (!isClient(c, mappings)) continue;
                    // Read the singleton storage without triggering <clinit>, including on Java 22+.
                    Object singleton = null;
                    for (Field f : c.getDeclaredFields()) {
                        if (Modifier.isStatic(f.getModifiers()) && f.getType() == c) {
                            Object value = singletonValue(f);
                            if (value != null) {
                                if (singleton != null && singleton != value) throw new Unsupported("Ambiguous Minecraft singleton");
                                singleton = value;
                            }
                        }
                    }
                    if (singleton != null) return new MinecraftBridge(singleton, mappings);
                }
                return null;
            }
        };
    }
    static boolean isClient(Class<?> c, Mappings m) {
        return m.isClient(c);
    }
    static boolean initialized(Class<?> c) throws Unsupported {
        for (Field field : c.getDeclaredFields())
            if (Modifier.isStatic(field.getModifiers()) && field.getType() == c && singletonValue(field) != null) return true;
        return false;
    }
    static Object singletonValue(Field field) throws Unsupported {
        try {
            Class<?> type = Class.forName("sun.misc.Unsafe");
            Field instance = type.getDeclaredField("theUnsafe"); instance.setAccessible(true);
            Object unsafe = instance.get(null);
            Object base = type.getMethod("staticFieldBase", Field.class).invoke(unsafe, field);
            long offset = (Long) type.getMethod("staticFieldOffset", Field.class).invoke(unsafe, field);
            return type.getMethod("getObjectVolatile", Object.class, long.class).invoke(unsafe, base, offset);
        } catch (ReflectiveOperationException e) {
            throw new Unsupported("VM does not permit non-initializing singleton reads");
        }
    }
    MinecraftBridge(Object client, Mappings map) throws Exception {
        this.map = map.select(client.getClass());
        this.client = client; mc = client.getClass(); loader = mc.getClassLoader();
        if (!(client instanceof Executor)) throw new Unsupported("Minecraft does not implement java.util.concurrent.Executor");
        executor = (Executor) client;
        screen = role("net.minecraft.client.gui.screens.Screen", "net.minecraft.client.gui.screen.Screen", "net.minecraft.class_437");
        data = role("net.minecraft.client.multiplayer.ServerData", "net.minecraft.client.network.ServerInfo", "net.minecraft.class_642");
        address = role("net.minecraft.client.multiplayer.resolver.ServerAddress", "net.minecraft.client.multiplayer.ServerAddress",
                "net.minecraft.client.network.ServerAddress", "net.minecraft.class_639");
        world = role("net.minecraft.client.multiplayer.ClientLevel", "net.minecraft.client.world.ClientWorld", "net.minecraft.class_638");
        player = role("net.minecraft.client.player.LocalPlayer", "net.minecraft.client.entity.player.ClientPlayerEntity",
                "net.minecraft.client.network.ClientPlayerEntity", "net.minecraft.class_746");
        handler = role("net.minecraft.client.multiplayer.ClientPacketListener", "net.minecraft.client.network.play.ClientPlayNetHandler",
                "net.minecraft.client.network.ClientPlayNetworkHandler", "net.minecraft.class_634");
        integrated = role("net.minecraft.client.server.IntegratedServer", "net.minecraft.server.integrated.IntegratedServer", "net.minecraft.class_1132");
    }
    Class<?> role(String... names) throws Unsupported {
        for (String name : names) {
            for (String candidate : new String[] {map.runtime(name), name})
                try { return Class.forName(candidate, false, loader); }
                catch (ClassNotFoundException ignored) { }
        }
        throw new Unsupported("Class unavailable: " + names[0]);
    }
    static List<Field> fields(Class<?> c) {
        List<Field> result = new ArrayList<Field>();
        for (; c != null; c = c.getSuperclass()) Collections.addAll(result, c.getDeclaredFields());
        return result;
    }
    static List<Method> methods(Class<?> c) {
        // Prefer overrides over inherited implementations; never arbitrarily choose an overload.
        Map<String, Method> result = new LinkedHashMap<String, Method>();
        for (; c != null; c = c.getSuperclass()) for (Method m : c.getDeclaredMethods()) {
            if (m.isBridge() || m.isSynthetic()) continue;
            String key = m.getName() + Arrays.toString(m.getParameterTypes());
            if (!result.containsKey(key)) result.put(key, m);
        }
        return new ArrayList<Method>(result.values());
    }
    Field field(Class<?> owner, Class<?> type, boolean required, String... names) throws Unsupported {
        List<Field> typed = new ArrayList<Field>(), named = new ArrayList<Field>();
        for (Field f : fields(owner)) if (!Modifier.isStatic(f.getModifiers()) && f.getType() == type) {
            typed.add(f); if (map.fieldName(f, names)) named.add(f);
        }
        List<Field> candidates = named.isEmpty() ? typed : named;
        if (candidates.size() == 1) { Field f = candidates.get(0); f.setAccessible(true); return f; }
        if (required) throw new Unsupported("Missing/ambiguous field " + owner.getName() + " : " + type.getName());
        return null;
    }
    Method method(Class<?> owner, boolean statik, Class<?> result, Class<?>[] params, boolean signatureOnly,
                  String... names) throws Unsupported {
        List<Method> candidates = new ArrayList<Method>(), named = new ArrayList<Method>();
        for (Method m : methods(owner)) {
            if (Modifier.isStatic(m.getModifiers()) != statik || m.getReturnType() != result
                    || !Arrays.equals(m.getParameterTypes(), params)) continue;
            candidates.add(m); if (map.methodName(m, names)) named.add(m);
        }
        List<Method> selected = named.isEmpty() && signatureOnly ? candidates : named;
        if (selected.size() > 1) throw new Unsupported("Ambiguous method: " + owner.getName() + " " + Arrays.toString(names));
        if (selected.isEmpty()) return null;
        Method m = selected.get(0); m.setAccessible(true); return m;
    }
    Method require(Method m, String description) throws Unsupported {
        if (m == null) throw new Unsupported("Unsupported signature: " + description); return m;
    }
    Object value(Field field, Object instance) throws Exception { return field == null ? null : field.get(instance); }
    Object level() throws Exception { return field(mc, world, true, "level", "world", "field_71441_e", "field_1687").get(client); }
    Object player() throws Exception { return field(mc, player, true, "player", "field_71439_g", "field_1724").get(client); }
    boolean local() throws Exception {
        Field f = field(mc, integrated, false, "singleplayerServer", "integratedServer");
        if (f != null && f.get(client) != null) return true;
        boolean known = f != null;
        // isLocalServer and hasSingleplayerServer coexist in Mojmap; evaluate each, not as ambiguous alternatives.
        for (String name : new String[] {"isLocalServer", "hasSingleplayerServer", "isInSingleplayer", "func_71387_A", "method_1542"}) {
            Method m = method(mc, false, boolean.class, new Class<?>[0], false, name);
            if (m != null) { known = true; if ((Boolean) m.invoke(client)) return true; }
        }
        if (known) return false;
        throw new Unsupported("Cannot prove this is not a singleplayer world");
    }
    Object connection(Object p) throws Exception {
        Method m = method(mc, false, handler, new Class<?>[0], true,
                "getConnection", "getNetworkHandler", "func_147114_u", "method_1562");
        if (m != null) return m.invoke(client);
        Field f = field(mc, handler, false, "connection", "networkHandler");
        if (f != null) return f.get(client);
        if (p != null) return field(p.getClass(), handler, true, "connection", "networkHandler").get(p);
        // Check the player TYPE even when absent, so missing mappings do not pretend to be ready.
        field(player, handler, true, "connection", "networkHandler"); return null;
    }
    Object currentServer() throws Exception {
        Method m = method(mc, false, data, new Class<?>[0], true,
                "getCurrentServer", "getCurrentServerEntry", "func_147104_D", "method_1558");
        if (m != null) return m.invoke(client);
        return field(mc, data, true, "currentServer", "currentServerEntry").get(client);
    }
    String actualAddress() throws Exception {
        Object server = currentServer(); if (server == null) return "";
        // ServerData has multiple String fields: NO unique-String-field guessing.
        Field selected = null;
        for (Field f : fields(data)) if (!Modifier.isStatic(f.getModifiers()) && f.getType() == String.class
                && map.fieldName(f, "ip", "address", "serverIP", "field_78845_b", "field_3761")) {
            if (selected != null) throw new Unsupported("Ambiguous server address field"); selected = f;
        }
        if (selected == null) throw new Unsupported("No mapped ServerData address field");
        selected.setAccessible(true); String ip = (String) selected.get(server); return ip == null ? "" : ip;
    }
    Protocol.State observe() throws Exception {
        Object w = level(), p = player(), h = connection(p);
        if (local()) return new Protocol.State("unsupported", "", "Singleplayer server: agent will not disconnect or save it");
        if (w != null && p != null && h != null) {
            seenSession = true;
            String ip = actualAddress();
            return new Protocol.State("connected", ip, ip.isEmpty() ? "Network world active; current server address unavailable" : "");
        }
        Field sf = field(mc, screen, true, "screen", "currentScreen", "field_71462_r", "field_1755");
        Object s = sf.get(client);
        Class<?> connect = connectClass();
        if ((s != null && connect.isInstance(s)) || w != null || p != null || h != null)
            return new Protocol.State("connecting", "", "Waiting for complete world/player/network state");
        return new Protocol.State(seenSession ? "disconnected" : "ready", "", "");
    }
    Class<?> connectClass() throws Unsupported {
        return role("net.minecraft.client.gui.screens.ConnectScreen", "net.minecraft.client.gui.screen.ConnectingScreen",
                "net.minecraft.client.gui.screen.multiplayer.ConnectScreen", "net.minecraft.class_412");
    }
    Object makeData(String ip) throws Exception {
        Constructor<?> selected = null; Object argument = null;
        for (Constructor<?> c : data.getDeclaredConstructors()) {
            Class<?>[] p = c.getParameterTypes();
            if (p.length != 3 || p[0] != String.class || p[1] != String.class) continue;
            Object third;
            if (p[2] == boolean.class) third = Boolean.FALSE;
            else if (p[2].isEnum()) {
                third = null;
                for (Field f : p[2].getDeclaredFields()) if (f.isEnumConstant() && map.fieldName(f, "OTHER")) {
                    if (third != null) throw new Unsupported("Ambiguous OTHER enum constant");
                    f.setAccessible(true); third = f.get(null);
                }
                if (third == null) continue;
            } else continue;
            if (selected != null) throw new Unsupported("Ambiguous ServerData constructors");
            selected = c; argument = third;
        }
        if (selected == null) throw new Unsupported("ServerData(String,String,boolean/Type.OTHER) unavailable");
        selected.setAccessible(true); return selected.newInstance("MoonCL", ip, argument);
    }
    static final class ConnectPlan {
        Method start, setScreen; Constructor<?> constructor; Object[] args;
        void run(Object client) throws Exception {
            if (start != null) start.invoke(null, args);
            else setScreen.invoke(client, constructor.newInstance(args));
        }
    }
    ConnectPlan plan(String ip) throws Exception {
        Class<?> cc = connectClass(); Object parent = field(mc, screen, true, "screen", "currentScreen").get(client);
        Object server = makeData(ip);
        Method parse = method(address, true, address, new Class<?>[] { String.class }, true, "parseString", "parse", "func_78860_a", "method_2950");
        List<Method> starts = new ArrayList<Method>();
        Class<?> transfer = null;
        try { transfer = role("net.minecraft.client.multiplayer.TransferState", "net.minecraft.client.network.CookieStorage", "net.minecraft.class_9112"); }
        catch (Unsupported ignored) { }
        for (Method m : methods(cc)) {
            Class<?>[] p = m.getParameterTypes();
            if (!Modifier.isStatic(m.getModifiers()) || m.getReturnType() != void.class || (p.length != 5 && p.length != 6)) continue;
            if (p[0] != screen || p[1] != mc || p[2] != address || p[3] != data || p[4] != boolean.class) continue;
            if (p.length == 6 && (transfer == null || p[5] != transfer)) continue;
            starts.add(m);
        }
        ConnectPlan plan = new ConnectPlan();
        if (starts.size() > 1) throw new Unsupported("Ambiguous ConnectScreen.startConnecting signature");
        if (starts.size() == 1) {
            require(parse, "ServerAddress.parse(String)");
            plan.start = starts.get(0); plan.start.setAccessible(true);
            Object parsed = parse.invoke(null, ip);
            if (parsed == null) throw new Unsupported("ServerAddress parser returned null");
            plan.args = plan.start.getParameterTypes().length == 5
                    ? new Object[] {parent, client, parsed, server, false}
                    : new Object[] {parent, client, parsed, server, false, null};
            return plan;
        }
        try {
            plan.constructor = cc.getDeclaredConstructor(screen, mc, data); plan.constructor.setAccessible(true);
        } catch (NoSuchMethodException e) { throw new Unsupported("No supported ConnectScreen static start or 1.16 constructor"); }
        plan.setScreen = require(method(mc, false, void.class, new Class<?>[] { screen }, true,
                "setScreen", "openScreen", "displayGuiScreen", "func_147108_a", "method_1507"), "setScreen(Screen)");
        plan.args = new Object[] {parent, client, server}; return plan;
    }
    void disconnectWorld() throws Exception {
        Object w = level();
        if (local()) throw new Unsupported("Refusing to switch/disconnect a singleplayer save; leave it manually first");
        if (w == null) {
            if (player() != null || connection(null) != null) throw new Unsupported("Network/world transition active; leave it manually first");
            return;
        }
        // Resolve the complete teardown before doing anything; unknown signatures fail closed.
        Method clean = method(mc, false, void.class, new Class<?>[] {screen}, false,
                "clearLevel", "disconnect", "func_213231_b", "method_18096");
        Object parent = field(mc, screen, true, "screen", "currentScreen").get(client);
        Object[] args = new Object[] {parent};
        if (clean == null) {
            clean = method(mc, false, void.class, new Class<?>[0], false, "clearLevel", "disconnect", "method_18099");
            args = new Object[0];
        }
        if (clean == null) {
            // 26.x: Minecraft owns the complete network/world teardown. ClientLevel's old no-arg disconnect is gone.
            clean = require(method(mc, false, void.class, new Class<?>[] {screen, boolean.class}, false, "disconnect"),
                    "Minecraft.disconnect(Screen,boolean)");
            clean.invoke(client, parent, false);
        } else {
            Method leave = require(method(world, false, void.class, new Class<?>[0], false,
                    "disconnect", "sendQuittingDisconnectingPacket", "func_72882_A", "method_8525"), "ClientLevel.disconnect()");
            leave.invoke(w); clean.invoke(client, args);
        }
        if (level() != null || player() != null || connection(null) != null)
            throw new Unsupported("World/network cleanup incomplete; not starting another connection");
    }
    Protocol.State connect(String ip) throws Exception {
        // Validate the complete connect plan before disconnecting an existing server.
        if (local()) throw new Unsupported("Refusing to switch a singleplayer save; leave it manually first");
        Object w = level(), p = player(), h = connection(p);
        if ((w == null && (p != null || h != null)) || (w != null && (p == null || h == null)))
            throw new Unsupported("World/network transition active; wait for it to complete first");
        ConnectPlan plan = plan(ip);
        Object s = field(mc, screen, true, "screen", "currentScreen").get(client);
        if (s != null && connectClass().isInstance(s)) throw new Unsupported("A connection screen is already active; cancel it manually first");
        disconnectWorld();
        plan.run(client); seenSession = true;
        return new Protocol.State("connecting", "", "Connection started; waiting for actual world/player/network handler");
    }
    Protocol.State disconnect() throws Exception {
        Object s = field(mc, screen, true, "screen", "currentScreen").get(client);
        if (s != null && connectClass().isInstance(s)) throw new Unsupported("Cancel an in-progress connection in Minecraft first");
        disconnectWorld(); seenSession = true; return observe();
    }
}
