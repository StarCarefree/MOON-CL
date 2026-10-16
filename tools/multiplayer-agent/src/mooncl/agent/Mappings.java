package mooncl.agent;

import java.io.*;
import java.lang.reflect.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;

/** Official named -> obf mappings plus an optional obf -> loader-runtime edge. */
final class Mappings {
    final Map<String, String> classes = new HashMap<String, String>();
    final Map<String, List<Member>> members = new HashMap<String, List<Member>>();
    final Map<String, List<Member>> runtimeMembers = new HashMap<String, List<Member>>();
    RuntimeMappings link;
    static final String CLIENT = "net.minecraft.client.Minecraft";
    static final class Member {
        final String type, name, runtime; final List<String> args;
        Member(String type, String name, String runtime, List<String> args) {
            this.type = type; this.name = name; this.runtime = runtime; this.args = args;
        }
    }
    static Mappings load(String path) throws IOException {
        Mappings m = new Mappings();
        if (path == null || path.isEmpty() || !Files.isRegularFile(Paths.get(path))) return m;
        try (BufferedReader r = Files.newBufferedReader(Paths.get(path), StandardCharsets.UTF_8)) { m.parse(r); }
        return m;
    }
    void loadRuntime(String path) throws IOException {
        if (path == null || path.isEmpty() || !Files.isRegularFile(Paths.get(path))) return;
        // Assign only after complete parsing; malformed input cannot leave half a mapping active.
        try (BufferedReader r = Files.newBufferedReader(Paths.get(path), StandardCharsets.UTF_8)) {
            link = RuntimeMappings.parse(r);
        }
    }
    void parse(BufferedReader r) throws IOException {
        String owner = null, line;
        while ((line = r.readLine()) != null) {
            if (line.trim().isEmpty() || line.trim().startsWith("#")) continue;
            int arrow = line.lastIndexOf(" -> ");
            if (arrow < 0) continue;
            String left = line.substring(0, arrow).trim(), right = line.substring(arrow + 4).trim();
            if (!Character.isWhitespace(line.charAt(0)) && right.endsWith(":")) {
                owner = left; classes.put(owner, right.substring(0, right.length() - 1));
                List<Member> list = new ArrayList<Member>();
                members.put(owner, list); runtimeMembers.put(runtime(owner), list); continue;
            }
            if (owner == null) continue;
            left = left.replaceFirst("^(?:[0-9]+:){2}", "");
            int space = left.indexOf(' ');
            if (space < 0) continue;
            String type = left.substring(0, space), rest = left.substring(space + 1);
            int open = rest.indexOf('(');
            List<String> args = null; String name = rest;
            if (open >= 0) {
                int close = rest.indexOf(')', open);
                if (close < 0) continue;
                name = rest.substring(0, open);
                String a = rest.substring(open + 1, close);
                args = a.isEmpty() ? Collections.<String>emptyList() : Arrays.asList(a.split(","));
            }
            members.get(owner).add(new Member(type, name, right, args));
        }
    }
    String runtime(String name) {
        if (name.endsWith("[]")) return runtime(name.substring(0, name.length() - 2)) + "[]";
        String r = classes.get(name); return r == null ? name : r;
    }
    boolean isClient(Class<?> c) {
        String name = c.getName(), obf = classes.get(CLIENT);
        return name.equals(CLIENT) || name.equals("net.minecraft.client.MinecraftClient") || name.equals("net.minecraft.class_310")
                || name.equals(obf) || (link != null && obf != null && name.equals(link.classes.get(obf)));
    }
    /** Make a session-local view. Never mutate the source mapping while discovery retries. */
    Mappings select(Class<?> client) {
        String actual = client.getName(), obf = classes.get(CLIENT);
        if (obf == null) return this; // Fully named new releases can operate with no Mojang mapping at all.
        String target = link == null ? null : link.classes.get(obf);
        boolean named = actual.equals(CLIENT) || actual.equals("net.minecraft.client.MinecraftClient");
        boolean linked = target != null && actual.equals(target);
        // Forge remaps C_xxx_ class names back to Mojmap while retaining m_xxx_/f_xxx_ members.
        if (named && link != null && link.srg) linked = hasSrgEvidence(client, obf);
        if (!named && !linked && actual.equals(obf)) return this;
        if (!named && !linked) return this;
        Mappings view = new Mappings();
        for (Map.Entry<String, String> e : classes.entrySet()) {
            boolean namedClasses = named && (!linked || !actual.equals(target));
            String runtime = namedClasses ? e.getKey() : link.classes.get(e.getValue());
            view.classes.put(e.getKey(), runtime == null ? e.getValue() : runtime);
        }
        for (Map.Entry<String, List<Member>> e : members.entrySet()) {
            List<Member> list = new ArrayList<Member>();
            String owner = classes.get(e.getKey());
            for (Member m : e.getValue()) {
                String runtime = m.name;
                if (linked) {
                    runtime = link.member(owner, m.runtime, descriptor(m), m.args == null);
                    // No guessed obf fallback in the loader namespace. Named Forge methods may be unchanged.
                    if (runtime == null) runtime = named ? m.name : "";
                }
                list.add(new Member(m.type, m.name, runtime, m.args));
            }
            view.members.put(e.getKey(), list);
            view.runtimeMembers.put(view.runtime(e.getKey()), list);
        }
        return view;
    }
    boolean hasSrgEvidence(Class<?> client, String owner) {
        List<Member> list = members.get(CLIENT);
        if (list == null) return false;
        for (Member m : list) {
            String name = link.member(owner, m.runtime, descriptor(m), m.args == null);
            if (name == null || name.equals(m.name)) continue;
            // Named-class Forge evidence must also have the complete named type signature.
            if (m.args == null) {
                for (Field f : client.getDeclaredFields())
                    if (f.getName().equals(name) && f.getType().getTypeName().equals(m.type)) return true;
            } else {
                for (Method method : client.getDeclaredMethods()) {
                    if (!method.getName().equals(name) || !method.getReturnType().getTypeName().equals(m.type)
                            || method.getParameterTypes().length != m.args.size()) continue;
                    boolean match = true;
                    for (int i = 0; i < m.args.size(); i++)
                        match &= method.getParameterTypes()[i].getTypeName().equals(m.args.get(i));
                    if (match) return true;
                }
            }
        }
        return false;
    }
    String descriptor(Member m) {
        if (m.args == null) return typeDescriptor(runtime(m.type));
        StringBuilder b = new StringBuilder("(");
        for (String arg : m.args) b.append(typeDescriptor(runtime(arg)));
        return b.append(')').append(typeDescriptor(runtime(m.type))).toString();
    }
    static String typeDescriptor(String name) {
        if (name.endsWith("[]")) return "[" + typeDescriptor(name.substring(0, name.length() - 2));
        String[] primitives = {"void", "boolean", "byte", "char", "short", "int", "long", "float", "double"};
        String codes = "VZBCSIJFD";
        for (int i = 0; i < primitives.length; i++) if (name.equals(primitives[i])) return String.valueOf(codes.charAt(i));
        return "L" + name.replace('.', '/') + ";";
    }
    boolean methodName(Method method, String... aliases) {
        List<Member> list = runtimeMembers.get(method.getDeclaringClass().getName());
        if (list == null) return has(aliases, method.getName());
        for (Member m : list) {
            if (m.args == null || !m.runtime.equals(method.getName())
                    || !runtime(m.type).equals(method.getReturnType().getTypeName())
                    || m.args.size() != method.getParameterTypes().length) continue;
            boolean ok = true;
            for (int i = 0; i < m.args.size(); i++)
                ok &= runtime(m.args.get(i)).equals(method.getParameterTypes()[i].getTypeName());
            if (ok && has(aliases, m.name)) return true;
        }
        return false;
    }
    boolean fieldName(Field field, String... aliases) {
        List<Member> list = runtimeMembers.get(field.getDeclaringClass().getName());
        if (list == null) return has(aliases, field.getName());
        for (Member m : list)
            if (m.args == null && m.runtime.equals(field.getName()) && has(aliases, m.name)
                    && runtime(m.type).equals(field.getType().getTypeName())) return true;
        return false;
    }
    static boolean has(String[] values, String s) { for (String v : values) if (v.equals(s)) return true; return false; }
}
