package pclmoon.framegen;

import org.objectweb.asm.*;

/** Display-only adapter for the verified 1.21.11 intermediary FPS getter; never writes game counters. */
final class FpsDisplayTransformer {
    private static final String OWNER = "net/minecraft/class_310";
    private static final String GETTER = "method_47599";
    private static final String HOOK = "pclmoon/framegen/bridge/NativeGameHooks";
    static byte[] transform(byte[] bytes) {
        try {
            ClassReader reader = new ClassReader(bytes);
            if (!OWNER.equals(reader.getClassName())) return null;
            final int[] valid = new int[1];
            reader.accept(new ClassVisitor(Opcodes.ASM9) {
                public MethodVisitor visitMethod(int access, String name, String desc, String signature, String[] exceptions) {
                    if (!getter(access, name, desc)) return null;
                    return new MethodVisitor(Opcodes.ASM9) {
                        int step; boolean bad;
                        public void visitFieldInsn(int opcode, String owner, String name, String desc) {
                            if (step != 0 || opcode != Opcodes.GETSTATIC || !OWNER.equals(owner)
                                    || !"field_1738".equals(name) || !"I".equals(desc)) bad = true;
                            step++;
                        }
                        public void visitInsn(int opcode) { if (step != 1 || opcode != Opcodes.IRETURN) bad = true; step++; }
                        public void visitVarInsn(int opcode, int var) { bad = true; }
                        public void visitIntInsn(int opcode, int operand) { bad = true; }
                        public void visitLdcInsn(Object value) { bad = true; }
                        public void visitJumpInsn(int opcode, Label label) { bad = true; }
                        public void visitTypeInsn(int opcode, String type) { bad = true; }
                        public void visitIincInsn(int var, int increment) { bad = true; }
                        public void visitTableSwitchInsn(int min, int max, Label dflt, Label... labels) { bad = true; }
                        public void visitLookupSwitchInsn(Label dflt, int[] keys, Label[] labels) { bad = true; }
                        public void visitMultiANewArrayInsn(String desc, int dimensions) { bad = true; }
                        public void visitInvokeDynamicInsn(String name, String desc, Handle bootstrap, Object... args) { bad = true; }
                        public void visitMethodInsn(int opcode, String owner, String name, String desc, boolean iface) { bad = true; }
                        public void visitTryCatchBlock(Label start, Label end, Label handler, String type) { bad = true; }
                        public void visitEnd() { if (!bad && step == 2) valid[0]++; }
                    };
                }
            }, ClassReader.SKIP_DEBUG | ClassReader.SKIP_FRAMES);
            if (valid[0] != 1) return null;
            ClassWriter writer = new ClassWriter(reader, ClassWriter.COMPUTE_MAXS);
            reader.accept(new ClassVisitor(Opcodes.ASM9, writer) {
                public MethodVisitor visitMethod(int access, String name, String desc, String sig, String[] exceptions) {
                    MethodVisitor target = super.visitMethod(access, name, desc, sig, exceptions);
                    if (!getter(access, name, desc)) return target;
                    return new MethodVisitor(Opcodes.ASM9, target) {
                        public void visitInsn(int opcode) {
                            if (opcode == Opcodes.IRETURN) super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOK, "displayFps", "(I)I", false);
                            super.visitInsn(opcode);
                        }
                    };
                }
            }, 0);
            return writer.toByteArray();
        } catch (RuntimeException unknownLayout) { return null; }
    }
    private static boolean getter(int access, String name, String desc) {
        return GETTER.equals(name) && "()I".equals(desc)
            && (access & (Opcodes.ACC_PUBLIC | Opcodes.ACC_STATIC | Opcodes.ACC_NATIVE | Opcodes.ACC_ABSTRACT)) == Opcodes.ACC_PUBLIC;
    }
}
