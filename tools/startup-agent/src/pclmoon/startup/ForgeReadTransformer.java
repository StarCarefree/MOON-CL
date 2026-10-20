package pclmoon.startup;

import java.lang.instrument.ClassFileTransformer;
import java.lang.instrument.Instrumentation;
import java.security.ProtectionDomain;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.Set;
import java.util.concurrent.atomic.AtomicBoolean;
import jdk.internal.org.objectweb.asm.ClassReader;
import jdk.internal.org.objectweb.asm.ClassWriter;
import jdk.internal.org.objectweb.asm.Opcodes;
import jdk.internal.org.objectweb.asm.tree.AbstractInsnNode;
import jdk.internal.org.objectweb.asm.tree.ClassNode;
import jdk.internal.org.objectweb.asm.tree.FrameNode;
import jdk.internal.org.objectweb.asm.tree.InsnList;
import jdk.internal.org.objectweb.asm.tree.InsnNode;
import jdk.internal.org.objectweb.asm.tree.JumpInsnNode;
import jdk.internal.org.objectweb.asm.tree.LabelNode;
import jdk.internal.org.objectweb.asm.tree.MethodInsnNode;
import jdk.internal.org.objectweb.asm.tree.MethodNode;
import jdk.internal.org.objectweb.asm.tree.VarInsnNode;

/** Method-body-only hooks at Forge's raw read boundary, before transform/signature/defineClass. */
public final class ForgeReadTransformer implements ClassFileTransformer {
    private static final String LOADER = "cpw/mods/cl/ModuleClassLoader";
    private static final String READER = "cpw/mods/cl/JarModuleFinder$JarModuleReader";
    private static final String HELPER = "pclmoon/startup/ForgeReadCache";
    private static final String TICKET = HELPER + "$Ticket";
    private static final String TICKET_DESC = "L" + TICKET + ";";
    private static final String READ_DESC = "(Ljava/lang/module/ModuleReader;Ljava/lang/module/ModuleReference;Ljava/lang/String;)[B";
    private static final String UNION_PACKAGE = "cpw.mods.niofs.union";
    private static final AtomicBoolean LOGGED = new AtomicBoolean();
    private static final AtomicBoolean FAST_LOGGED = new AtomicBoolean();
    // Unmodified securejarhandler 2.1.10 ModuleClassLoader.class, independently inspected.
    // Any other version or earlier agent edit keeps its original read body.
    private static final String VERIFIED_LOADER_SHA256 = "62e3eaa069098d55f5da70e6dbc2a35a1e622d68804b2af6049583151bcb6f16";
    private final Instrumentation instrumentation;
    private final Module helperModule;

    public ForgeReadTransformer(Instrumentation instrumentation, Module helperModule) {
        this.instrumentation = Objects.requireNonNull(instrumentation);
        this.helperModule = Objects.requireNonNull(helperModule);
    }

    private static MethodInsnNode call(String name, String desc) {
        return new MethodInsnNode(Opcodes.INVOKESTATIC, HELPER, name, desc, false);
    }

    @Override public byte[] transform(Module module, ClassLoader loader, String name, Class<?> type,
            ProtectionDomain domain, byte[] bytes) {
        if (!LOADER.equals(name) && !READER.equals(name)) return null;
        try {
            ClassNode node = new ClassNode(Opcodes.ASM8);
            new ClassReader(bytes).accept(node, ClassReader.EXPAND_FRAMES);
            if (!name.equals(node.name)) return null;
            boolean changed = false;
            boolean fastRead = LOADER.equals(name) && !Boolean.getBoolean("pclmoon.startup.forgeFastRead.disabled")
                && verifiedReadBody(bytes);
            for (MethodNode method : node.methods) {
                if ((method.access & (Opcodes.ACC_STATIC | Opcodes.ACC_ABSTRACT | Opcodes.ACC_NATIVE)) != 0) continue;
                if (LOADER.equals(name) && method.name.equals("getClassBytes") && method.desc.equals(READ_DESC)) {
                    if (alreadyHooked(method)) continue;
                    if (fastRead) patchDirectRead(method); else patchRead(method);
                    changed = true;
                } else if (READER.equals(name) && method.name.equals("close") && method.desc.equals("()V")) {
                    if (alreadyHooked(method)) continue;
                    InsnList close = new InsnList();
                    close.add(new VarInsnNode(Opcodes.ALOAD, 0));
                    close.add(call("closeReader", "(Ljava/lang/Object;)V"));
                    method.instructions.insert(close);
                    changed = true;
                }
            }
            if (!changed) return null;
            configureModules(module);
            // Preserve original type merges; COMPUTE_FRAMES would load Forge/Mod classes recursively.
            ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
            node.accept(writer);
            byte[] result = writer.toByteArray();
            if (fastRead && FAST_LOGGED.compareAndSet(false, true))
                System.err.println("[pcl-moon-startup] verified Forge 2.1.10 direct-read path enabled");
            if (LOGGED.compareAndSet(false, true)) {
                try { System.err.println("[pcl-moon-startup] Forge raw-read cache hooks enabled"); }
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

    private static boolean verifiedReadBody(byte[] bytes) {
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
        method.instructions.add(call("read", READ_DESC));
        method.instructions.add(new InsnNode(Opcodes.ARETURN));
        method.maxLocals = 4;
        method.maxStack = 3;
    }

    private static void patchRead(MethodNode method) {
        int slot = method.maxLocals++;
        for (AbstractInsnNode instruction : method.instructions.toArray()) {
            if (instruction instanceof FrameNode frame) {
                extendLocals(frame.local, slot);
            } else if (instruction.getOpcode() == Opcodes.ARETURN) {
                InsnList observe = new InsnList();
                observe.add(new InsnNode(Opcodes.DUP));
                observe.add(new VarInsnNode(Opcodes.ALOAD, slot));
                observe.add(new VarInsnNode(Opcodes.ALOAD, 3));
                observe.add(call("record", "([B" + TICKET_DESC + "Ljava/lang/String;)V"));
                method.instructions.insertBefore(instruction, observe);
            }
        }
        LabelNode original = new LabelNode();
        InsnList enter = new InsnList();
        enter.add(new VarInsnNode(Opcodes.ALOAD, 1));
        enter.add(new VarInsnNode(Opcodes.ALOAD, 2));
        enter.add(new VarInsnNode(Opcodes.ALOAD, 3));
        enter.add(call("begin", "(Ljava/lang/module/ModuleReader;Ljava/lang/module/ModuleReference;Ljava/lang/String;)" + TICKET_DESC));
        enter.add(new VarInsnNode(Opcodes.ASTORE, slot));
        enter.add(new VarInsnNode(Opcodes.ALOAD, slot));
        enter.add(new VarInsnNode(Opcodes.ALOAD, 3));
        enter.add(call("cached", "(" + TICKET_DESC + "Ljava/lang/String;)[B"));
        enter.add(new InsnNode(Opcodes.DUP));
        enter.add(new JumpInsnNode(Opcodes.IFNULL, original));
        enter.add(new InsnNode(Opcodes.ARETURN));
        enter.add(original);
        List<Object> locals = new ArrayList<>(List.of(LOADER, "java/lang/module/ModuleReader",
                "java/lang/module/ModuleReference", "java/lang/String"));
        extendLocals(locals, slot);
        enter.add(new FrameNode(Opcodes.F_NEW, locals.size(), locals.toArray(), 1, new Object[] { "[B" }));
        enter.add(new InsnNode(Opcodes.POP));
        method.instructions.insert(enter);
    }

    private static void extendLocals(List<Object> locals, int slot) {
        int used = 0;
        for (Object local : locals) {
            used += (Opcodes.LONG.equals(local) || Opcodes.DOUBLE.equals(local)) ? 2 : 1;
        }
        if (used > slot) throw new IllegalArgumentException("Unexpected local frame");
        while (used++ < slot) locals.add(Opcodes.TOP);
        locals.add(TICKET);
    }

    private void configureModules(Module target) {
        if (target != null && target != helperModule) {
            if (!target.canRead(helperModule)) {
                instrumentation.redefineModule(target, Set.of(helperModule), Map.of(), Map.of(), Set.of(), Map.of());
            }
            if (helperModule.isNamed() && !helperModule.isExported("pclmoon.startup", target)) {
                instrumentation.redefineModule(helperModule, Set.of(),
                        Map.of("pclmoon.startup", Set.of(target)), Map.of(), Set.of(), Map.of());
            }
            openUnion(target);
        }
        // The union package can also reside in a separate, already-loaded module.
        for (Class<?> loaded : instrumentation.getAllLoadedClasses()) {
            if (loaded.getName().equals("cpw.mods.niofs.union.UnionFileSystem")) {
                openUnion(loaded.getModule());
                break;
            }
        }
    }

    private void openUnion(Module union) {
        if (!union.getPackages().contains(UNION_PACKAGE)) return;
        if (!helperModule.canRead(union)) {
            instrumentation.redefineModule(helperModule, Set.of(union), Map.of(), Map.of(), Set.of(), Map.of());
        }
        if (!union.isOpen(UNION_PACKAGE, helperModule)) {
            instrumentation.redefineModule(union, Set.of(), Map.of(),
                    Map.of(UNION_PACKAGE, Set.of(helperModule)), Set.of(), Map.of());
        }
    }
}
