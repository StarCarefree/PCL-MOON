package pclmoon.framegen;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassVisitor;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.MethodVisitor;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.Type;

/** No Minecraft names, mappings, mixins, retransformation or JDK patches. */
public final class GlfwTransformer implements ClassFileTransformer {
    private static final String GLFW = "org/lwjgl/glfw/GLFW";
    private static final String HOOK = "pclmoon/framegen/bridge/SwapHook";
    public byte[] transform(ClassLoader loader, String name, Class<?> redefining,
                            ProtectionDomain domain, byte[] bytes) {
        if (!GLFW.equals(name) || redefining != null) return null;
        try {
            ClassReader reader = new ClassReader(bytes);
            final int[] found = new int[3];
            reader.accept(new ClassVisitor(Opcodes.ASM9) {
                public MethodVisitor visitMethod(int access, String method, String desc, String sig, String[] exceptions) {
                    if (method.startsWith("pclmoon$")) found[2]++;
                    if ((access & (Opcodes.ACC_PUBLIC | Opcodes.ACC_STATIC | Opcodes.ACC_NATIVE | Opcodes.ACC_ABSTRACT))
                            == (Opcodes.ACC_PUBLIC | Opcodes.ACC_STATIC) && "(J)V".equals(desc)) {
                        if (method.equals("glfwSwapBuffers")) found[0]++;
                        if (method.equals("glfwDestroyWindow")) found[1]++;
                    }
                    return null;
                }
            }, ClassReader.SKIP_CODE | ClassReader.SKIP_DEBUG | ClassReader.SKIP_FRAMES);
            if (found[0] != 1 || found[1] != 1 || found[2] != 0) {
                System.err.println("[PCL FrameGen] unrecognised GLFW layout; original presentation retained");
                return null;
            }
            ClassWriter writer = new ClassWriter(reader, 0);
            reader.accept(new ClassVisitor(Opcodes.ASM9, writer) {
                public MethodVisitor visitMethod(int access, String method, String desc, String sig, String[] exceptions) {
                    boolean swap = method.equals("glfwSwapBuffers") && desc.equals("(J)V");
                    boolean destroy = method.equals("glfwDestroyWindow") && desc.equals("(J)V");
                    if (!swap && !destroy) return super.visitMethod(access, method, desc, sig, exceptions);
                    MethodVisitor wrapper = super.visitMethod(access, method, desc, sig, exceptions);
                    wrapper.visitCode();
                    wrapper.visitVarInsn(Opcodes.LLOAD, 0);
                    wrapper.visitLdcInsn(Type.getObjectType(GLFW));
                    wrapper.visitMethodInsn(Opcodes.INVOKESTATIC, HOOK, swap ? "swap" : "destroy", "(JLjava/lang/Class;)V", false);
                    wrapper.visitInsn(Opcodes.RETURN);
                    wrapper.visitMaxs(3, 2);
                    wrapper.visitEnd();
                    return super.visitMethod(access, "pclmoon$" + method, desc, sig, exceptions);
                }
            }, 0);
            return writer.toByteArray();
        } catch (Throwable unsupported) {
            System.err.println("[PCL FrameGen] GLFW hook unavailable (" + unsupported.getClass().getSimpleName() + ")");
            return null;
        }
    }
}
