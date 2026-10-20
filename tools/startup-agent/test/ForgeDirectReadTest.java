import java.io.InputStream;
import java.lang.instrument.ClassFileTransformer;
import java.lang.instrument.Instrumentation;
import java.lang.reflect.Proxy;
import java.nio.file.DirectoryStream;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;

/** Transformer gating of the Forge direct-read hook, plus helper-JAR placement checks.
 * args[0] is an expendable fixture directory; args[1] is the tested agent JAR path. */
public final class ForgeDirectReadTest {
    private static int checks;
    private static void check(boolean condition, String message) {
        checks++;
        if (!condition) throw new AssertionError(message);
    }
    private static byte[] fixture(String name) throws Exception {
        try (InputStream stream = ForgeDirectReadTest.class.getResourceAsStream("/" + name + ".class")) {
            return stream == null ? null : stream.readAllBytes();
        }
    }
    private static List<Path> helpers(Path directory) throws Exception {
        List<Path> found = new ArrayList<>();
        try (DirectoryStream<Path> stream = Files.newDirectoryStream(directory, "pcl-moon-startup-helper-*")) {
            for (Path entry : stream) found.add(entry);
        }
        return found;
    }
    public static void main(String[] args) throws Exception {
        Class<?> direct = Class.forName("pclmoon.startup.ForgeDirectRead", true, null);
        Instrumentation instrumentation = (Instrumentation) Proxy.newProxyInstance(
                ForgeDirectReadTest.class.getClassLoader(), new Class<?>[] { Instrumentation.class },
                (proxy, method, methodArgs) -> {
                    if (method.getName().equals("getAllLoadedClasses")) return new Class<?>[0];
                    if (method.getName().equals("redefineModule")) return null;
                    throw new AssertionError("Unexpected instrumentation operation: " + method.getName());
                });
        ClassFileTransformer transformer = (ClassFileTransformer) Class.forName("pclmoon.startup.ForgeReadTransformer")
                .getConstructor(Instrumentation.class, Module.class).newInstance(instrumentation, direct.getModule());
        String loaderName = "cpw/mods/cl/ModuleClassLoader", readerName = "cpw/mods/cl/JarModuleFinder$JarModuleReader";
        byte[] loaderBytes = fixture(loaderName), readerBytes = fixture(readerName);
        check(loaderBytes != null && readerBytes != null, "Transformer fixtures were not compiled");
        check(transformer.transform(null, "unrelated/Type", null, null, loaderBytes) == null, "unmatched class stays unchanged");
        check(transformer.transform(null, loaderName, null, null, readerBytes) == null, "mismatched internal name stays unchanged");
        check(transformer.transform(null, readerName, null, null, readerBytes) == null, "reader close hook removed entirely");
        // The fixture loader is not byte-identical to securejarhandler 2.1.10, so it must be refused.
        check(transformer.transform(null, null, loaderName, null, null, loaderBytes) == null, "unverified loader hash keeps original body");
        byte[] mutated = loaderBytes.clone();
        mutated[mutated.length - 1] ^= 1;
        check(transformer.transform(null, null, loaderName, null, null, mutated) == null, "mutated loader bytes refused");
        // The helper JAR is content-named next to the agent JAR, stable across runs, never in %TEMP%.
        Path agent = Path.of(args[1]).toAbsolutePath().normalize();
        List<Path> helpers = helpers(agent.getParent());
        check(helpers.size() == 1, "expected exactly one pcl-moon-startup-helper-*.jar beside agent, found " + helpers);
        check(helpers.get(0).getFileName().toString().endsWith(".jar"), "helper must keep .jar suffix: " + helpers.get(0));
        check(helpers(Path.of(System.getProperty("java.io.tmpdir"))).isEmpty(), "helper must not be written under tmpdir");
        System.out.println("PASS: " + checks + " Forge direct-read transformer gating and helper placement checks");
    }
}
