package pclmoon.startup;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;
import jdk.internal.org.objectweb.asm.ClassReader;
import jdk.internal.org.objectweb.asm.ClassWriter;
import jdk.internal.org.objectweb.asm.Opcodes;
import jdk.internal.org.objectweb.asm.tree.AbstractInsnNode;
import jdk.internal.org.objectweb.asm.tree.ClassNode;
import jdk.internal.org.objectweb.asm.tree.InsnList;
import jdk.internal.org.objectweb.asm.tree.InsnNode;
import jdk.internal.org.objectweb.asm.tree.JumpInsnNode;
import jdk.internal.org.objectweb.asm.tree.LabelNode;
import jdk.internal.org.objectweb.asm.tree.MethodInsnNode;
import jdk.internal.org.objectweb.asm.tree.MethodNode;
import jdk.internal.org.objectweb.asm.tree.VarInsnNode;

/** Changes method bodies only. Class definition and loader/Mixin order stay untouched. */
final class ClassPathTransformer implements ClassFileTransformer {
    private static final String HELPER = "pclmoon/startup/NegativeCache";
    private static final String LOOKUP = "L" + HELPER + "$ClassLookup;";
    int searches, mutations, manifests;

    private static MethodInsnNode call(String name, String descriptor) {
        return new MethodInsnNode(Opcodes.INVOKESTATIC, HELPER, name, descriptor, false);
    }

    @Override public byte[] transform(ClassLoader loader, String name, Class<?> type,
            ProtectionDomain domain, byte[] bytes) {
        boolean classpath = "jdk/internal/loader/URLClassPath".equals(name);
        boolean jar = "jdk/internal/loader/URLClassPath$JarLoader".equals(name);
        if (!classpath && !jar) return null;
        ClassNode node = new ClassNode(Opcodes.ASM8);
        new ClassReader(bytes).accept(node, 0);
        for (MethodNode method : node.methods) {
            if (classpath && ((method.name.equals("addURL") && method.desc.equals("(Ljava/net/URL;)V")) ||
                    (method.name.equals("closeLoaders") && method.desc.equals("()Ljava/util/List;")))) {
                method.instructions.insert(call("invalidateClasspaths", "()V"));
                for (AbstractInsnNode instruction : method.instructions.toArray()) {
                    int opcode = instruction.getOpcode();
                    if (opcode == Opcodes.RETURN || opcode == Opcodes.ARETURN || opcode == Opcodes.ATHROW)
                        method.instructions.insertBefore(instruction, call("invalidateClasspaths", "()V"));
                }
                mutations++;
            }
            if (jar && method.name.equals("getClassPath") && method.desc.equals("()[Ljava/net/URL;")) {
                for (AbstractInsnNode instruction : method.instructions.toArray()) {
                    if (instruction.getOpcode() == Opcodes.ARETURN) {
                        InsnList observe = new InsnList();
                        observe.add(new InsnNode(Opcodes.DUP));
                        observe.add(call("manifestClassPath", "([Ljava/net/URL;)V"));
                        method.instructions.insertBefore(instruction, observe);
                    }
                }
                manifests++;
            }
            if (classpath && ((method.name.equals("findResource") && method.desc.equals("(Ljava/lang/String;Z)Ljava/net/URL;")) ||
                    (method.name.equals("getResource") && method.desc.equals("(Ljava/lang/String;Z)Ljdk/internal/loader/Resource;")))) {
                int slot = method.maxLocals++;
                // Capture the generation BEFORE the original lookup. A concurrent append or
                // manifest expansion makes that ticket unusable even if the old lookup returns null.
                for (AbstractInsnNode instruction : method.instructions.toArray()) {
                    if (instruction.getOpcode() == Opcodes.ARETURN) {
                        InsnList observe = new InsnList();
                        observe.add(new InsnNode(Opcodes.DUP));
                        observe.add(new VarInsnNode(Opcodes.ALOAD, slot));
                        observe.add(new VarInsnNode(Opcodes.ALOAD, 1));
                        observe.add(call("observeClassLookup", "(Ljava/lang/Object;" + LOOKUP + "Ljava/lang/String;)V"));
                        method.instructions.insertBefore(instruction, observe);
                    }
                }
                LabelNode original = new LabelNode();
                InsnList enter = new InsnList();
                enter.add(new VarInsnNode(Opcodes.ALOAD, 0));
                enter.add(new VarInsnNode(Opcodes.ALOAD, 1));
                enter.add(call("beginClassLookup", "(Ljava/lang/Object;Ljava/lang/String;)" + LOOKUP));
                enter.add(new VarInsnNode(Opcodes.ASTORE, slot));
                enter.add(new VarInsnNode(Opcodes.ALOAD, slot));
                enter.add(new VarInsnNode(Opcodes.ALOAD, 1));
                enter.add(call("isClassMissing", "(" + LOOKUP + "Ljava/lang/String;)Z"));
                enter.add(new JumpInsnNode(Opcodes.IFEQ, original));
                enter.add(new InsnNode(Opcodes.ACONST_NULL));
                enter.add(new InsnNode(Opcodes.ARETURN));
                enter.add(original);
                method.instructions.insert(enter);
                searches++;
            }
        }
        ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_FRAMES | ClassWriter.COMPUTE_MAXS);
        node.accept(writer);
        return writer.toByteArray();
    }
}
