package io.pclmoon.loading;

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;

/**
 * Headless replacement for MLS's Swing child.
 * When started as the display subprocess it still reads the original pipe.
 * When loaded in the game JVM it consumes the same progress methods directly, so the launcher
 * does not depend on MLS opening that pipe.
 */
public final class ProgressRelay {
    private static final Map<String, Bar> bars = new LinkedHashMap<String, Bar>();
    private static String stage = "加载模组启动器", detail = "正在读取模组加载进度", status = "loading", target = "";
    private static long current, total;
    private static String selected = "";
    private static Path file;
    private static String token;
    private static boolean stopped;
    private static Thread heartbeat;
    private static volatile boolean attached;
    public static boolean isAttached() { return attached; }
    public static void main(String[] ignored) {
        open();
        try {
            DataInputStream in = new DataInputStream(new BufferedInputStream(System.in));
            while (true) {
                int id = in.readUnsignedByte(), argc = in.readUnsignedByte();
                if (argc > 16) throw new IOException("Invalid MLS argument count");
                String[] args = new String[argc];
                for (int i = 0; i < argc; i++) args[i] = in.readUTF(); // Java modified UTF-8, including NUL/surrogate pairs.
                synchronized (ProgressRelay.class) { event(id, args); publish(); }
                if (id == 255) break;
            }
        } catch (EOFException end) {
            // Parent closed after packet 255, or the game exited. Not a broken feed.
        } catch (Exception ex) {
            synchronized (ProgressRelay.class) {
                status = "unavailable"; stage = "模组进度连接中断"; detail = "等待游戏窗口，进度通道已关闭"; publish();
            }
        } finally { if (heartbeat != null) heartbeat.interrupt(); }
        // NEVER System.exit the parent or close its pipe because a PCL-MOON window was dismissed.
    }

    /** Called from rewritten MLS methods inside the game process. Never throws into the game. */
    public static void attach() {
        if (attached) return;
        synchronized (ProgressRelay.class) {
            if (attached) return;
            attached = true;
            open();
            detail = "mod-loading-screen 进度已接入启动器";
            publish();
        }
    }

    public static void entrypointBegin(String name, String type, int count) {
        synchronized (ProgressRelay.class) {
            bars.put("entrypoint:" + name, new Bar("初始化 " + name, count));
            selected = "entrypoint:" + name; detail = type; select(); publish();
        }
    }

    public static void entrypointStep(String typeName, String typeType, String modId, String modName) {
        synchronized (ProgressRelay.class) {
            selected = "entrypoint:" + typeName; Bar entry = bars.get(selected);
            if (entry != null) entry.current++;
            detail = modName + "  ·  " + modId; select(); publish();
        }
    }

    public static void entrypointEnd(String name) {
        synchronized (ProgressRelay.class) {
            bars.remove("entrypoint:" + name); selected = lastKey(); select(); publish();
        }
    }

    public static void customCreate(String id, String title, int max) {
        synchronized (ProgressRelay.class) {
            selected = "custom:" + id; bars.put(selected, new Bar(title, max)); detail = title; select(); publish();
        }
    }

    public static void customOp(String id, String op, String value) {
        synchronized (ProgressRelay.class) {
            String key = "custom:" + id; Bar bar = bars.get(key);
            if ("close".equals(op)) { bars.remove(key); selected = lastKey(); select(); publish(); return; }
            if (bar == null || value == null) return;
            if ("progress".equals(op)) bar.current = number(value);
            else if ("maximum".equals(op)) bar.maximum = number(value);
            else if ("minimum".equals(op)) bar.minimum = number(value);
            else if ("title".equals(op)) bar.title = value;
            else if ("indeterminate".equals(op)) bar.indeterminate = Boolean.parseBoolean(value);
            selected = key; select(); publish();
        }
    }

    /** MLS passes a varargs array that may contain only the operation. */
    public static void customOp(String[] args) {
        if (args == null || args.length < 2 || args[0] == null || args[1] == null) return;
        customOp(args[0], args[1], args.length > 2 ? args[2] : "");
    }

    public static void title(String value) {
        synchronized (ProgressRelay.class) { target = value == null ? "" : value; publish(); }
    }

    public static void updating(boolean value) {
        synchronized (ProgressRelay.class) {
            if (value) { stage = "更新整合包"; detail = "mod-loading-screen 更新器"; publish(); }
        }
    }

    public static void complete() {
        synchronized (ProgressRelay.class) {
            attached = false;
            status = "ready"; stage = "模组初始化完成"; detail = "mod-loading-screen 已发出完成事件";
            current = total = 1; publish();
        }
    }

    private static void open() {
        if (file != null) return;
        String path = System.getenv("PCL_MOON_MLS_PROGRESS_FILE");
        token = System.getenv("PCL_MOON_MLS_PROGRESS_TOKEN");
        if (path == null || token == null) return;
        file = Paths.get(path);
        heartbeat = new Thread(new Runnable() {
            @Override public void run() {
                try { while (!Thread.currentThread().isInterrupted()) { publish(); Thread.sleep(1000); } }
                catch (InterruptedException ignored) { }
            }
        }, "PCL-MOON progress heartbeat");
        heartbeat.setDaemon(true); heartbeat.start();
    }

    private static void event(int id, String[] a) {
        switch (id) {
            case 0: entrypointBegin(a[0], a[1], (int)number(a[2])); break;
            case 1: entrypointStep(a[0], a[1], a[2], a[3]); break;
            case 2: entrypointEnd(a[0]); break;
            case 4: customCreate(a[0], a[1], (int)number(a[2])); break;
            case 5: customOp(a[0], a[1], a.length > 2 ? a[2] : ""); break;
            case 6: title(a[0]); break;
            case 7: updating(Boolean.parseBoolean(a[0])); break;
            case 255: complete(); break;
            default: break; // memory/source metadata do not advance loading progress.
        }
    }

    private static void select() {
        Bar bar = bars.get(selected);
        if (bar == null) { stage = "等待下一个加载阶段"; current = total = 0; return; }
        stage = bar.title; current = Math.max(0, bar.current - bar.minimum);
        total = bar.indeterminate ? 0 : Math.max(0, bar.maximum - bar.minimum);
    }

    private static String lastKey() { String key = ""; for (String k : bars.keySet()) key = k; return key; }
    private static long number(String value) { try { return Long.parseLong(value); } catch (NumberFormatException ignored) { return 0; } }

    private static synchronized void publish() {
        if (file == null || stopped) return;
        Path parent = file.getParent();
        if (parent != null && !Files.isDirectory(parent)) { stopped = true; return; } // Launcher gone: do not affect the game.
        String json = "{\"protocol\":1,\"token\":" + quote(token) + ",\"status\":" + quote(status) +
            ",\"stage\":" + quote(stage) + ",\"detail\":" + quote(detail) + ",\"target\":" + quote(target) +
            ",\"current\":" + current + ",\"total\":" + total + "}";
        Path pending = file.resolveSibling("progress.tmp");
        try {
            Files.write(pending, json.getBytes(StandardCharsets.UTF_8));
            try { Files.move(pending, file, StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING); }
            catch (AtomicMoveNotSupportedException ex) { Files.move(pending, file, StandardCopyOption.REPLACE_EXISTING); }
        } catch (IOException ignored) { /* UI telemetry failure must not abort Minecraft */ }
    }

    private static String quote(String value) {
        StringBuilder b = new StringBuilder("\"");
        if (value != null) for (int i = 0; i < Math.min(value.length(), 2048); i++) {
            char c = value.charAt(i);
            if (c == '"' || c == '\\') b.append('\\').append(c);
            else if (c < 32 || Character.isSurrogate(c)) b.append(String.format(Locale.ROOT, "\\u%04x", (int)c));
            else b.append(c);
        }
        return b.append('"').toString();
    }

    private static final class Bar {
        String title; long minimum, maximum, current; boolean indeterminate;
        Bar(String title, long maximum) { this.title = title; this.maximum = maximum; }
    }
}
