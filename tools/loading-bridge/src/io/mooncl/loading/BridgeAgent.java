package io.mooncl.loading;

import java.io.*;
import java.lang.instrument.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.security.ProtectionDomain;

/** Replaces ONLY the MLS display subprocess main, preserving its binary event protocol. */
public final class BridgeAgent {
    private static final String TARGET = "io/github/gaming32/modloadingscreen/ActualLoadingScreen";
    private static final String RELAY = "io/mooncl/loading/ProgressRelay";

    public static void premain(String ignored, Instrumentation instrumentation) {
        if (System.getenv("MOONCL_MLS_PROGRESS_FILE") == null || System.getenv("MOONCL_MLS_PROGRESS_TOKEN") == null) return;
        try {
            String ownJar = Paths.get(BridgeAgent.class.getProtectionDomain().getCodeSource().getLocation().toURI()).toString();
            // MLS builds the display subprocess classpath from this property. Do not alter the actual game loader.
            System.setProperty("java.class.path", System.getProperty("java.class.path", "") + File.pathSeparator + ownJar);
            instrumentation.addTransformer(new ClassFileTransformer() {
                @Override public byte[] transform(ClassLoader loader, String name, Class<?> redef, ProtectionDomain domain, byte[] bytes) {
                    if (!TARGET.equals(name)) return null;
                    try {
                        byte[] rewritten = redirectDisplayMain(bytes);
                        if (rewritten != null) System.out.println("[MOONCL] mod-loading-screen native progress relay enabled");
                        return rewritten;
                    } catch (Throwable ex) {
                        System.err.println("[MOONCL] MLS bridge unavailable; original loading screen preserved: " + ex);
                        return null;
                    }
                }
            });
        } catch (Throwable ex) {
            // Optional presentation must never prevent the game from booting.
            System.err.println("[MOONCL] Cannot enable loading progress bridge: " + ex);
        }
    }

    /** Adds one UTF8 constant, redirects matching String constants only, never Class/name/descriptors. No ASM/JDK internals. */
    static byte[] redirectDisplayMain(byte[] bytes) throws IOException {
        DataInputStream in = new DataInputStream(new ByteArrayInputStream(bytes));
        if (in.readInt() != 0xcafebabe) throw new IOException("Invalid class file");
        in.readUnsignedShort(); in.readUnsignedShort();
        int count = in.readUnsignedShort();
        if (count == 65535) return null;
        String[] utf = new String[count]; int[] stringOffsets = new int[count];
        int p = 10;
        for (int i = 1; i < count; i++) {
            int tag = bytes[p++] & 255;
            switch (tag) {
                case 1: int length = u16(bytes, p); p += 2; utf[i] = new String(bytes, p, length, StandardCharsets.UTF_8); p += length; break;
                case 3: case 4: p += 4; break;
                case 5: case 6: p += 8; i++; break;
                case 7: case 16: case 19: case 20: p += 2; break;
                case 8: stringOffsets[i] = p; p += 2; break;
                case 9: case 10: case 11: case 12: case 17: case 18: p += 4; break;
                case 15: p += 3; break;
                default: throw new IOException("Unknown constant tag " + tag);
            }
        }
        byte[] pool = bytes.clone(); boolean changed = false;
        for (int i = 1; i < count; i++) {
            int offset = stringOffsets[i];
            if (offset != 0 && TARGET.equals(utf[u16(bytes, offset)])) {
                pool[offset] = (byte)(count >>> 8); pool[offset + 1] = (byte)count; changed = true;
            }
        }
        if (!changed) return null; // Unknown MLS build: fail open without modifying anything.
        ByteArrayOutputStream result = new ByteArrayOutputStream(bytes.length + 80);
        DataOutputStream out = new DataOutputStream(result);
        out.write(pool, 0, 8); out.writeShort(count + 1); out.write(pool, 10, p - 10);
        out.writeByte(1); out.writeUTF(RELAY); out.write(pool, p, pool.length - p);
        return result.toByteArray();
    }
    private static int u16(byte[] bytes, int p) { return (bytes[p] & 255) << 8 | bytes[p + 1] & 255; }
}
