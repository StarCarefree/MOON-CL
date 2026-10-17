package mooncl.startup;

import java.util.Enumeration;
import java.util.Map;
import java.util.IdentityHashMap;
import java.util.Collections;
import java.util.WeakHashMap;
import java.net.URL;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;
import java.util.concurrent.atomic.LongAdder;
import java.util.jar.JarEntry;
import java.util.jar.JarFile;

/** Bootstrap-only, conservative JAR membership index. False positives use the original lookup. */
public final class NegativeCache {
    private static final int MAX_JARS = 256;
    private static final Object LOCK = new Object();
    private static volatile Map<JarFile, Index> indexes = Collections.emptyMap();
    private static final Map<Object, Boolean> closing = new WeakHashMap<>();
    private static final LongAdder hits = new LongAdder();
    private static volatile boolean enabled;
    private static final boolean METRICS = Boolean.getBoolean("mooncl.startup.metrics");
    private static final boolean CLASS_LOOKUPS = Runtime.version().feature() == 21
        && !Boolean.getBoolean("mooncl.startup.classpathCache.disabled");
    private static final Object CLASS_LOCK = new Object();
    private static final AtomicLong generation = new AtomicLong();
    private static final LongAdder classHits = new LongAdder();
    private static volatile Map<Object, ClassLookup> classpaths = Collections.emptyMap();
    private static volatile boolean classpathEnabled;
    private static volatile boolean manifestExpanded;
    private static final ThreadLocal<Boolean> registering = new ThreadLocal<>();

    private NegativeCache() { }

    public static void start(long milliseconds) {
        enabled = true;
        Thread cleanup = new Thread(() -> {
            try { Thread.sleep(milliseconds); }
            catch (InterruptedException ignored) { Thread.currentThread().interrupt(); }
            stop();
        }, "MOONCL startup cache cleanup");
        cleanup.setDaemon(true);
        cleanup.start();
    }

    public static JarEntry getJarEntry(JarFile jar, String name) {
        if (!enabled || name == null || jar.getClass() != JarFile.class) return jar.getJarEntry(name);
        Index index = indexes.get(jar);
        if (index == null && enabled && indexes.size() < MAX_JARS) {
            synchronized (LOCK) {
                index = indexes.get(jar);
                if (closing.containsKey(jar)) return jar.getJarEntry(name);
                if (index == null && enabled && indexes.size() < MAX_JARS) {
                    try { index = new Index(jar); }
                    catch (RuntimeException unavailable) {
                        // Enumeration can reject malformed entries that a single lookup never reads.
                        // Disable the optional index and let the original API decide the outcome.
                        enabled = false;
                        indexes = Collections.emptyMap();
                        return jar.getJarEntry(name);
                    }
                    Map<JarFile, Index> next = new IdentityHashMap<>(indexes);
                    next.put(jar, index);
                    indexes = next;
                }
            }
        }
        if (index != null && !index.mightContain(name)) {
            if (METRICS) hits.increment();
            return null;
        }
        return jar.getJarEntry(name);
    }

    public static void forget(Object jar) {
        if (!enabled || jar == null || jar.getClass() != JarFile.class) return;
        synchronized (LOCK) {
            if (!enabled) return; // stop() may have cleared the maps while close waited for LOCK.
            closing.put(jar, Boolean.TRUE);
            Map<JarFile, Index> next = new IdentityHashMap<>(indexes);
            next.remove(jar);
            indexes = next;
        }
    }

    public static void stop() {
        enabled = false;
        classpathEnabled = false;
        invalidateClasspaths();
        synchronized (LOCK) { indexes = Collections.emptyMap(); closing.clear(); }
        System.err.println("[MOONCL Startup] cache released" + (METRICS
            ? "; JAR misses avoided=" + hits.sum() + "; classpath scans avoided=" + classHits.sum()
                + "; " + ForgeReadCache.summary() : ""));
    }

    public static void activateClasspaths() { classpathEnabled = CLASS_LOOKUPS && !manifestExpanded; }

    /** Only called after all mutation hooks have been verified. */
    @SuppressWarnings("removal")
    public static ClassLookup beginClassLookup(Object owner, String name) {
        if (!enabled || !classpathEnabled || name == null || name.length() > 256 || !name.endsWith(".class")
                || System.getSecurityManager() != null) return null;
        ClassLookup lookup = classpaths.get(owner);
        if (lookup != null) return lookup;
        if (registering.get() != null || classpaths.size() >= 16) return null;
        registering.set(Boolean.TRUE);
        try {
            // Match the JDK's owner -> metadata lock order. Never hold CLASS_LOCK while
            // inspecting an owner/JarFile; a JDK getLoader call can hold owner and invalidate.
            synchronized (owner) {
                long version = generation.get();
                URL[] urls = (URL[]) owner.getClass().getMethod("getURLs").invoke(owner);
                var handler = owner.getClass().getDeclaredField("jarHandler");
                handler.setAccessible(true);
                boolean eligible = urls.length > 0 && handler.get(owner) == null;
                for (URL url : urls) {
                    if (!"file".equals(url.getProtocol()) || !url.getHost().isEmpty() || url.getQuery() != null
                            || url.getRef() != null || !url.getPath().endsWith(".jar")
                            || !Files.isRegularFile(Path.of(url.toURI()))) { eligible = false; break; }
                }
                // Already-open loaders may have expanded a manifest BEFORE this agent's hook
                // was installed. Check their actual JarFiles, not just getURLs()'s initial paths.
                var loaders = owner.getClass().getDeclaredField("loaders");
                loaders.setAccessible(true);
                for (Object loader : (java.util.List<?>) loaders.get(owner)) {
                    if (!eligible) break;
                    if (!loader.getClass().getName().equals("jdk.internal.loader.URLClassPath$JarLoader")) {
                        eligible = false; break;
                    }
                    var getJar = loader.getClass().getDeclaredMethod("getJarFile");
                    getJar.setAccessible(true);
                    JarFile jar = (JarFile) getJar.invoke(loader);
                    var manifest = jar.getManifest();
                    if (manifest != null && manifest.getMainAttributes().getValue("Class-Path") != null)
                        eligible = false;
                }
                synchronized (CLASS_LOCK) {
                    if (!enabled || !classpathEnabled || version != generation.get() || classpaths.size() >= 16) return null;
                    lookup = classpaths.get(owner);
                    if (lookup != null) return lookup;
                    lookup = new ClassLookup(owner, version, eligible);
                    Map<Object, ClassLookup> next = new IdentityHashMap<>(classpaths);
                    next.put(owner, lookup);
                    classpaths = next;
                    return lookup;
                }
            }
        } catch (ReflectiveOperationException | RuntimeException | java.io.IOException | java.net.URISyntaxException unavailable) {
            classpathEnabled = false;
            invalidateClasspaths();
            return null;
        } finally { registering.remove(); }
    }

    public static boolean isClassMissing(ClassLookup lookup, String name) {
        if (lookup == null || !enabled || !classpathEnabled || !lookup.eligible
                || lookup.version != generation.get()) return false;
        Index aggregate = lookup.aggregate;
        boolean missing = aggregate != null && !aggregate.mightContain(name) || lookup.misses.contains(name);
        if (missing && METRICS) classHits.increment();
        return missing;
    }

    public static void observeClassLookup(Object result, ClassLookup lookup, String name) {
        if (result != null || lookup == null || !lookup.eligible || !enabled || !classpathEnabled
                || lookup.version != generation.get()) return;
        if (lookup.building.compareAndSet(false, true)) buildClassIndex(lookup);
        if (lookup.size.incrementAndGet() > 4096) { lookup.size.decrementAndGet(); return; }
        if (!lookup.misses.add(name)) lookup.size.decrementAndGet();
    }

    private static void buildClassIndex(ClassLookup lookup) {
        try {
            synchronized (lookup.owner) {
                if (!enabled || !classpathEnabled || lookup.version != generation.get()) return;
                URL[] urls = (URL[]) lookup.owner.getClass().getMethod("getURLs").invoke(lookup.owner);
                var field = lookup.owner.getClass().getDeclaredField("loaders");
                field.setAccessible(true);
                List<?> loaders = (List<?>) field.get(lookup.owner);
                // A null original lookup normally opens the full path. Missing/corrupt/skipped
                // JARs or duplicate URLs are deliberately not summarized into an aggregate.
                if (loaders.size() != urls.length || loaders.size() > MAX_JARS) return;
                List<JarFile> jars = new ArrayList<>();
                long count = 0;
                for (Object loader : loaders) {
                    if (!loader.getClass().getName().equals("jdk.internal.loader.URLClassPath$JarLoader")) return;
                    var getJar = loader.getClass().getDeclaredMethod("getJarFile");
                    getJar.setAccessible(true);
                    JarFile jar = (JarFile) getJar.invoke(loader);
                    if (jar.getClass() != JarFile.class) return;
                    var manifest = jar.getManifest();
                    if (manifest != null && manifest.getMainAttributes().getValue("Class-Path") != null) return;
                    count += jar.size();
                    if (count > 262144) return;
                    jars.add(jar);
                }
                Index aggregate = new Index(jars, count);
                if (enabled && classpathEnabled && lookup.version == generation.get()) lookup.aggregate = aggregate;
            }
        } catch (ReflectiveOperationException | java.io.IOException | RuntimeException unavailable) {
            // A failed optional summary must never substitute for an original class lookup.
        }
    }

    /** Any addURL/close invalidates even in-flight observations, across all classpaths. */
    public static void invalidateClasspaths() {
        synchronized (CLASS_LOCK) {
            generation.incrementAndGet();
            classpaths = Collections.emptyMap();
        }
    }

    public static void manifestClassPath(URL[] urls) {
        if (urls != null && urls.length > 0) {
            manifestExpanded = true;
            classpathEnabled = false; // A manifest can introduce a live directory not visible in getURLs().
            invalidateClasspaths();
        }
    }

    public static long classMissHits() { return classHits.sum(); }

    public static final class ClassLookup {
        final Object owner;
        final AtomicBoolean building = new AtomicBoolean();
        volatile Index aggregate;
        final long version;
        final boolean eligible;
        final Set<String> misses = ConcurrentHashMap.newKeySet();
        final AtomicInteger size = new AtomicInteger();
        ClassLookup(Object owner, long version, boolean eligible) {
            this.owner = owner; this.version = version; this.eligible = eligible;
        }
    }

    private static final class Index {
        // At most 256 KiB per JAR / 64 MiB total. Released on close or at startup timeout.
        final long[] bits;
        final int mask;
        Index(JarFile jar) {
            this(Collections.singletonList(jar), jar.size());
        }
        Index(List<JarFile> jars, long count) {
            int bitCount = 1024;
            while (bitCount < Math.min(2097152L, Math.max(1024L, count * 16L))) bitCount <<= 1;
            bits = new long[bitCount >>> 6];
            mask = bitCount - 1;
            for (JarFile jar : jars) addEntries(jar);
        }
        private void addEntries(JarFile jar) {
            Enumeration<JarEntry> entries = jar.entries();
            while (entries.hasMoreElements()) {
                String name = entries.nextElement().getName();
                add(name.hashCode());
                // Multi-release aliases are indexed conservatively for ALL versions. Resolution
                // (including the JVM's chosen version and signature checks) remains in JarFile.
                String prefix = "META-INF/versions/";
                if (name.startsWith(prefix)) {
                    int slash = name.indexOf('/', prefix.length());
                    if (slash > 0 && slash + 1 < name.length()) add(name.substring(slash + 1).hashCode());
                }
            }
        }
        void add(int hash) {
            int bit = (hash ^ (hash >>> 16)) & mask;
            bits[bit >>> 6] |= 1L << bit;
        }
        boolean test(int hash) {
            int bit = (hash ^ (hash >>> 16)) & mask;
            return (bits[bit >>> 6] & (1L << bit)) != 0;
        }
        boolean mightContain(String name) {
            int hash = name.hashCode();
            return test(hash) || (!name.endsWith("/") && test(hash * 31 + '/'));
        }
    }
}
