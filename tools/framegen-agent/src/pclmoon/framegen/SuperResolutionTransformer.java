package pclmoon.framegen;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;
import org.objectweb.asm.*;

/** Deliberately narrow 1.21.11 intermediary adapter; never changes window/input size. */
public final class SuperResolutionTransformer implements ClassFileTransformer {
    private static final String CLIENT = "net/minecraft/class_310", WINDOW = "net/minecraft/class_1041";
    private static final String WORLD = "net/minecraft/class_757", FB = "net/minecraft/class_276";
    private static final String DESC = "(Lnet/minecraft/class_9779;)V";
    private static final String HOOK = "pclmoon/framegen/bridge/NativeUpscaleHooks";
    public byte[] transform(ClassLoader loader, String name, Class<?> redefining, ProtectionDomain domain, byte[] bytes) {
        if (redefining != null || bytes == null || !(CLIENT.equals(name) || WINDOW.equals(name) || WORLD.equals(name))) return null;
        try {
            ClassReader reader = new ClassReader(bytes);
            if (!name.equals(reader.getClassName())) return null;
            final int[] found = new int[4];
            reader.accept(new ClassVisitor(Opcodes.ASM9) {
                public MethodVisitor visitMethod(int access, String method, String desc, String sig, String[] exceptions) {
                    if (method.startsWith("pclmoon$sr")) found[3]++;
                    int kind = kind(name, access, method, desc);
                    if (kind > 0) found[kind - 1]++;
                    return new MethodVisitor(Opcodes.ASM9) {
                        StringBuilder body = new StringBuilder(); int worldCalls;
                        public void visitVarInsn(int op, int var) { body.append("v").append(op).append(':').append(var).append(';'); }
                        public void visitFieldInsn(int op, String owner, String field, String d) { body.append("f").append(op).append(':').append(owner).append('.').append(field).append(d).append(';'); }
                        public void visitInsn(int op) { body.append("i").append(op).append(';'); }
                        public void visitMethodInsn(int op, String owner, String m, String d, boolean itf) {
                            body.append("call;");
                            if (HOOK.equals(owner)) found[3]++;
                            if (op == Opcodes.INVOKEVIRTUAL && "net/minecraft/class_761".equals(owner) && "method_22710".equals(m)
                                && "(Lnet/minecraft/class_9922;Lnet/minecraft/class_9779;ZLnet/minecraft/class_4184;Lorg/joml/Matrix4f;Lorg/joml/Matrix4f;Lorg/joml/Matrix4f;Lcom/mojang/blaze3d/buffers/GpuBufferSlice;Lorg/joml/Vector4f;Z)V".equals(d)) worldCalls++;
                        }
                        public void visitJumpInsn(int op, Label l) { body.append("jump;"); }
                        public void visitIntInsn(int op, int operand) { body.append("int;"); }
                        public void visitLdcInsn(Object value) { body.append("ldc;"); }
                        public void visitInvokeDynamicInsn(String n, String d, Handle h, Object... args) { body.append("dynamic;"); }
                        public void visitIincInsn(int var, int amount) { body.append("increment;"); }
                        public void visitTableSwitchInsn(int min, int max, Label def, Label... labels) { body.append("switch;"); }
                        public void visitLookupSwitchInsn(Label def, int[] keys, Label[] labels) { body.append("switch;"); }
                        public void visitMultiANewArrayInsn(String d, int dimensions) { body.append("array;"); }
                        public void visitTryCatchBlock(Label start, Label end, Label handler, String t) { body.append("handler;"); }
                        public void visitTypeInsn(int op, String t) { body.append("type;"); }
                        public void visitEnd() {
                            if (kind == 0) return;
                            if (WORLD.equals(name)) { if (worldCalls != 1) found[0] = -100; }
                            else {
                                String field = CLIENT.equals(name) ? "field_1689L" + FB + ";" : kind == 1 ? "field_5181I" : "field_5196I";
                                String expected = "v25:0;f180:" + name + "." + field + ";i" + (CLIENT.equals(name) ? 176 : 172) + ";";
                                if (!expected.equals(body.toString())) found[kind - 1] = -100;
                            }
                        }
                    };
                }
            }, ClassReader.SKIP_DEBUG | ClassReader.SKIP_FRAMES);
            if (found[3] != 0) return null;
            if (found[0] != 1 || (WINDOW.equals(name) && found[1] != 1)) return null;
            ClassWriter writer = new ClassWriter(reader, ClassWriter.COMPUTE_MAXS);
            reader.accept(new ClassVisitor(Opcodes.ASM9, writer) {
                public MethodVisitor visitMethod(int access, String method, String desc, String sig, String[] exceptions) {
                    int kind = kind(name, access, method, desc);
                    if (kind == 0) return super.visitMethod(access, method, desc, sig, exceptions);
                    if (WORLD.equals(name)) {
                        MethodVisitor wrapper = super.visitMethod(access, method, desc, sig, exceptions);
                        wrapper.visitCode(); wrapper.visitVarInsn(Opcodes.ALOAD, 0); wrapper.visitVarInsn(Opcodes.ALOAD, 1);
                        wrapper.visitLdcInsn(Type.getObjectType(WORLD));
                        wrapper.visitMethodInsn(Opcodes.INVOKESTATIC, HOOK, "renderWorld", "(Ljava/lang/Object;Ljava/lang/Object;Ljava/lang/Class;)V", false);
                        wrapper.visitInsn(Opcodes.RETURN); wrapper.visitMaxs(3, 2); wrapper.visitEnd();
                        return super.visitMethod(access, "pclmoon$srRenderWorld", desc, sig, exceptions);
                    }
                    return new MethodVisitor(Opcodes.ASM9, super.visitMethod(access, method, desc, sig, exceptions)) {
                        public void visitInsn(int op) {
                            if (op == Opcodes.ARETURN && CLIENT.equals(name)) {
                                super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOK, "selectFramebuffer", "(Ljava/lang/Object;)Ljava/lang/Object;", false);
                                super.visitTypeInsn(Opcodes.CHECKCAST, FB);
                            } else if (op == Opcodes.IRETURN && WINDOW.equals(name)) {
                                super.visitVarInsn(Opcodes.ALOAD, 0);
                                super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOK, kind == 1 ? "framebufferWidth" : "framebufferHeight", "(ILjava/lang/Object;)I", false);
                            }
                            super.visitInsn(op);
                        }
                    };
                }
            }, 0);
            int bit = CLIENT.equals(name) ? 1 : WINDOW.equals(name) ? 2 : 4;
            byte[] transformed = writer.toByteArray();
            Class.forName(HOOK.replace('/', '.')).getMethod("installed", ClassLoader.class, int.class).invoke(null, loader, bit);
            return transformed;
        } catch (Throwable unsupported) {
            System.err.println("[PCL SuperResolution] adapter unavailable: " + name + " (" + unsupported.getClass().getSimpleName() + ")");
            return null;
        }
    }
    private static int kind(String owner, int access, String method, String desc) {
        if ((access & (Opcodes.ACC_STATIC | Opcodes.ACC_NATIVE | Opcodes.ACC_ABSTRACT)) != 0 || (access & Opcodes.ACC_PUBLIC) == 0) return 0;
        if (CLIENT.equals(owner)) return "method_1522".equals(method) && ("()L" + FB + ";").equals(desc) ? 1 : 0;
        if (WINDOW.equals(owner) && "()I".equals(desc)) return "method_4489".equals(method) ? 1 : "method_4506".equals(method) ? 2 : 0;
        return WORLD.equals(owner) && "method_3188".equals(method) && DESC.equals(desc) ? 1 : 0;
    }
}
