package mooncl.startup;

import java.lang.module.ModuleReader;
import java.lang.module.ModuleReference;
import java.lang.reflect.Method;
import java.net.URI;
import java.nio.file.FileSystems;
import java.nio.file.Files;
import java.nio.file.LinkOption;
import java.nio.file.Path;
import java.nio.file.attribute.BasicFileAttributes;
import java.nio.file.attribute.FileTime;
import java.util.HashMap;
import java.util.IdentityHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Objects;
import java.util.Timer;
import java.util.TimerTask;
import java.util.concurrent.TimeUnit;

/** Short-lived positive cache of reader output, never of transformed/defined classes. */
public final class ForgeReadCache {
    private static final Object LOCK = new Object();
    private static final int MAX_READERS = 128, MAX_ENTRIES = 16384, MAX_CLASS = 1024 * 1024;
    private static final long MAX_BYTES = 32L * 1024 * 1024;
    private static final IdentityHashMap<Object, ReaderState> READERS = new IdentityHashMap<>();
    private static final boolean METRICS = metricsEnabled();
    private static boolean running;
    private static long generation, started, lifetime, bytes, hits, eligibleSources, bypassedSources, directReads;
    private static int entries, sources;
    private static Timer timer;

    private ForgeReadCache() {}

    private static boolean metricsEnabled() {
        try { return Boolean.getBoolean("mooncl.startup.metrics"); }
        catch (Throwable ignored) { return false; }
    }

    /** An opaque capability for exactly one reader/reference/name and one start generation. */
    public static final class Ticket {
        private final Source source;
        private final String name;
        private Ticket(Source source, String name) { this.source = source; this.name = name; }
    }

    private static final class ReaderState {
        boolean closed;
        final IdentityHashMap<ModuleReference, Source> refs = new IdentityHashMap<>();
    }

    private static final class Source {
        final long generation;
        boolean valid = true, ready;
        Stamp[] stamps;
        final Map<String, byte[]> values = new HashMap<>();
        Source(long generation) { this.generation = generation; }
    }

    private static final class Stamp {
        final Path path;
        final long size;
        final FileTime modified;
        final Object key;
        Stamp(Path path, BasicFileAttributes attrs) {
            this.path = path;
            size = attrs.size(); modified = attrs.lastModifiedTime(); key = attrs.fileKey();
        }
        boolean matches() throws Exception {
            BasicFileAttributes attrs = attributes(path);
            return attrs.size() == size && attrs.lastModifiedTime().equals(modified)
                    && Objects.equals(key, attrs.fileKey());
        }
    }

    public static void start(long ms) {
        synchronized (LOCK) {
            clearLocked();
            hits = 0;
            eligibleSources = 0; bypassedSources = 0;
            directReads = 0;
            if (ms <= 0) return;
            try {
                lifetime = TimeUnit.MILLISECONDS.toNanos(ms); // saturating conversion
                started = System.nanoTime();
                running = true;
                long epoch = generation;
                timer = new Timer("mooncl-forge-read-cache", true);
                timer.schedule(new TimerTask() {
                    @Override public void run() {
                        String report = null;
                        synchronized (LOCK) {
                            if (generation == epoch) {
                                if (METRICS) report = summary();
                                clearLocked();
                            }
                        }
                        if (report != null) System.err.println("[mooncl-startup] Forge cache window ended; " + report);
                    }
                }, Math.min(ms, Long.MAX_VALUE - System.currentTimeMillis() - 1));
            } catch (Throwable ignored) { clearLocked(); }
        }
    }

    public static void stop() {
        synchronized (LOCK) { clearLocked(); }
    }

    private static void clearLocked() {
        running = false;
        generation++;
        if (timer != null) { timer.cancel(); timer = null; }
        for (ReaderState reader : READERS.values()) {
            for (Source source : reader.refs.values()) {
                source.valid = false;
                source.values.clear();
            }
            reader.refs.clear();
        }
        READERS.clear();
        bytes = 0; entries = 0; sources = 0;
    }

    private static boolean activeLocked() {
        if (running && System.nanoTime() - started >= lifetime) clearLocked();
        return running;
    }

    private static boolean liveLocked(Source source) {
        return activeLocked() && source.valid && source.generation == generation;
    }
    /** Equivalent to securejarhandler 2.1.10's Optional/Stream read pipeline, without its
     * stream/lambda wrappers. The caller still runs the original transformation and signers. */
    public static byte[] read(java.lang.module.ModuleReader reader, ModuleReference ref, String name)
            throws java.io.IOException {
        Ticket ticket = begin(reader, ref, name);
        byte[] value = cached(ticket, name);
        if (value != null) return value;
        String resource = name.replace('.', '/') + ".class";
        java.io.InputStream input = reader.open(resource).orElse(null);
        if (METRICS) synchronized (LOCK) { directReads++; }
        if (input == null) return new byte[0];
        try (input) {
            value = Objects.requireNonNull(input.readAllBytes());
        }
        // A close failure must propagate before any successful result is cached.
        record(value, ticket, name);
        return value;
    }


    public static Ticket begin(ModuleReader reader, ModuleReference ref, String name) {
        if (reader == null || ref == null || name == null || name.isEmpty() || name.length() > 512) return null;
        Source source = null;
        try {
            synchronized (LOCK) {
                if (!activeLocked()) return null;
                ReaderState state = READERS.get(reader);
                if (state == null) {
                    if (READERS.size() >= MAX_READERS) return null;
                    state = new ReaderState();
                    READERS.put(reader, state);
                }
                if (state.closed) return null;
                source = state.refs.get(ref);
                if (source != null) {
                    return source.valid && source.ready ? new Ticket(source, name) : null;
                }
                // Reserve BEFORE any external work: close/stop cannot resurrect an in-flight source.
                if (sources >= MAX_ENTRIES) return null;
                source = new Source(generation);
                state.refs.put(ref, source);
                sources++;
            }
            Stamp[] snapshot = snapshot(ref);
            synchronized (LOCK) {
                if (!liveLocked(source)) return null;
                if (snapshot == null) {
                    if (METRICS) bypassedSources++;
                    invalidateLocked(source); return null;
                }
                if (METRICS) eligibleSources++;
                source.stamps = snapshot;
                source.ready = true;
                return new Ticket(source, name);
            }
        } catch (Throwable ignored) {
            if (source != null) invalidate(source);
            return null;
        }
    }

    public static byte[] cached(Ticket ticket, String name) {
        if (!matches(ticket, name)) return null;
        Source source = ticket.source;
        try {
            byte[] value;
            Stamp[] snapshot;
            synchronized (LOCK) {
                if (!liveLocked(source) || !source.ready) return null;
                value = source.values.get(name);
                if (value == null) return null;
                snapshot = source.stamps;
            }
            if (!unchanged(snapshot)) { invalidate(source); return null; }
            byte[] copy = value.clone();
            synchronized (LOCK) {
                if (!liveLocked(source)) return null;
                if (METRICS) hits++;
                return copy;
            }
        } catch (Throwable ignored) { invalidate(source); return null; }
    }

    public static void record(byte[] value, Ticket ticket, String name) {
        if (value == null || value.length == 0 || value.length > MAX_CLASS || !matches(ticket, name)) return;
        Source source = ticket.source;
        try {
            Stamp[] snapshot;
            synchronized (LOCK) {
                if (!liveLocked(source) || !source.ready) return;
                snapshot = source.stamps;
            }
            if (!unchanged(snapshot)) { invalidate(source); return; }
            byte[] copy = value.clone();
            synchronized (LOCK) {
                if (!liveLocked(source) || source.values.containsKey(name)
                        || entries >= MAX_ENTRIES || bytes + copy.length > MAX_BYTES) return;
                source.values.put(name, copy);
                bytes += copy.length;
                entries++;
            }
        } catch (Throwable ignored) { invalidate(source); }
    }

    private static boolean matches(Ticket ticket, String name) {
        return ticket != null && ticket.name.equals(name);
    }

    /** Called at close entry, including when the real close subsequently throws. */
    public static void closeReader(Object reader) {
        if (reader == null) return;
        try {
            synchronized (LOCK) {
                if (!activeLocked()) return;
                ReaderState state = READERS.get(reader);
                if (state == null) {
                    if (READERS.size() >= MAX_READERS) return;
                    state = new ReaderState();
                    READERS.put(reader, state);
                }
                state.closed = true;
                for (Source source : state.refs.values()) invalidateLocked(source);
                state.refs.clear();
                // Keep a bounded identity tombstone until stop: later begins cannot revive this reader.
            }
        } catch (Throwable ignored) { /* The original close must still run. */ }
    }

    private static void invalidate(Source source) {
        synchronized (LOCK) { invalidateLocked(source); }
    }

    private static void invalidateLocked(Source source) {
        if (!source.valid) return;
        source.valid = false;
        for (byte[] value : source.values.values()) { bytes -= value.length; entries--; }
        source.values.clear();
    }

    private static boolean unchanged(Stamp[] snapshot) throws Exception {
        for (Stamp stamp : snapshot) if (!stamp.matches()) return false;
        return true;
    }

    private static BasicFileAttributes attributes(Path path) throws Exception {
        BasicFileAttributes attrs = Files.readAttributes(path, BasicFileAttributes.class, LinkOption.NOFOLLOW_LINKS);
        if (!attrs.isRegularFile()) throw new IllegalArgumentException("Not a regular JAR");
        return attrs;
    }

    private static Stamp jar(Path path) throws Exception {
        if (path.getFileSystem() != FileSystems.getDefault()
                || path.getFileName() == null
                || !path.getFileName().toString().toLowerCase(Locale.ROOT).endsWith(".jar")) return null;
        Path absolute = path.toAbsolutePath().normalize();
        // NOFOLLOW rejects a final symlink, and stat each time observes parent-directory changes.
        return new Stamp(absolute, attributes(absolute));
    }

    private static Stamp[] snapshot(ModuleReference ref) throws Exception {
        URI location = ref.location().orElse(null);
        if (location == null) return null;
        if ("file".equalsIgnoreCase(location.getScheme())) {
            Stamp stamp = jar(Path.of(location));
            return stamp == null ? null : new Stamp[] { stamp };
        }
        if (!"union".equalsIgnoreCase(location.getScheme())) return null;
        // Provider lookup may be invisible to a bootstrap helper; that is a safe bypass, not a guess.
        Object fs = Path.of(location).getFileSystem();
        Class<?> type = fs.getClass();
        if (!type.getName().equals("cpw.mods.niofs.union.UnionFileSystem")) return null;
        if (type.getMethod("getFilesystemFilter").invoke(fs) != null) return null;
        Method bases = type.getDeclaredMethod("getBasePaths");
        if (!bases.trySetAccessible()) return null;
        Object raw = bases.invoke(fs);
        if (!(raw instanceof List<?> paths) || paths.isEmpty() || paths.size() > MAX_READERS) return null;
        Stamp[] result = new Stamp[paths.size()];
        for (int i = 0; i < result.length; i++) {
            if (!(paths.get(i) instanceof Path path)) return null;
            result[i] = jar(path);
            if (result[i] == null) return null;
        }
        return result;
    }

    public static long hitCount() { synchronized (LOCK) { return hits; } }
    public static String summary() {
        synchronized (LOCK) {
            return "Forge raw hits=" + hits + ", direct reads=" + directReads
                + ", eligible sources=" + eligibleSources + ", bypassed sources=" + bypassedSources;
        }
    }
    public static long storedBytes() { synchronized (LOCK) { activeLocked(); return bytes; } }
}
