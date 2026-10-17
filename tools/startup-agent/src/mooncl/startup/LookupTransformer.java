package mooncl.startup;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;
import jdk.internal.org.objectweb.asm.ClassReader;
import jdk.internal.org.objectweb.asm.ClassVisitor;
import jdk.internal.org.objectweb.asm.ClassWriter;
import jdk.internal.org.objectweb.asm.MethodVisitor;
import jdk.internal.org.objectweb.asm.Opcodes;

final class LookupTransformer implements ClassFileTransformer {
    int lookups;
    int closeHooks;

    @Override public byte[] transform(ClassLoader loader, String name, Class<?> type,
            ProtectionDomain domain, byte[] bytes) {
        boolean jarLoader = "jdk/internal/loader/URLClassPath$JarLoader".equals(name);
        boolean zipFile = "java/util/zip/ZipFile".equals(name);
        if (!jarLoader && !zipFile) return null;
        ClassReader reader = new ClassReader(bytes);
        ClassWriter writer = new ClassWriter(0);
        reader.accept(new ClassVisitor(Opcodes.ASM8, writer) {
            @Override public MethodVisitor visitMethod(int access, String method, String descriptor,
                    String signature, String[] exceptions) {
                MethodVisitor parent = super.visitMethod(access, method, descriptor, signature, exceptions);
                if (jarLoader && method.equals("getResource") && descriptor.equals("(Ljava/lang/String;Z)Ljdk/internal/loader/Resource;")) {
                    return new MethodVisitor(Opcodes.ASM8, parent) {
                        @Override public void visitMethodInsn(int opcode, String owner, String name, String desc, boolean itf) {
                            if (opcode == Opcodes.INVOKEVIRTUAL && owner.equals("java/util/jar/JarFile") &&
                                    name.equals("getJarEntry") && desc.equals("(Ljava/lang/String;)Ljava/util/jar/JarEntry;")) {
                                lookups++;
                                super.visitMethodInsn(Opcodes.INVOKESTATIC, "mooncl/startup/NegativeCache", "getJarEntry",
                                    "(Ljava/util/jar/JarFile;Ljava/lang/String;)Ljava/util/jar/JarEntry;", false);
                            } else super.visitMethodInsn(opcode, owner, name, desc, itf);
                        }
                    };
                }
                if (zipFile && method.equals("close") && descriptor.equals("()V")) {
                    closeHooks++;
                    return new MethodVisitor(Opcodes.ASM8, parent) {
                        @Override public void visitCode() {
                            super.visitCode();
                            super.visitVarInsn(Opcodes.ALOAD, 0);
                            super.visitMethodInsn(Opcodes.INVOKESTATIC, "mooncl/startup/NegativeCache", "forget", "(Ljava/lang/Object;)V", false);
                        }
                    };
                }
                return parent;
            }
        }, 0);
        return writer.toByteArray();
    }
}
