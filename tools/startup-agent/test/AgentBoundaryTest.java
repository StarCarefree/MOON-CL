import java.lang.instrument.Instrumentation;
import java.lang.reflect.InvocationTargetException;
import java.net.URL;
import java.net.URLClassLoader;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.jar.Attributes;
import java.util.jar.JarEntry;
import java.util.jar.JarFile;
import java.util.jar.JarOutputStream;
import java.util.jar.Manifest;

/** Run this class as a FIRST test agent, before the startup agent under test. */
public final class AgentBoundaryTest {
    private static Instrumentation instrumentation;
    private static URLClassLoader prewarmed;
    private static Path root, empty, large;
    private static int checks;
    private static void check(boolean value, String message) {
        checks++;
        if (!value) throw new AssertionError(message);
    }
    private static final class Loader extends URLClassLoader {
        Loader(URL... urls) { super(urls, null); }
        void append(URL url) { super.addURL(url); }
    }
    private static void jar(Path file, String classpath, int entries) throws Exception {
        Manifest manifest = new Manifest();
        manifest.getMainAttributes().put(Attributes.Name.MANIFEST_VERSION, "1.0");
        if (classpath != null) manifest.getMainAttributes().put(Attributes.Name.CLASS_PATH, classpath);
        try (JarOutputStream out = new JarOutputStream(Files.newOutputStream(file), manifest)) {
            for (int i = 0; i < entries; i++) {
                out.putNextEntry(new JarEntry("entry/" + i + ".class")); out.write(0); out.closeEntry();
            }
        }
    }
    public static void premain(String directory, Instrumentation value) throws Exception {
        instrumentation = value;
        root = Path.of(directory); Files.createDirectories(root.resolve("preexisting"));
        empty = root.resolve("empty.jar"); large = root.resolve("large.jar");
        jar(empty, null, 0); jar(large, null, 1200);
        Path manifest = root.resolve("prewarmed.jar"); jar(manifest, "preexisting/", 0);
        prewarmed = new URLClassLoader(new URL[] {manifest.toUri().toURL()}, null);
        // JDK expands this manifest before the tested agent is installed.
        if (prewarmed.getResource("BeforeInstall.class") != null) throw new AssertionError("Fixture is dirty");
    }
    public static void main(String[] args) throws Exception {
        Class<?> helper = Class.forName("mooncl.startup.NegativeCache", true, null);
        var get = helper.getMethod("getJarEntry", JarFile.class, String.class);
        var workers = Executors.newFixedThreadPool(2);
        try {
            for (int i = 0; i < 60; i++) {
                Path directory = root.resolve("append-" + i); Files.createDirectories(directory);
                try (Loader loader = new Loader(empty.toUri().toURL())) {
                    for (int j = 0; j < 5; j++) check(loader.getResource("Late.class") == null, "Fixture not empty");
                    CountDownLatch begin = new CountDownLatch(1);
                    var reader = workers.submit(() -> {
                        begin.await();
                        for (int j = 0; j < 60; j++) loader.getResource("Late.class");
                        return true;
                    });
                    var writer = workers.submit(() -> { begin.await(); loader.append(directory.toUri().toURL()); return true; });
                    begin.countDown(); reader.get(5, TimeUnit.SECONDS); writer.get(5, TimeUnit.SECONDS);
                    check(loader.getResource("Late.class") == null, "Empty appended directory not empty");
                    Files.write(directory.resolve("Late.class"), new byte[] {1});
                    check(loader.getResource("Late.class") != null, "Concurrent append hid a new directory class");
                }
            }
            long classHits = (Long) helper.getMethod("classMissHits").invoke(null);
            check(Runtime.version().feature() == 21 ? classHits > 0 : classHits == 0,
                "Whole-path cache must run only on the validated Java 21 layout");
            for (int i = 0; i < 120; i++) {
                JarFile jar = new JarFile(large.toFile());
                CountDownLatch begin = new CountDownLatch(1);
                var reader = workers.submit(() -> {
                    begin.await();
                    try { get.invoke(null, jar, "Missing.class"); }
                    catch (InvocationTargetException expected) {
                        if (!(expected.getCause() instanceof IllegalStateException)) throw expected;
                    }
                    return true;
                });
                var closer = workers.submit(() -> { begin.await(); jar.close(); return true; });
                begin.countDown(); reader.get(5, TimeUnit.SECONDS); closer.get(5, TimeUnit.SECONDS);
                try { get.invoke(null, jar, "Missing.class"); throw new AssertionError("Closed JAR returned cached null"); }
                catch (InvocationTargetException expected) { check(expected.getCause() instanceof IllegalStateException, "Closed JAR exception changed"); }
            }
        } finally { workers.shutdownNow(); }
        for (int i = 0; i < 10; i++) check(prewarmed.getResource("AfterInstall.class") == null, "Prewarmed fixture not empty");
        Files.write(root.resolve("preexisting/AfterInstall.class"), new byte[] {2});
        check(prewarmed.getResource("AfterInstall.class") != null, "Pre-install manifest expansion was lost");
        prewarmed.close();
        // A normal retransform by another agent must preserve both optimization/invalidation hooks.
        instrumentation.retransformClasses(Class.forName("jdk.internal.loader.URLClassPath$JarLoader"));
        Path later = root.resolve("later"); Files.createDirectories(later);
        Path manifest = root.resolve("later.jar"); jar(manifest, "later/", 0);
        try (Loader loader = new Loader(manifest.toUri().toURL())) {
            for (int i = 0; i < 10; i++) check(loader.getResource("Retransformed.class") == null, "Retransform fixture not empty");
            Files.write(later.resolve("Retransformed.class"), new byte[] {3});
            check(loader.getResource("Retransformed.class") != null, "Retransform removed manifest hook");
        }
        helper.getMethod("stop").invoke(null);
        System.out.println("PASS: " + checks + " pre-agent manifest, retransformation, concurrent append and close checks");
    }
}
