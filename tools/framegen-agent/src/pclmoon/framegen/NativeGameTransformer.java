package pclmoon.framegen;

import java.lang.instrument.ClassFileTransformer;
import java.security.ProtectionDomain;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassVisitor;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.MethodVisitor;
import org.objectweb.asm.Opcodes;

/**
 * 1.21.11 Fabric 0.18.4 intermediary only. Fail closed on any other layout.
 * Does not walk unrelated classes, patch the JDK, or change tick/render logic.
 */
public final class NativeGameTransformer implements ClassFileTransformer {
    static final String MINECRAFT = "net/minecraft/class_310";
    static final String RENDER_SYSTEM = "com/mojang/blaze3d/systems/RenderSystem";
    static final String HOOK = "pclmoon/framegen/bridge/NativeGameHooks";
    static final String RUN = "method_1514";
    static final String RUN_DESC = "()V";
    static final String FRAME = "method_1523";
    static final String FRAME_DESC = "(Z)V";
    static final String LIMIT = "limitDisplayFPS";
    static final String LIMIT_DESC = "(I)V";
    private static final String INSTALLED = "1.21.11-intermediary";
    private static volatile boolean frameAdapter, limiterAdapter;

    public byte[] transform(ClassLoader loader, String name, Class<?> redefining,
                            ProtectionDomain domain, byte[] bytes) {
        if (redefining != null || bytes == null) return null;
        if (MINECRAFT.equals(name)) {
            byte[] display = FpsDisplayTransformer.transform(bytes);
            byte[] frame = transformMinecraft(display == null ? bytes : display);
            return frame == null ? display : frame;
        }
        if (RENDER_SYSTEM.equals(name)) return transformRenderSystem(bytes);
        return null;
    }

    private static byte[] transformMinecraft(byte[] bytes) {
        try {
            ClassReader reader = new ClassReader(bytes);
            if (!MINECRAFT.equals(reader.getClassName())) return null;
            int[] found = new int[3];
            reader.accept(new ClassVisitor(Opcodes.ASM9) {
                public MethodVisitor visitMethod(int access, String method, String desc, String sig, String[] exceptions) {
                    if (isRun(access, method, desc)) found[0]++;
                    else if (isFrame(access, method, desc)) found[1]++;
                    if (!HOOK.equals(reader.getClassName())) {
                        return new MethodVisitor(Opcodes.ASM9) {
                            public void visitMethodInsn(int opcode, String owner, String name, String descriptor, boolean iface) {
                                if (HOOK.equals(owner) && ("frameStart".equals(name) || "frameEnd".equals(name))) found[2]++;
                            }
                        };
                    }
                    return null;
                }
            }, ClassReader.SKIP_DEBUG | ClassReader.SKIP_FRAMES);
            if (found[2] != 0) return null;
            if (found[0] != 1 || found[1] != 1) return reject("minecraft method layout");
            ClassWriter writer = new ClassWriter(reader, ClassWriter.COMPUTE_MAXS);
            final boolean[] runPatched = new boolean[1];
            reader.accept(new ClassVisitor(Opcodes.ASM9, writer) {
                public MethodVisitor visitMethod(int access, String method, String desc, String sig, String[] exceptions) {
                    MethodVisitor visitor = super.visitMethod(access, method, desc, sig, exceptions);
                    if (!isFrame(access, method, desc)) return visitor;
                    return new MethodVisitor(Opcodes.ASM9, visitor) {
                        private boolean entered;
                        private int returns;
                        public void visitCode() {
                            super.visitCode();
                            if (entered) return;
                            entered = true;
                            super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOK, "frameStart", "()V", false);
                        }
                        public void visitInsn(int opcode) {
                            if (opcode == Opcodes.RETURN) {
                                returns++;
                                super.visitMethodInsn(Opcodes.INVOKESTATIC, HOOK, "frameEnd", "()V", false);
                            }
                            super.visitInsn(opcode);
                        }
                        public void visitEnd() {
                            runPatched[0] = entered && returns == 1;
                            super.visitEnd();
                        }
                    };
                }
            }, ClassReader.EXPAND_FRAMES);
            if (!runPatched[0]) return reject("minecraft run shape");
            report(true);
            return writer.toByteArray();
        } catch (Throwable unsupported) {
            return reject(unsupported.getClass().getSimpleName());
        }
    }

    private static byte[] transformRenderSystem(byte[] bytes) {
        try {
            ClassReader reader = new ClassReader(bytes);
            if (!RENDER_SYSTEM.equals(reader.getClassName())) return null;
            int[] found = new int[2];
            reader.accept(new ClassVisitor(Opcodes.ASM9) {
                public MethodVisitor visitMethod(int access, String method, String desc, String sig, String[] exceptions) {
                    if (method.equals("pclmoon$" + LIMIT) && desc.equals(LIMIT_DESC)) found[1]++;
                    if (!isLimit(access, method, desc)) {
                        return new MethodVisitor(Opcodes.ASM9) {
                            public void visitMethodInsn(int opcode, String owner, String name, String descriptor, boolean iface) {
                                if (HOOK.equals(owner) && ("limitFps".equals(name) || "limitNative".equals(name))) found[1]++;
                            }
                        };
                    }
                    found[0]++;
                    return new MethodVisitor(Opcodes.ASM9) {
                        private int invokes;
                        private boolean shaped;
                        public void visitMethodInsn(int opcode, String owner, String name, String descriptor, boolean iface) {
                            if (opcode == Opcodes.INVOKESTATIC && "org/lwjgl/glfw/GLFW".equals(owner)
                                    && "glfwWaitEventsTimeout".equals(name) && "(D)V".equals(descriptor)) invokes++;
                            if (HOOK.equals(owner) && ("limitFps".equals(name) || "limitNative".equals(name))) found[1]++;
                            super.visitMethodInsn(opcode, owner, name, descriptor, iface);
                        }
                        public void visitVarInsn(int opcode, int var) {
                            if (opcode == Opcodes.ILOAD && var == 0) shaped = true;
                            super.visitVarInsn(opcode, var);
                        }
                        public void visitEnd() {
                            if (invokes != 1 || !shaped) found[0] = -1;
                            super.visitEnd();
                        }
                    };
                }
            }, ClassReader.SKIP_DEBUG | ClassReader.SKIP_FRAMES);
            if (found[1] != 0) return null;
            if (found[0] != 1) return reject("rendersystem limiter layout");
            ClassWriter writer = new ClassWriter(reader, 0);
            final int[] patched = new int[1];
            reader.accept(new ClassVisitor(Opcodes.ASM9, writer) {
                public MethodVisitor visitMethod(int access, String method, String desc, String sig, String[] exceptions) {
                    if (!isLimit(access, method, desc)) return super.visitMethod(access, method, desc, sig, exceptions);
                    MethodVisitor wrapper = super.visitMethod(access, method, desc, sig, exceptions);
                    wrapper.visitCode();
                    wrapper.visitVarInsn(Opcodes.ILOAD, 0);
                    wrapper.visitLdcInsn(org.objectweb.asm.Type.getObjectType(RENDER_SYSTEM));
                    wrapper.visitMethodInsn(Opcodes.INVOKESTATIC, HOOK, "limitNative", "(ILjava/lang/Class;)V", false);
                    wrapper.visitInsn(Opcodes.RETURN);
                    wrapper.visitMaxs(2, 1);
                    wrapper.visitEnd();
                    patched[0]++;
                    return super.visitMethod(access, "pclmoon$" + LIMIT, desc, sig, exceptions);
                }
            }, 0);
            if (patched[0] != 1) return reject("rendersystem limiter patch");
            report(false);
            return writer.toByteArray();
        } catch (Throwable unsupported) {
            return reject(unsupported.getClass().getSimpleName());
        }
    }

    private static boolean isRun(int access, String method, String desc) {
        return method.equals(RUN) && desc.equals(RUN_DESC)
                && (access & (Opcodes.ACC_STATIC | Opcodes.ACC_NATIVE | Opcodes.ACC_ABSTRACT)) == 0
                && (access & Opcodes.ACC_PUBLIC) != 0;
    }

    private static boolean isFrame(int access, String method, String desc) {
        return method.equals(FRAME) && desc.equals(FRAME_DESC)
                && (access & (Opcodes.ACC_STATIC | Opcodes.ACC_NATIVE | Opcodes.ACC_ABSTRACT | Opcodes.ACC_PUBLIC)) == 0;
    }

    private static boolean isLimit(int access, String method, String desc) {
        return method.equals(LIMIT) && desc.equals(LIMIT_DESC)
                && (access & (Opcodes.ACC_PUBLIC | Opcodes.ACC_STATIC)) == (Opcodes.ACC_PUBLIC | Opcodes.ACC_STATIC)
                && (access & (Opcodes.ACC_NATIVE | Opcodes.ACC_ABSTRACT)) == 0;
    }

    private static synchronized void report(boolean frame) {
        if (frame) frameAdapter = true; else limiterAdapter = true;
        String state = (limiterAdapter ? "原生限帧已接入" : "限帧待命中") + " / "
            + (frameAdapter ? "1.21.11 单帧采样已接入" : "单帧采样未命中");
        try {
            Class<?> hooks = Class.forName(HOOK.replace('/', '.'), false,
                    NativeGameTransformer.class.getClassLoader());
            hooks.getMethod("installed", String.class).invoke(null, state);
        } catch (Throwable ignored) {
            System.err.println("[PCL FrameGen] native game adapter: " + state);
        }
    }

    private static byte[] reject(String reason) {
        System.err.println("[PCL FrameGen] native game adapter unavailable (" + reason + "); original limiter retained");
        return null;
    }
}
