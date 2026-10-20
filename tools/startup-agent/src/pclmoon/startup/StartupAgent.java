package pclmoon.startup;

import java.io.InputStream;
import java.lang.instrument.Instrumentation;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Collections;
import java.util.Map;
import java.util.Set;
import java.util.jar.JarFile;

public final class StartupAgent {
    public static void premain(String options, Instrumentation instrumentation) {
        if (Boolean.getBoolean("pclmoon.startup.disabled")) return;
        try {
            if ((Runtime.version().feature() != 17 && Runtime.version().feature() != 21)
                    || !instrumentation.isRetransformClassesSupported()) {
                System.err.println("[PCL-MOON Startup] unsupported JVM; using original lookup");
                return;
            }
            Path helper = Files.createTempFile("pcl-moon-startup-helper-", ".jar");
            helper.toFile().deleteOnExit();
            try (InputStream input = StartupAgent.class.getResourceAsStream("/bootstrap-helper.jar")) {
                if (input == null) throw new IllegalStateException("Missing bootstrap helper");
                Files.copy(input, helper, java.nio.file.StandardCopyOption.REPLACE_EXISTING);
            }
            instrumentation.appendToBootstrapClassLoaderSearch(new JarFile(helper.toFile()));
            Class<?> cache = Class.forName("pclmoon.startup.NegativeCache", true, null);
            Module javaBase = Object.class.getModule();
            instrumentation.redefineModule(javaBase, Set.of(cache.getModule()),
                Map.of("jdk.internal.org.objectweb.asm", Set.of(StartupAgent.class.getModule()),
                       "jdk.internal.org.objectweb.asm.tree", Set.of(StartupAgent.class.getModule()),
                       "jdk.internal.loader", Set.of(cache.getModule())),
                Map.of("jdk.internal.loader", Set.of(cache.getModule())), Collections.emptySet(), Collections.emptyMap());
            long duration = Math.max(1000, Math.min(120000, Long.getLong("pclmoon.startup.durationMs", 60000L)));
            installForgeCache(instrumentation, cache.getModule(), duration);
            if (Runtime.version().feature() == 17) {
                // Forge primarily reads named modules, not URLClassPath. Avoid eagerly
                // indexing unrelated app JARs or patching JDK classes for almost no hits.
                System.err.println("[PCL-MOON Startup] Java 17 Forge-only path; JDK lookup caches not installed");
                return;
            }
            cache.getMethod("start", long.class).invoke(null, duration);
            LookupTransformer transformer = new LookupTransformer();
            // Java 17 still has JarIndex-driven implicit classpaths. Keep that whole-path
            // optimization off until its distinct semantics have their own coverage.
            ClassPathTransformer classpaths = Runtime.version().feature() != 21
                || Boolean.getBoolean("pclmoon.startup.classpathCache.disabled") ? null : new ClassPathTransformer();
            boolean installed = false;
            instrumentation.addTransformer(transformer, true);
            try {
                if (classpaths != null) instrumentation.addTransformer(classpaths, true);
                instrumentation.retransformClasses(Class.forName("jdk.internal.loader.URLClassPath$JarLoader"),
                    java.util.zip.ZipFile.class, Class.forName("jdk.internal.loader.URLClassPath"));
                if (transformer.lookups != 1 || transformer.closeHooks != 1) {
                    cache.getMethod("stop").invoke(null);
                    System.err.println("[PCL-MOON Startup] unknown JDK layout; cache disabled");
                    return;
                }
                if (classpaths != null) {
                    if (classpaths.searches == 2 && classpaths.mutations == 2 && classpaths.manifests == 1) {
                        cache.getMethod("activateClasspaths").invoke(null);
                        System.err.println("[PCL-MOON Startup] versioned classpath miss cache enabled");
                    } else System.err.println("[PCL-MOON Startup] unknown classpath layout; classpath cache disabled");
                }
                installed = true;
            } finally {
                // Keep hooks in subsequent normal retransforms requested by other agents.
                if (!installed) {
                    instrumentation.removeTransformer(transformer);
                    if (classpaths != null) instrumentation.removeTransformer(classpaths);
                }
            }
            System.err.println("[PCL-MOON Startup] bounded JAR miss cache enabled for " + duration + " ms");
        } catch (Throwable error) {
            try { Class.forName("pclmoon.startup.NegativeCache", false, null).getMethod("stop").invoke(null); }
            catch (Throwable ignored) { }
            // An optional optimization must never abort premain or expose command-line credentials.
            System.err.println("[PCL-MOON Startup] unavailable (" + error.getClass().getSimpleName() + "); using original lookup");
        }
    }

    private static void installForgeCache(Instrumentation instrumentation, Module helperModule, long duration) {
        if (Boolean.getBoolean("pclmoon.startup.forgeCache.disabled")) return;
        ForgeReadTransformer transformer = null;
        try {
            Class<?> helper = Class.forName("pclmoon.startup.ForgeReadCache", true, null);
            helper.getMethod("start", long.class).invoke(null, duration);
            transformer = new ForgeReadTransformer(instrumentation, helperModule);
            instrumentation.addTransformer(transformer, true);
            // Do not eagerly resolve Forge classes: its module layers have not necessarily
            // been built yet. Future definitions are transformed by the registered callback.
            for (Class<?> loaded : instrumentation.getAllLoadedClasses()) {
                if ((loaded.getName().equals("cpw.mods.cl.ModuleClassLoader")
                        || loaded.getName().equals("cpw.mods.cl.JarModuleFinder$JarModuleReader"))
                        && instrumentation.isModifiableClass(loaded)) instrumentation.retransformClasses(loaded);
            }
        } catch (Throwable unavailable) {
            if (transformer != null) instrumentation.removeTransformer(transformer);
            try { Class.forName("pclmoon.startup.ForgeReadCache", false, null).getMethod("stop").invoke(null); }
            catch (Throwable ignored) { }
            System.err.println("[PCL-MOON Startup] Forge read cache unavailable; original reads retained");
        }
    }
}
