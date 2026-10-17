package cpw.mods.cl;

import java.io.IOException;
import java.io.InputStream;
import java.lang.module.ModuleReader;
import java.lang.module.ModuleReference;

/** Independent boundary fixture, not Forge implementation code. */
public class ModuleClassLoader extends ClassLoader {
    public int transforms, signatures;

    protected byte[] getClassBytes(ModuleReader reader, ModuleReference ref, String name) throws IOException {
        if (ref == null) return null;
        long wideLocal = name.length();
        double secondWideLocal = wideLocal / 2.0;
        if (secondWideLocal < 0) return new byte[0];
        var input = reader.open(name.replace('.', '/') + ".class");
        if (input.isEmpty()) return null;
        try (InputStream stream = input.get()) {
            byte[] bytes = stream.readAllBytes();
            if (wideLocal == Long.MIN_VALUE) throw new IOException("unreachable");
            return bytes;
        }
    }

    public byte[] read(ModuleReader reader, ModuleReference ref, String name) throws IOException {
        return getClassBytes(reader, ref, name);
    }

    public byte[] pipeline(ModuleReader reader, ModuleReference ref, String name) throws IOException {
        byte[] raw = getClassBytes(reader, ref, name);
        transforms++;
        if (raw != null && raw.length > 0) raw[0] ^= 1;
        signatures++;
        return raw;
    }

    // This overload must not be patched.
    public byte[] getClassBytes(String ignored) { return new byte[] { 42 }; }
}
