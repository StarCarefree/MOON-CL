import java.io.InputStream;
import java.lang.reflect.Method;
import java.net.URL;
import java.net.URLClassLoader;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.Callable;
import java.util.concurrent.Executors;
import java.util.jar.Attributes;
import java.util.jar.JarEntry;
import java.util.jar.JarOutputStream;
import java.util.jar.Manifest;

/** Exercises the real JDK URLClassPath used by Fabric's Knot/Mixin parent lookups. */
public final class ClassPathCacheTest {
    public static final class Payload { public static String value() { return "unchanged"; } }
    private static int checks;
    private static final String CLASS = ClassPathCacheTest.class.getName() + "$Payload";
    private static final String ENTRY = CLASS.replace('.', '/') + ".class";
    private static void check(boolean value, String message) {
        checks++;
        if (!value) throw new AssertionError(message);
    }
    private static final class MutableLoader extends URLClassLoader {
        MutableLoader(URL... urls) { super(urls, null); }
        void append(URL url) { super.addURL(url); }
    }
    private static void jar(Path path, String entry, byte[] bytes, String manifestPath) throws Exception {
        Manifest manifest = new Manifest();
        manifest.getMainAttributes().put(Attributes.Name.MANIFEST_VERSION, "1.0");
        if (manifestPath != null) manifest.getMainAttributes().put(Attributes.Name.CLASS_PATH, manifestPath);
        try (JarOutputStream out = new JarOutputStream(Files.newOutputStream(path), manifest)) {
            if (entry != null) {
                out.putNextEntry(new JarEntry(entry)); out.write(bytes); out.closeEntry();
            }
        }
    }
    private static void absent(MutableLoader loader, String name) throws Exception {
        check(loader.getResource(name.replace('.', '/') + ".class") == null, "Missing class resource appeared");
        try { loader.loadClass(name); throw new AssertionError("Missing class loaded"); }
        catch (ClassNotFoundException expected) { check(expected.getMessage().equals(name), "Exception message changed"); }
    }
    public static void main(String[] args) throws Exception {
        Path root = Path.of(args[0]);
        Files.createDirectories(root);
        Path empty = root.resolve("empty.jar"), payload = root.resolve("payload.jar");
        byte[] bytes;
        try (InputStream input = ClassPathCacheTest.class.getResourceAsStream("/" + ENTRY)) { bytes = input.readAllBytes(); }
        jar(empty, null, null, null); jar(payload, ENTRY, bytes, null);
        try (MutableLoader loader = new MutableLoader(empty.toUri().toURL())) {
            for (int i = 0; i < 100; i++) absent(loader, CLASS);
            if (Runtime.version().feature() == 21 && !Boolean.getBoolean("mooncl.startup.disabled")
                    && !Boolean.getBoolean("mooncl.startup.classpathCache.disabled")) {
                var metric = Class.forName("mooncl.startup.NegativeCache", true, null).getMethod("classMissHits");
                long before = (Long) metric.invoke(null);
                for (int i = 0; i < 100; i++)
                    check(loader.getResource("first/query/Unique" + i + ".class") == null, "First-time missing class appeared");
                check((Long) metric.invoke(null) - before >= 90, "Aggregate must skip FIRST queries, not just repeated names");
            }
            // addURL invalidates all in-flight miss tickets before returning to the caller.
            loader.append(payload.toUri().toURL());
            check(loader.getResource(ENTRY) != null, "Appended JAR hidden by a cached miss");
            Class<?> loaded = loader.loadClass(CLASS);
            check(loaded.getMethod("value").invoke(null).equals("unchanged"), "Class bytes/initialization changed");
            check(loaded.getProtectionDomain().getCodeSource().getLocation().equals(payload.toUri().toURL()), "CodeSource changed");
            var workers = Executors.newFixedThreadPool(4);
            try {
                List<Callable<Boolean>> tasks = new ArrayList<>();
                for (int i = 0; i < 20; i++) tasks.add(() -> {
                    for (int j = 0; j < 300; j++) {
                        if (loader.getResource("missing/Concurrent.class") != null || loader.loadClass(CLASS) != loaded) return false;
                    }
                    return true;
                });
                for (var future : workers.invokeAll(tasks)) check(future.get(), "Concurrent class lookup changed");
            } finally { workers.shutdownNow(); }
            try { loader.getResource(null); throw new AssertionError("Null name accepted"); }
            catch (NullPointerException expected) { checks++; }
        }
        // Directories may change at any time; not just when addURL is called.
        Path directory = root.resolve("dynamic"); Files.createDirectories(directory);
        try (MutableLoader loader = new MutableLoader(directory.toUri().toURL(), empty.toUri().toURL())) {
            for (int i = 0; i < 10; i++) absent(loader, CLASS);
            Files.write(directory.resolve(ENTRY), bytes);
            check(loader.loadClass(CLASS).getMethod("value").invoke(null).equals("unchanged"), "Dynamic directory masked");
            Files.delete(directory.resolve(ENTRY));
        }
        // A manifest may add a dynamic directory not present in getURLs(). Never cache those misses.
        Path manifestJar = root.resolve("manifest.jar");
        jar(manifestJar, null, null, "dynamic/");
        try (MutableLoader loader = new MutableLoader(manifestJar.toUri().toURL())) {
            for (int i = 0; i < 10; i++) absent(loader, CLASS);
            Files.write(directory.resolve(ENTRY), bytes);
            check(loader.loadClass(CLASS).getMethod("value").invoke(null).equals("unchanged"), "Manifest directory masked");
            Files.delete(directory.resolve(ENTRY));
        }
        if (Runtime.version().feature() == 21 && !Boolean.getBoolean("mooncl.startup.disabled")
                && !Boolean.getBoolean("mooncl.startup.classpathCache.disabled")) {
            Class<?> cache = Class.forName("mooncl.startup.NegativeCache", true, null);
            Method metric = cache.getMethod("classMissHits");
            check(((Long) metric.invoke(null)) > 0, "No classpath scan avoided: optimization inactive");
            cache.getMethod("stop").invoke(null);
            try (MutableLoader loader = new MutableLoader(empty.toUri().toURL(), payload.toUri().toURL())) {
                check(loader.loadClass(CLASS).getMethod("value").invoke(null).equals("unchanged"), "Timeout fallback broken");
            }
        }
        System.out.println("PASS: " + checks + " class lookup, append, directory, manifest, concurrency and fallback checks");
    }
}
