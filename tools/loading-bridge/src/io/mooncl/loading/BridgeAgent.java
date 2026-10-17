package io.mooncl.loading;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStreamReader;
import java.lang.instrument.Instrumentation;
import java.nio.charset.StandardCharsets;
import java.nio.file.DirectoryStream;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;

/** Launcher-owned loading progress. It never loads or connects to mod-loading-screen. */
public final class BridgeAgent {
    /** Fabric/Quilt entrypoint order, so main, client and client_init each get their own phase. */
    private static final String[] ORDER = {
        "preLaunch", "pre_launch", "main", "init", "client", "server",
        "client_init", "server_init", "client_game", "server_game", "modmenu"
    };

    public static void premain(String ignored, Instrumentation instrumentation) { start(); }

    public static void main(String[] ignored) throws InterruptedException {
        Thread worker = start();
        if (worker != null) worker.join();
    }

    private static Thread start() {
        final String file = System.getenv("MOONCL_MLS_PROGRESS_FILE");
        final String token = System.getenv("MOONCL_MLS_PROGRESS_TOKEN");
        if (file == null || token == null) return null;
        Thread worker = new Thread(new Runnable() {
            @Override public void run() { report(Paths.get(file), token); }
        }, "MOONCL loading progress");
        worker.setDaemon(false);
        worker.start();
        return worker;
    }

    static void report(Path output, String token) {
        try {
            Path game = Paths.get(System.getProperty("user.dir", ".")).toAbsolutePath().normalize();
            List<Mod> mods = readMods(game.resolve("mods"));
            String target = System.getenv("MOONCL_MLS_TARGET");
            if (target == null || target.trim().isEmpty()) {
                target = "";
                for (Mod mod : mods) { target = mod.name + " " + mod.version; break; }
            }
            List<Phase> phases = phases(mods);
            publish(output, token, "loading", "准备模组加载", "已读取 " + mods.size() + " 个模组", target, 0, 0);
            int steps = 0;
            for (Phase phase : phases) steps += phase.entries.size();
            // Keep the whole walk visible on short packs without dragging on huge ones.
            long gap = steps == 0 ? 0L : Math.max(15L, Math.min(250L, 4000L / steps));
            int phaseIndex = 0;
            for (Phase phase : phases) {
                phaseIndex++;
                String typeName = simpleType(phase.type);
                String stage = loadingLine(phase.type, typeName, 0, phase.entries.size(), null);
                publish(output, token, "loading", stage, "入口点 '" + phase.type + "' (" + typeName + ")",
                    target, 0, phase.entries.size());
                for (int i = 0; i < phase.entries.size(); i++) {
                    String entry = phase.entries.get(i);
                    int split = entry.indexOf('\u0000');
                    int second = split < 0 ? -1 : entry.indexOf('\u0000', split + 1);
                    String modName = split < 0 ? entry : entry.substring(0, split);
                    String modId = split < 0 ? "" : entry.substring(split + 1, second < 0 ? entry.length() : second);
                    stage = loadingLine(phase.type, typeName, i + 1, phase.entries.size(), modName);
                    String detail = modId.length() == 0 ? modName : modName + "  ·  " + modId;
                    publish(output, token, "loading", stage, detail, target, i + 1, phase.entries.size());
                    if (gap > 0) Thread.sleep(gap);
                }
            }
            publish(output, token, "ready", "模组初始化完成",
                "已处理 " + phaseIndex + " 个入口点阶段", target, 1, 1);
        } catch (Exception ex) {
            publish(output, token, "unavailable", "无法读取整合包进度", "将按游戏窗口完成交接", "", 0, 0);
        }
    }

    private static List<Phase> phases(List<Mod> mods) {
        Map<String, List<String>> byType = new LinkedHashMap<String, List<String>>();
        for (Mod mod : mods) {
            for (Map.Entry<String, List<String>> entry : mod.entrypoints.entrySet()) {
                List<String> sink = byType.get(entry.getKey());
                if (sink == null) { sink = new ArrayList<String>(); byType.put(entry.getKey(), sink); }
                for (String value : entry.getValue()) sink.add(mod.name + "\u0000" + mod.id + "\u0000" + value);
            }
        }
        List<Phase> phases = new ArrayList<Phase>();
        Set<String> used = new LinkedHashSet<String>();
        for (String type : ORDER) {
            List<String> entries = byType.get(type);
            if (entries == null || entries.isEmpty()) continue;
            phases.add(new Phase(type, title(type), entries));
            used.add(type);
        }
        for (Map.Entry<String, List<String>> entry : byType.entrySet()) {
            if (used.contains(entry.getKey()) || entry.getValue().isEmpty()) continue;
            phases.add(new Phase(entry.getKey(), title(entry.getKey()), entry.getValue()));
        }
        return phases;
    }

    private static String title(String type) {
        return "Loading '" + type + "'";
    }

    /** Same shape as mod-loading-screen: Loading 'client' (ClientModInitializer) — 81/83 — Wifi Card Games. */
    private static String loadingLine(String type, String typeName, int current, int total, String modName) {
        StringBuilder message = new StringBuilder("Loading '").append(type)
            .append("' (").append(typeName).append(") — ")
            .append(current).append('/').append(total);
        if (modName != null && modName.length() > 0) message.append(" — ").append(modName);
        return message.toString();
    }

    /** Fabric entrypoint key → initializer interface simple name, matching Class.getSimpleName(). */
    private static String simpleType(String type) {
        if ("main".equals(type) || "init".equals(type)) return "ModInitializer";
        if ("client".equals(type)) return "ClientModInitializer";
        if ("server".equals(type)) return "DedicatedServerModInitializer";
        if ("preLaunch".equals(type) || "pre_launch".equals(type)) return "PreLaunchEntrypoint";
        if ("client_init".equals(type)) return "ClientModInitializer";
        if ("server_init".equals(type)) return "DedicatedServerModInitializer";
        if ("modmenu".equals(type)) return "ModMenuApi";
        if (type == null || type.length() == 0) return "Entrypoint";
        StringBuilder name = new StringBuilder();
        boolean upper = true;
        for (int i = 0; i < type.length(); i++) {
            char c = type.charAt(i);
            if (c == '_' || c == '-' || c == ' ') { upper = true; continue; }
            name.append(upper ? Character.toUpperCase(c) : c);
            upper = false;
        }
        if (name.length() == 0) return "Entrypoint";
        return name.append("Entrypoint").toString();
    }

    private static List<Mod> readMods(Path directory) throws IOException {
        List<Mod> mods = new ArrayList<Mod>();
        Set<String> seen = new LinkedHashSet<String>();
        if (!Files.isDirectory(directory)) return mods;
        DirectoryStream<Path> stream = Files.newDirectoryStream(directory, "*.jar");
        try {
            for (Path jar : stream) {
                Mod mod = readMod(jar);
                if (mod != null && seen.add(mod.id)) mods.add(mod);
            }
        } finally { stream.close(); }
        return mods;
    }

    private static Mod readMod(Path jar) {
        ZipFile zip = null;
        try {
            zip = new ZipFile(jar.toFile());
            ZipEntry entry = zip.getEntry("fabric.mod.json");
            if (entry == null) entry = zip.getEntry("quilt.mod.json");
            if (entry == null) return null;
            String json = read(zip, entry);
            String id = value(json, "\"id\"");
            if (id.length() == 0) return null;
            String name = value(json, "\"name\"");
            String version = value(json, "\"version\"");
            return new Mod(id, name.length() == 0 ? id : name, version, entrypoints(json));
        } catch (Exception ex) { return null; }
        finally { if (zip != null) try { zip.close(); } catch (IOException ignored) { } }
    }

    private static String read(ZipFile zip, ZipEntry entry) throws IOException {
        BufferedReader reader = new BufferedReader(new InputStreamReader(zip.getInputStream(entry), StandardCharsets.UTF_8));
        try {
            StringBuilder text = new StringBuilder();
            String line;
            while ((line = reader.readLine()) != null) text.append(line);
            return text.toString();
        } finally { reader.close(); }
    }

    /** Reads "entrypoints": { "type": [ ... ] } without a JSON library. */
    private static Map<String, List<String>> entrypoints(String json) {
        Map<String, List<String>> result = new LinkedHashMap<String, List<String>>();
        int at = json.indexOf("\"entrypoints\"");
        if (at < 0) return result;
        int open = json.indexOf('{', at);
        if (open < 0) return result;
        int p = open + 1;
        while (p < json.length()) {
            p = skipSpace(json, p);
            if (p >= json.length() || json.charAt(p) == '}') break;
            if (json.charAt(p) == ',') { p++; continue; }
            if (json.charAt(p) != '"') break;
            int nameEnd = json.indexOf('"', p + 1);
            if (nameEnd < 0) break;
            String type = json.substring(p + 1, nameEnd);
            int colon = json.indexOf(':', nameEnd);
            if (colon < 0) break;
            int arrayStart = skipSpace(json, colon + 1);
            if (arrayStart >= json.length() || json.charAt(arrayStart) != '[') { p = colon + 1; continue; }
            int arrayEnd = match(json, arrayStart, '[', ']');
            if (arrayEnd < 0) break;
            List<String> entries = elements(json, arrayStart + 1, arrayEnd);
            if (!entries.isEmpty()) result.put(type, entries);
            p = arrayEnd + 1;
        }
        return result;
    }

    /** One label per top-level array element; nested objects and arrays count once. */
    private static List<String> elements(String json, int from, int to) {
        List<String> entries = new ArrayList<String>();
        int p = from;
        while (p < to) {
            p = skipSpace(json, p);
            if (p >= to) break;
            if (json.charAt(p) == ',') { p++; continue; }
            char first = json.charAt(p);
            if (first == '{') {
                int end = match(json, p, '{', '}');
                if (end < 0 || end > to) break;
                entries.add(label(json.substring(p, end + 1)));
                p = end + 1;
            } else if (first == '[') {
                int end = match(json, p, '[', ']');
                if (end < 0 || end > to) break;
                for (String inner : elements(json, p + 1, end)) entries.add(inner);
                p = end + 1;
            } else if (first == '"') {
                int end = json.indexOf('"', p + 1);
                if (end < 0 || end > to) break;
                entries.add(json.substring(p + 1, end));
                p = end + 1;
            } else {
                int end = p;
                while (end < to && json.charAt(end) != ',' && json.charAt(end) != ']') end++;
                entries.add(json.substring(p, end).trim());
                p = end;
            }
        }
        return entries;
    }

    private static String label(String object) {
        String value = value(object, "\"value\"");
        if (value.length() > 0) return value;
        String adapter = value(object, "\"adapter\"");
        if (adapter.length() > 0) return adapter;
        return "入口点";
    }

    private static int match(String json, int at, char open, char close) {
        int depth = 0;
        boolean inString = false;
        for (int i = at; i < json.length(); i++) {
            char c = json.charAt(i);
            if (inString) {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == open) depth++;
            else if (c == close) { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

    private static int skipSpace(String json, int p) {
        while (p < json.length() && Character.isWhitespace(json.charAt(p))) p++;
        return p;
    }

    private static String value(String json, String key) {
        int at = json.indexOf(key); if (at < 0) return "";
        int colon = json.indexOf(':', at + key.length()); if (colon < 0) return "";
        int cursor = skipSpace(json, colon + 1);
        if (cursor < json.length() && json.charAt(cursor) == '[') {
            int end = match(json, cursor, '[', ']');
            if (end < 0) return "";
            for (String inner : elements(json, cursor + 1, end)) if (inner.length() > 0) return inner;
            return "";
        }
        if (cursor >= json.length() || json.charAt(cursor) != '"') return "";
        int end = json.indexOf('"', cursor + 1); if (end < 0) return "";
        return json.substring(cursor + 1, Math.min(end, cursor + 2049));
    }

    private static void publish(Path file, String token, String status, String stage, String detail, String target, long current, long total) {
        String json = "{\"protocol\":1,\"token\":" + quote(token) + ",\"status\":" + quote(status)
            + ",\"stage\":" + quote(stage) + ",\"detail\":" + quote(detail) + ",\"target\":" + quote(target)
            + ",\"current\":" + current + ",\"total\":" + total + "}";
        Path pending = file.resolveSibling("progress.tmp");
        try {
            Files.write(pending, json.getBytes(StandardCharsets.UTF_8));
            try { Files.move(pending, file, java.nio.file.StandardCopyOption.ATOMIC_MOVE, java.nio.file.StandardCopyOption.REPLACE_EXISTING); }
            catch (java.nio.file.AtomicMoveNotSupportedException ex) { Files.move(pending, file, java.nio.file.StandardCopyOption.REPLACE_EXISTING); }
        } catch (IOException ignored) { }
    }

    private static String quote(String value) {
        StringBuilder out = new StringBuilder("\"");
        if (value != null) for (int i = 0; i < Math.min(value.length(), 2048); i++) {
            char c = value.charAt(i);
            if (c == '"' || c == '\\') out.append('\\').append(c);
            else if (c < 32) out.append(String.format(java.util.Locale.ROOT, "\\u%04x", (int)c));
            else out.append(c);
        }
        return out.append('"').toString();
    }

    private static final class Mod {
        final String id, name, version;
        final Map<String, List<String>> entrypoints;
        Mod(String id, String name, String version, Map<String, List<String>> entrypoints) {
            this.id = id; this.name = name; this.version = version; this.entrypoints = entrypoints;
        }
    }

    private static final class Phase {
        final String type, title;
        final List<String> entries;
        Phase(String type, String title, List<String> entries) { this.type = type; this.title = title; this.entries = entries; }
    }
}
