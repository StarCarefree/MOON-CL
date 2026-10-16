package io.mooncl.loading;

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;

/** Headless replacement for MLS's Swing child: reads its ORIGINAL DataOutputStream protocol. */
public final class ProgressRelay {
    private static final Map<String, Bar> bars = new LinkedHashMap<String, Bar>();
    private static String stage = "加载模组启动器", detail = "mod-loading-screen 已连接", status = "loading", target = "";
    private static long current, total;
    private static String selected = "";
    private static Path file;
    private static String token;
    private static boolean stopped;

    public static void main(String[] ignored) {
        String path = System.getenv("MOONCL_MLS_PROGRESS_FILE");
        token = System.getenv("MOONCL_MLS_PROGRESS_TOKEN");
        if (path != null && token != null) file = Paths.get(path);
        Thread heartbeat = new Thread(new Runnable() {
            @Override public void run() {
                try { while (!Thread.currentThread().isInterrupted()) { publish(); Thread.sleep(1000); } }
                catch (InterruptedException ignored) { }
            }
        }, "MOONCL progress heartbeat");
        heartbeat.setDaemon(true); heartbeat.start();
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
        } catch (Exception ex) {
            synchronized (ProgressRelay.class) {
                status = "unavailable"; stage = "模组进度连接中断"; detail = "等待游戏窗口，进度通道已关闭"; publish();
            }
        } finally { heartbeat.interrupt(); }
        // NEVER System.exit the parent or close its pipe because a MOONCL window was dismissed.
    }

    private static void event(int id, String[] a) {
        switch (id) {
            case 0:
                bars.put("entrypoint:" + a[0], new Bar("初始化 " + a[0], number(a[2])));
                selected = "entrypoint:" + a[0]; detail = a[1]; select(); break;
            case 1:
                selected = "entrypoint:" + a[0]; Bar entry = bars.get(selected);
                if (entry != null) entry.current++;
                detail = a[3] + "  ·  " + a[2]; select(); break;
            case 2:
                bars.remove("entrypoint:" + a[0]); selected = lastKey(); select(); break;
            case 4:
                selected = "custom:" + a[0]; bars.put(selected, new Bar(a[1], number(a[2]))); detail = a[1]; select(); break;
            case 5:
                String key = "custom:" + a[0]; Bar bar = bars.get(key);
                if ("close".equals(a[1])) { bars.remove(key); selected = lastKey(); select(); break; }
                if (bar == null || a.length < 3) break;
                if ("progress".equals(a[1])) bar.current = number(a[2]);
                else if ("maximum".equals(a[1])) bar.maximum = number(a[2]);
                else if ("minimum".equals(a[1])) bar.minimum = number(a[2]);
                else if ("title".equals(a[1])) bar.title = a[2];
                else if ("indeterminate".equals(a[1])) bar.indeterminate = Boolean.parseBoolean(a[2]);
                selected = key; select(); break;
            case 6: target = a[0]; break;
            case 7: if (Boolean.parseBoolean(a[0])) { stage = "更新整合包"; detail = "mod-loading-screen 更新器"; } break;
            case 255: status = "ready"; stage = "模组初始化完成"; detail = "mod-loading-screen 已发出完成事件"; current = total = 1; break;
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
        if (!Files.isDirectory(parent)) { stopped = true; return; } // Launcher gone: keep draining MLS without affecting game.
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
