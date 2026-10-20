import java.lang.reflect.Field;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Map;
import java.util.jar.JarEntry;
import java.util.jar.JarFile;
import java.util.jar.JarOutputStream;

public final class TimeoutTest {
    public static void main(String[] args) throws Exception {
        if (Runtime.version().feature() == 17) {
            // The Forge-only path has no cache timer; verify premain installed the bootstrap helper.
            Class<?> direct = Class.forName("pclmoon.startup.ForgeDirectRead", true, null);
            if (direct.getClassLoader() != null) throw new AssertionError("ForgeDirectRead must live on the bootstrap loader");
            direct.getMethod("summary").invoke(null);
            System.out.println("PASS: Java 17 premain installs the Forge direct-read helper without a cache timer");
            return;
        }
        Path path = Path.of(args[0]).resolve("timeout.jar");
        try (JarOutputStream out = new JarOutputStream(Files.newOutputStream(path))) {
            out.putNextEntry(new JarEntry("Existing.class")); out.write(1); out.closeEntry();
        }
        Class<?> helper = Class.forName("pclmoon.startup.NegativeCache", true, null);
        var lookup = helper.getMethod("getJarEntry", JarFile.class, String.class);
        try (JarFile jar = new JarFile(path.toFile())) {
            if (lookup.invoke(null, jar, "Missing.class") != null) throw new AssertionError("Absent resource exists");
            long deadline = System.nanoTime() + 5_000_000_000L;
            Field enabled = helper.getDeclaredField("enabled"); enabled.setAccessible(true);
            while (enabled.getBoolean(null) && System.nanoTime() < deadline) Thread.sleep(20);
            if (enabled.getBoolean(null)) throw new AssertionError("Cache timeout did not run");
            for (String name : new String[] {"indexes", "classpaths", "closing"}) {
                Field field = helper.getDeclaredField(name); field.setAccessible(true);
                // The timer clears maps after publishing enabled=false. Wait for completion, not a race.
                while (!((Map<?, ?>) field.get(null)).isEmpty() && System.nanoTime() < deadline) Thread.sleep(20);
                if (!((Map<?, ?>) field.get(null)).isEmpty()) throw new AssertionError("Retained cache after timeout: " + name);
            }
            if (lookup.invoke(null, jar, "Existing.class") == null) throw new AssertionError("Fallback lost existing entry");
        }
        Files.delete(path);
        System.out.println("PASS: actual timer disabled caches, released all maps, and retained original lookup");
    }
}
