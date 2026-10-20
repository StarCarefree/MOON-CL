import java.io.InputStream;
import java.lang.reflect.InvocationTargetException;
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
import java.util.jar.JarFile;
import java.util.jar.JarOutputStream;
import java.util.jar.Manifest;

public final class LookupTest {
    private static int checks;
    private static void check(boolean value, String message) {
        checks++;
        if (!value) throw new AssertionError(message);
    }
    private static void entry(JarOutputStream out, String name, String text) throws Exception {
        out.putNextEntry(new JarEntry(name));
        out.write(text.getBytes(StandardCharsets.UTF_8));
        out.closeEntry();
    }
    public static void main(String[] args) throws Exception {
        Path root = Path.of(args[0]);
        Path file = root.resolve("fixture.jar");
        Manifest manifest = new Manifest();
        manifest.getMainAttributes().put(Attributes.Name.MANIFEST_VERSION, "1.0");
        manifest.getMainAttributes().putValue("Multi-Release", "true");
        try (JarOutputStream out = new JarOutputStream(Files.newOutputStream(file), manifest)) {
            entry(out, "dir/", "");
            entry(out, "Aa", "collision");
            entry(out, "unicode/模组.txt", "unicode");
            entry(out, "selected.txt", "base");
            entry(out, "META-INF/versions/17/selected.txt", "version17");
            entry(out, "META-INF/versions/17/version-only.txt", "version17");
            entry(out, "selected.class", "base");
            entry(out, "META-INF/versions/17/selected.class", "version17");
            entry(out, "META-INF/versions/17/version-only.class", "version17");
            entry(out, "directory.class/", "");
            entry(out, "Aa.class", "collision");
            for (int i = 0; i < 5000; i++) entry(out, "entries/" + i + ".txt", "entry" + i);
        }
        URL[] urls = {file.toUri().toURL()};
        try (URLClassLoader loader = new URLClassLoader(urls, null)) {
            check(loader.getResource("missing") == null, "Absent resource changed");
            check(loader.getResource("BB") == null, "Hash collision produced a resource");
            check(loader.getResource("dir") != null, "Directory alias lost");
            check(loader.getResource("unicode/模组.txt") != null, "Unicode entry lost");
            try (InputStream input = loader.getResourceAsStream("selected.txt")) {
                check(new String(input.readAllBytes(), StandardCharsets.UTF_8).equals("version17"), "MR version selection changed");
            }
            check(loader.getResource("version-only.txt") != null, "MR-only resource lost");
            check(loader.getResource("warmup/Missing.class") == null, "Class fixture not empty");
            try (InputStream input = loader.getResourceAsStream("selected.class")) {
                check(new String(input.readAllBytes(), StandardCharsets.UTF_8).equals("version17"), "Aggregate changed MR class selection");
            }
            check(loader.getResource("version-only.class") != null, "Aggregate lost MR-only class");
            check(loader.getResource("directory.class") != null, "Aggregate lost class-suffix directory alias");
            check(loader.getResource("Aa.class") != null && loader.getResource("BB.class") == null, "Aggregate changed hash collision handling");
            for (int i = 0; i < 5000; i++) {
                check(loader.getResource("entries/" + i + ".txt") != null, "Indexed entry lost: " + i);
                check(loader.getResource("not-there/" + i) == null, "False resource: " + i);
            }
            var workers = Executors.newFixedThreadPool(4);
            try {
                List<Callable<Boolean>> calls = new ArrayList<>();
                for (int i = 0; i < 40; i++) calls.add(() -> {
                    for (int j = 0; j < 200; j++) {
                        if (loader.getResource("missing" + j) != null || loader.getResource("Aa") == null) return false;
                    }
                    return true;
                });
                for (var future : workers.invokeAll(calls)) check(future.get(), "Concurrent lookup changed");
            } finally { workers.shutdownNow(); }
        }
        // Closing a cached JarFile must still cause the original IllegalStateException.
        if (!Boolean.getBoolean("pclmoon.startup.disabled")) {
            Class<?> cache = Class.forName("pclmoon.startup.NegativeCache", true, null);
            var lookup = cache.getMethod("getJarEntry", JarFile.class, String.class);
            JarFile jar = new JarFile(file.toFile());
            check(lookup.invoke(null, jar, "missing") == null, "Direct miss changed");
            jar.close();
            try { lookup.invoke(null, jar, "missing"); throw new AssertionError("Closed JAR was cached"); }
            catch (InvocationTargetException expected) { check(expected.getCause() instanceof IllegalStateException, "Closed exception changed"); }
            try (JarFile subclass = new JarFile(file.toFile()) {
                @Override public int hashCode() { throw new AssertionError("JarFile subclass was used as a cache key"); }
            }) {
                check(lookup.invoke(null, subclass, "Aa") != null, "Subclass lookup was not delegated");
            }
            checks++; // close() of the subclass must also bypass the cache.
            cache.getMethod("stop").invoke(null);
            try (JarFile reopened = new JarFile(file.toFile())) {
                check(lookup.invoke(null, reopened, "Aa") != null, "Disabled fallback lost resource");
            }
        }
        Files.delete(file);
        System.out.println("PASS: " + checks + " resource, MR, collision, concurrent, close and fallback checks");
    }
}
