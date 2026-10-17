import java.io.ByteArrayInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.lang.instrument.ClassFileTransformer;
import java.lang.instrument.Instrumentation;
import java.lang.module.ModuleDescriptor;
import java.lang.module.ModuleReader;
import java.lang.module.ModuleReference;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.net.URI;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.attribute.FileTime;
import java.util.ArrayList;
import java.util.List;
import java.util.Optional;
import java.util.concurrent.Callable;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.jar.JarEntry;
import java.util.jar.JarOutputStream;
import java.util.stream.Stream;

/** Standalone source-17 test: args[0] is an expendable fixture directory. No game/agent required. */
public class ForgeReadCacheTest {
    private static Class<?> cache;
    private static Method start, stop, begin, cached, record, close, stored, hits;
    private static int checks;

    private static void check(boolean condition, String message) {
        checks++;
        if (!condition) throw new AssertionError(message);
    }

    private static Object invoke(Method method, Object... args) throws Exception {
        try { return method.invoke(null, args); }
        catch (InvocationTargetException failure) {
            if (failure.getCause() instanceof Exception exception) throw exception;
            if (failure.getCause() instanceof Error error) throw error;
            throw failure;
        }
    }

    private static void reset() throws Exception { invoke(start, 60_000L); }
    private static long bytes() throws Exception { return (Long) invoke(stored); }
    private static Object ticket(ModuleReader reader, ModuleReference ref, String name) throws Exception {
        return invoke(begin, reader, ref, name);
    }
    private static byte[] hit(Object ticket, String name) throws Exception { return (byte[]) invoke(cached, ticket, name); }
    private static void save(byte[] value, Object ticket, String name) throws Exception { invoke(record, value, ticket, name); }

    private static final class Ref extends ModuleReference {
        Ref(URI location) { super(ModuleDescriptor.newModule("cache.fixture").build(), location); }
        @Override public ModuleReader open() { throw new AssertionError("Cache must never open a reference"); }
    }

    private static final class Reader implements ModuleReader {
        final AtomicInteger opens = new AtomicInteger();
        volatile byte[] content = new byte[] { 9, 8, 7 };
        volatile IOException failure;
        volatile boolean failStream;
        volatile Runnable beforeOpen;
        @Override public Optional<InputStream> open(String name) throws IOException {
            opens.incrementAndGet();
            Runnable action = beforeOpen;
            if (action != null) action.run();
            if (failure != null) throw failure;
            if (failStream) return Optional.of(new InputStream() {
                @Override public int read() throws IOException { throw new IOException("stream failure"); }
            });
            return content == null ? Optional.empty() : Optional.of(new ByteArrayInputStream(content));
        }
        @Override public Optional<URI> find(String name) { return Optional.empty(); }
        @Override public Stream<String> list() { throw new AssertionError("Cache must not enumerate entries"); }
        @Override public void close() {}
        @Override public boolean equals(Object other) { throw new AssertionError("Reader identity, not equals"); }
        @Override public int hashCode() { throw new AssertionError("Reader identity, not hashCode"); }
    }

    // The exact hook ordering: failures from the original operation are deliberately not intercepted.
    private static byte[] read(ModuleReader reader, ModuleReference ref, String name) throws Exception {
        Object ticket = ticket(reader, ref, name);
        byte[] value = hit(ticket, name);
        if (value != null) return value;
        var opened = reader.open(name.replace('.', '/') + ".class");
        if (opened.isEmpty()) value = null;
        else try (InputStream stream = opened.get()) { value = stream.readAllBytes(); }
        save(value, ticket, name);
        return value;
    }

    private static void jar(Path path, int length) throws Exception {
        try (JarOutputStream out = new JarOutputStream(Files.newOutputStream(path))) {
            out.putNextEntry(new JarEntry("sample/Payload.class"));
            out.write(new byte[length]);
            out.closeEntry();
        }
    }

    private static void basics(Path jar) throws Exception {
        reset();
        Ref ref = new Ref(jar.toUri());
        Reader reader = new Reader();
        byte[] first = read(reader, ref, "sample.Payload");
        first[0] = 99;
        byte[] second = read(reader, ref, "sample.Payload");
        check(second[0] == 9 && second != first && reader.opens.get() == 1, "record must clone raw bytes");
        second[1] = 99;
        check(read(reader, ref, "sample.Payload")[1] == 8, "hits must return independent clones");
        long count = (Long) invoke(hits);
        check(Boolean.getBoolean("mooncl.startup.metrics") ? count >= 2 : count == 0, "metrics opt-in");
        Reader otherReader = new Reader();
        otherReader.content = new byte[] { 22 };
        check(read(otherReader, ref, "sample.Payload")[0] == 22 && otherReader.opens.get() == 1, "reader identity isolation");
        Ref otherRef = new Ref(jar.toUri());
        reader.content = new byte[] { 33 };
        check(read(reader, otherRef, "sample.Payload")[0] == 33 && reader.opens.get() == 2, "reference identity isolation");
        check(read(reader, ref, "different.Name")[0] == 33 && reader.opens.get() == 3, "name isolation");
        Object ticket = ticket(reader, ref, "one.Name");
        save(new byte[] { 1 }, ticket, "another.Name");
        check(hit(ticket, "another.Name") == null && hit(ticket, "one.Name") == null, "ticket name cannot be substituted");
        check(ticket(reader, ref, "x".repeat(513)) == null, "name length bound");
        check(ticket(reader, ref, null) == null && ticket(null, ref, "x") == null && ticket(reader, null, "x") == null,
                "invalid arguments safely bypass");
        String boundary = "x".repeat(512);
        Object boundaryTicket = ticket(reader, ref, boundary);
        save(new byte[] { 4 }, boundaryTicket, boundary);
        check(hit(boundaryTicket, boundary)[0] == 4, "512-character names remain eligible");
    }

    private static void nonPositive(Path jar) throws Exception {
        reset();
        Ref ref = new Ref(jar.toUri());
        Reader reader = new Reader();
        reader.content = null;
        check(read(reader, ref, "missing") == null && read(reader, ref, "missing") == null && reader.opens.get() == 2,
                "missing results are never cached");
        reader.content = new byte[0];
        check(read(reader, ref, "empty").length == 0 && read(reader, ref, "empty").length == 0 && reader.opens.get() == 4,
                "empty arrays are never cached");
        IOException sentinel = new IOException("original failure");
        reader.failure = sentinel;
        for (int i = 0; i < 2; i++) {
            try { read(reader, ref, "throws"); throw new AssertionError("Read failure swallowed"); }
            catch (IOException expected) { check(expected == sentinel, "original exception identity preserved"); }
        }
        reader.failure = null;
        reader.failStream = true;
        try { read(reader, ref, "stream"); throw new AssertionError("Stream failure swallowed"); }
        catch (IOException expected) { check(expected.getMessage().equals("stream failure"), "stream exception preserved"); }
        check(bytes() == 0, "exceptions must not leave positive cache entries");
        reader.failStream = false;
        reader.content = new byte[] { 6 };
        check(read(reader, ref, "throws")[0] == 6, "exception does not poison future original reads");
    }

    private static void invalidation(Path jar, Path root) throws Exception {
        reset();
        Reader reader = new Reader();
        Ref ref = new Ref(jar.toUri());
        read(reader, ref, "changed");
        Object old = ticket(reader, ref, "changed");
        FileTime originalTime = Files.getLastModifiedTime(jar);
        Files.setLastModifiedTime(jar, FileTime.fromMillis(originalTime.toMillis() + 4000));
        check(hit(old, "changed") == null && bytes() == 0, "mtime changes invalidate existing hits");
        save(new byte[] { 1 }, old, "changed");
        check(ticket(reader, ref, "changed") == null && bytes() == 0, "invalid ticket cannot be revived");
        reset();
        Object duringRead = ticket(reader, ref, "changed");
        jar(jar, 800);
        save(new byte[] { 1 }, duringRead, "changed");
        check(bytes() == 0 && hit(duringRead, "changed") == null, "change between begin and record rejects old output");
        reset();
        read(reader, ref, "alreadyStored");
        Object duplicate = ticket(reader, ref, "alreadyStored");
        Files.setLastModifiedTime(jar, FileTime.fromMillis(Files.getLastModifiedTime(jar).toMillis() + 4000));
        save(new byte[] { 1 }, duplicate, "alreadyStored");
        check(bytes() == 0 && ticket(reader, ref, "alreadyStored") == null, "even duplicate records recheck source metadata");
        reset();
        read(reader, ref, "sameMetadata");
        Object sameMetadata = ticket(reader, ref, "sameMetadata");
        Path sameCopy = root.resolve("same-metadata.jar");
        Files.copy(jar, sameCopy, java.nio.file.StandardCopyOption.REPLACE_EXISTING);
        Files.setLastModifiedTime(sameCopy, Files.getLastModifiedTime(jar));
        Object fileKey = Files.readAttributes(jar, java.nio.file.attribute.BasicFileAttributes.class).fileKey();
        Files.move(sameCopy, jar, java.nio.file.StandardCopyOption.REPLACE_EXISTING);
        if (fileKey != null) check(hit(sameMetadata, "sameMetadata") == null, "fileKey detects replacement with unchanged size/mtime");
        reset();
        read(reader, ref, "replacement");
        Object replacement = ticket(reader, ref, "replacement");
        Path alternate = root.resolve("replacement.jar");
        jar(alternate, 2700);
        Files.move(alternate, jar, java.nio.file.StandardCopyOption.REPLACE_EXISTING);
        check(hit(replacement, "replacement") == null, "same-path replacement invalidates");
        Path directory = root.resolve("dynamic.jar");
        Files.createDirectories(directory);
        Ref dynamic = new Ref(directory.toUri());
        check(ticket(reader, dynamic, "dynamic") == null, "directories are never eligible, even with .jar suffix");
        Files.write(directory.resolve("new.class"), new byte[] { 5 });
        check(ticket(reader, dynamic, "new") == null, "dynamic directory remains bypassed");
        Path notJar = root.resolve("plain.bin");
        Files.write(notJar, new byte[] { 1 });
        check(ticket(reader, new Ref(notJar.toUri()), "x") == null, "non-JAR regular files bypass");
        check(ticket(reader, new Ref(URI.create("https://invalid.example/source.jar")), "x") == null, "unknown schemes bypass");
        check(ticket(reader, new Ref(null), "x") == null, "absent locations bypass");
        check(ticket(reader, new Ref(URI.create("union:/unregistered-fixture.jar%230!/")), "x") == null,
                "unavailable union provider safely bypasses");
    }

    private static void closeAndStop(Path jar) throws Exception {
        reset();
        Reader reader = new Reader();
        Ref ref = new Ref(jar.toUri());
        Object old = ticket(reader, ref, "closed");
        save(new byte[] { 1 }, old, "closed");
        invoke(close, reader);
        save(new byte[] { 1 }, old, "closed");
        check(bytes() == 0 && hit(old, "closed") == null && ticket(reader, ref, "fresh") == null, "close invalidates and tombstones reader");
        Reader neverSeen = new Reader();
        invoke(close, neverSeen);
        check(ticket(neverSeen, ref, "x") == null, "close before first begin stays closed");
        reset();
        Object previousGeneration = ticket(reader, ref, "stopped");
        invoke(stop);
        save(new byte[] { 1 }, previousGeneration, "stopped");
        check(bytes() == 0 && ticket(reader, ref, "stopped") == null, "stop forbids writes and new states");
        reset();
        save(new byte[] { 1 }, previousGeneration, "stopped");
        check(bytes() == 0, "restart never revives tickets from an old generation");
        invoke(start, 120L);
        Object expiry = ticket(new Reader(), ref, "expiry");
        save(new byte[] { 1 }, expiry, "expiry");
        check(Thread.getAllStackTraces().keySet().stream().anyMatch(t -> t.getName().equals("mooncl-forge-read-cache") && t.isDaemon()),
                "expiry timer is daemon");
        Thread.sleep(350);
        save(new byte[] { 1 }, expiry, "expiry");
        check(bytes() == 0 && hit(expiry, "expiry") == null && ticket(new Reader(), ref, "x") == null, "TTL stops cache and releases data");
    }

    private static void concurrency(Path jar) throws Exception {
        reset();
        Reader reader = new Reader();
        Ref ref = new Ref(jar.toUri());
        read(reader, ref, "parallel");
        var pool = Executors.newFixedThreadPool(6);
        try {
            List<Callable<Boolean>> work = new ArrayList<>();
            for (int worker = 0; worker < 12; worker++) work.add(() -> {
                for (int i = 0; i < 150; i++) {
                    byte[] result = read(reader, ref, "parallel");
                    if (result[0] != 9) return false;
                    result[0] = 0;
                }
                return true;
            });
            for (var result : pool.invokeAll(work)) check(result.get(), "concurrent hit/clone isolation");
            check(reader.opens.get() == 1 && bytes() == 3, "concurrent hits do not reopen or duplicate storage");
            for (boolean stopInstead : new boolean[] { false, true }) {
                reset();
                Reader blocked = new Reader();
                CountDownLatch entered = new CountDownLatch(1), release = new CountDownLatch(1);
                blocked.beforeOpen = () -> {
                    entered.countDown();
                    try { if (!release.await(5, TimeUnit.SECONDS)) throw new AssertionError("blocked original timed out"); }
                    catch (InterruptedException failure) { throw new AssertionError(failure); }
                };
                var read = pool.submit(() -> read(blocked, ref, "inflight"));
                try {
                    check(entered.await(3, TimeUnit.SECONDS), "original read started");
                    pool.submit(() -> { if (stopInstead) invoke(stop); else invoke(close, blocked); return null; }).get(2, TimeUnit.SECONDS);
                } finally { release.countDown(); }
                check(read.get(3, TimeUnit.SECONDS)[0] == 9 && bytes() == 0, "close/stop never blocks on original I/O or permits late record");
            }
        } finally { pool.shutdownNow(); }
    }

    private static void budgets(Path jar) throws Exception {
        reset();
        Ref ref = new Ref(jar.toUri());
        Reader reader = new Reader();
        Object huge = ticket(reader, ref, "huge");
        save(new byte[1024 * 1024 + 1], huge, "huge");
        check(bytes() == 0, "single class exceeds 1 MiB");
        byte[] oneMiB = new byte[1024 * 1024];
        for (int i = 0; i < 33; i++) save(oneMiB, ticket(reader, ref, "bulk" + i), "bulk" + i);
        check(bytes() == 32L * 1024 * 1024, "global byte budget is 32 MiB");
        check(hit(ticket(reader, ref, "bulk32"), "bulk32") == null, "over-budget class bypasses");
        invoke(close, reader);
        check(bytes() == 0, "close releases byte budget");
        reset();
        for (int i = 0; i < 16385; i++) save(new byte[] { 1 }, ticket(reader, ref, "entry" + i), "entry" + i);
        check(bytes() == 16384 && hit(ticket(reader, ref, "entry16384"), "entry16384") == null, "entry cap is 16384");
        reset();
        for (int i = 0; i < 128; i++) check(ticket(new Reader(), ref, "x") != null, "reader within budget");
        check(ticket(new Reader(), ref, "x") == null, "reader cap is 128 identities");
        invoke(stop);
        check(bytes() == 0, "stop releases all budgets");
    }

    private static final class DefinitionLoader extends ClassLoader {
        DefinitionLoader() { super(ForgeReadCacheTest.class.getClassLoader()); }
        Class<?> define(byte[] bytes) { return defineClass(null, bytes, 0, bytes.length); }
    }

    private static byte[] fixture(String name) throws Exception {
        try (InputStream stream = ForgeReadCacheTest.class.getResourceAsStream("/" + name + ".class")) {
            return stream == null ? null : stream.readAllBytes();
        }
    }

    private static void transformer(Path jar) throws Exception {
        String loaderName = "cpw/mods/cl/ModuleClassLoader", readerName = "cpw/mods/cl/JarModuleFinder$JarModuleReader";
        byte[] loaderBytes = fixture(loaderName), readerBytes = fixture(readerName);
        if (loaderBytes == null || readerBytes == null) {
            System.out.println("SKIP: transformer fixtures were not compiled (cache API tests still executed)");
            return;
        }
        Instrumentation instrumentation = (Instrumentation) Proxy.newProxyInstance(ForgeReadCacheTest.class.getClassLoader(),
                new Class<?>[] { Instrumentation.class }, (proxy, method, args) -> {
                    if (method.getName().equals("getAllLoadedClasses")) return new Class<?>[0];
                    if (method.getName().equals("redefineModule")) return null;
                    throw new AssertionError("Unexpected instrumentation operation: " + method.getName());
                });
        ClassFileTransformer transformer = (ClassFileTransformer) Class.forName("mooncl.startup.ForgeReadTransformer")
                .getConstructor(Instrumentation.class, Module.class).newInstance(instrumentation, cache.getModule());
        check(transformer.transform(null, "unrelated/Type", null, null, loaderBytes) == null, "unmatched class stays unchanged");
        check(transformer.transform(null, loaderName, null, null, readerBytes) == null, "mismatched internal name stays unchanged");
        byte[] patched = transformer.transform(null, null, loaderName, null, null, loaderBytes);
        check(patched != null, "target read method patched");
        check(transformer.transform(null, loaderName, null, null, patched) == null, "duplicate patch safely skipped");
        Class<?> fixture = new DefinitionLoader().define(patched);
        Object instance = fixture.getConstructor().newInstance();
        Method pipeline = fixture.getMethod("pipeline", ModuleReader.class, ModuleReference.class, String.class);
        Method raw = fixture.getMethod("read", ModuleReader.class, ModuleReference.class, String.class);
        reset();
        Reader reader = new Reader();
        Ref ref = new Ref(jar.toUri());
        for (int i = 0; i < 4; i++) check(((byte[]) pipeline.invoke(instance, reader, ref, "pipeline"))[0] == 8,
                "each pipeline gets original bytes before mutation");
        check(reader.opens.get() == 1 && fixture.getField("transforms").getInt(instance) == 4
                && fixture.getField("signatures").getInt(instance) == 4, "cache hits never bypass downstream transform/signature");
        check(((byte[]) fixture.getMethod("getClassBytes", String.class).invoke(instance, "overload"))[0] == 42,
                "unmatched method overload untouched");
        reader.content = null;
        check(raw.invoke(instance, reader, ref, "absent") == null, "patched null return verified");
        reader.content = new byte[0];
        check(((byte[]) raw.invoke(instance, reader, ref, "empty")).length == 0, "patched empty return verified");
        IOException sentinel = new IOException("patched original failure");
        reader.failure = sentinel;
        try { raw.invoke(instance, reader, ref, "failure"); throw new AssertionError("Patched method swallowed exception"); }
        catch (InvocationTargetException expected) { check(expected.getCause() == sentinel, "patched original exception preserved"); }
        byte[] patchedClose = transformer.transform(null, readerName, null, null, readerBytes);
        check(patchedClose != null, "target close method patched");
        Class<?> closeFixture = new DefinitionLoader().define(patchedClose);
        for (boolean fail : new boolean[] { false, true }) {
            reset();
            ModuleReader wrapped = (ModuleReader) closeFixture.getConstructor(ModuleReader.class).newInstance(new Reader());
            Object ticket = ticket(wrapped, ref, "closing");
            save(new byte[] { 1 }, ticket, "closing");
            closeFixture.getField("failClose").setBoolean(wrapped, fail);
            try { wrapped.close(); check(!fail, "close exception expected"); }
            catch (IOException expected) { check(fail && expected.getMessage().equals("fixture close failure"), "original close exception preserved"); }
            save(new byte[] { 1 }, ticket, "closing");
            check(bytes() == 0 && ticket(wrapped, ref, "x") == null, "close hook invalidates even on original close failure");
        }
    }

    public static void main(String[] args) throws Exception {
        Path root = Path.of(args[0]);
        Files.createDirectories(root);
        try { cache = Class.forName("mooncl.startup.ForgeReadCache", true, null); }
        catch (ClassNotFoundException standalone) { cache = Class.forName("mooncl.startup.ForgeReadCache"); }
        Class<?> ticket = Class.forName("mooncl.startup.ForgeReadCache$Ticket", false, cache.getClassLoader());
        start = cache.getMethod("start", long.class); stop = cache.getMethod("stop");
        begin = cache.getMethod("begin", ModuleReader.class, ModuleReference.class, String.class);
        cached = cache.getMethod("cached", ticket, String.class);
        record = cache.getMethod("record", byte[].class, ticket, String.class);
        close = cache.getMethod("closeReader", Object.class);
        stored = cache.getMethod("storedBytes"); hits = cache.getMethod("hitCount");
        Path jar = root.resolve("source.jar");
        jar(jar, 64);
        try {
            basics(jar); nonPositive(jar); invalidation(jar, root); closeAndStop(jar);
            concurrency(jar); budgets(jar); transformer(jar);
        } finally { invoke(stop); }
        System.out.println("PASS: " + checks + " Forge raw-byte cache, invalidation, concurrency, bounds and transformer checks");
    }
}
