package pclmoon.startup;

import java.lang.instrument.ClassFileTransformer;
import java.lang.instrument.Instrumentation;
import java.security.ProtectionDomain;
import java.util.Map;
import java.util.Objects;
import java.util.Set;
import java.util.concurrent.atomic.AtomicBoolean;
import jdk.internal.org.objectweb.asm.ClassReader;
import jdk.internal.org.objectweb.asm.ClassWriter;
import jdk.internal.org.objectweb.asm.Opcodes;
import jdk.internal.org.objectweb.asm.tree.AbstractInsnNode;
import jdk.internal.org.objectweb.asm.tree.ClassNode;
import jdk.internal.org.objectweb.asm.tree.InsnNode;
import jdk.internal.org.objectweb.asm.tree.MethodInsnNode;
import jdk.internal.org.objectweb.asm.tree.MethodNode;
import jdk.internal.org.objectweb.asm.tree.VarInsnNode;

/** Method-body-only hook at Forge's raw read boundary, before transform/signature/defineClass. */
public final class ForgeReadTransformer implements ClassFileTransformer {
    private static final String LOADER = "cpw/mods/cl/ModuleClassLoader";
    private static final String HELPER = "pclmoon/startup/ForgeDirectRead";
    private static final String READ_DESC = "(Ljava/lang/module/ModuleReader;Ljava/lang/module/ModuleReference;Ljava/lang/String;)[B";
    private static final AtomicBoolean LOGGED = new AtomicBoolean();
    // Unmodified securejarhandler 2.1.10 ModuleClassLoader.class, independently inspected.
    // Any other version or earlier agent edit keeps its original read body.
    private static final String VERIFIED_LOADER_SHA256 = "62e3eaa069098d55f5da70e6dbc2a35a1e622d68804b2af6049583151bcb6f16";
    private final Instrumentation instrumentation;
    private final Module helperModule;

    public ForgeReadTransformer(Instrumentation instrumentation, Module helperModule) {
        this.instrumentation = Objects.requireNonNull(instrumentation);
        this.helperModule = Objects.requireNonNull(helperModule);
    }

    @Override public byte[] transform(Module module, ClassLoader loader, String name, Class<?> type,
            ProtectionDomain domain, byte[] bytes) {
        if (!LOADER.equals(name)) return null;
        try {
            // Only the byte-identical 2.1.10 loader body may be replaced; every other shape
            // (other versions, earlier edits) keeps its original read logic untouched.
            if (!verifiedLoader(bytes)) return null;
            ClassNode node = new ClassNode(Opcodes.ASM8);
            new ClassReader(bytes).accept(node, ClassReader.EXPAND_FRAMES);
            if (!name.equals(node.name)) return null;
            boolean changed = false;
            for (MethodNode method : node.methods) {
                if ((method.access & (Opcodes.ACC_STATIC | Opcodes.ACC_ABSTRACT | Opcodes.ACC_NATIVE)) != 0) continue;
                if (method.name.equals("getClassBytes") && method.desc.equals(READ_DESC) && !alreadyHooked(method)) {
                    patchDirectRead(method);
                    changed = true;
                }
            }
            if (!changed) return null;
            configureModules(module);
            // Preserve original type merges; COMPUTE_FRAMES would load Forge/Mod classes recursively.
            ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
            node.accept(writer);
            byte[] result = writer.toByteArray();
            if (LOGGED.compareAndSet(false, true)) {
                try { System.err.println("[pcl-moon-startup] verified Forge 2.1.10 direct-read path enabled"); }
                catch (Throwable ignored) { /* Logging must not affect class loading. */ }
            }
            return result;
        } catch (Throwable ignored) { return null; } // Unsupported shapes/modules always retain original behavior.
    }

    @Override public byte[] transform(ClassLoader loader, String name, Class<?> type,
            ProtectionDomain domain, byte[] bytes) {
        return transform(type == null ? null : type.getModule(), loader, name, type, domain, bytes);
    }

    private static boolean alreadyHooked(MethodNode method) {
        for (AbstractInsnNode instruction : method.instructions) {
            if (instruction instanceof MethodInsnNode call && call.owner.equals(HELPER)) return true;
        }
        return false;
    }

    private static boolean verifiedLoader(byte[] bytes) {
        try {
            return java.util.HexFormat.of().formatHex(java.security.MessageDigest.getInstance("SHA-256").digest(bytes))
                .equals(VERIFIED_LOADER_SHA256);
        } catch (java.security.NoSuchAlgorithmException unavailable) { return false; }
    }

    private static void patchDirectRead(MethodNode method) {
        method.instructions.clear();
        method.tryCatchBlocks.clear();
        if (method.localVariables != null) method.localVariables.clear();
        if (method.visibleLocalVariableAnnotations != null) method.visibleLocalVariableAnnotations.clear();
        if (method.invisibleLocalVariableAnnotations != null) method.invisibleLocalVariableAnnotations.clear();
        method.instructions.add(new VarInsnNode(Opcodes.ALOAD, 1));
        method.instructions.add(new VarInsnNode(Opcodes.ALOAD, 2));
        method.instructions.add(new VarInsnNode(Opcodes.ALOAD, 3));
        method.instructions.add(new MethodInsnNode(Opcodes.INVOKESTATIC, HELPER, "read", READ_DESC, false));
        method.instructions.add(new InsnNode(Opcodes.ARETURN));
        method.maxLocals = 4;
        method.maxStack = 3;
    }

    private void configureModules(Module target) {
        if (target == null || target == helperModule) return;
        if (!target.canRead(helperModule)) {
            instrumentation.redefineModule(target, Set.of(helperModule), Map.of(), Map.of(), Set.of(), Map.of());
        }
        if (helperModule.isNamed() && !helperModule.isExported("pclmoon.startup", target)) {
            instrumentation.redefineModule(helperModule, Set.of(),
                    Map.of("pclmoon.startup", Set.of(target)), Map.of(), Set.of(), Map.of());
        }
    }
}
