package pclmoon.startup;

import java.lang.instrument.ClassFileTransformer;
import java.lang.instrument.Instrumentation;
import java.security.ProtectionDomain;
import java.util.Map;
import java.util.Set;
import jdk.internal.org.objectweb.asm.ClassReader;
import jdk.internal.org.objectweb.asm.ClassWriter;
import jdk.internal.org.objectweb.asm.Opcodes;
import jdk.internal.org.objectweb.asm.tree.ClassNode;
import jdk.internal.org.objectweb.asm.tree.InsnList;
import jdk.internal.org.objectweb.asm.tree.MethodInsnNode;
import jdk.internal.org.objectweb.asm.tree.MethodNode;
import jdk.internal.org.objectweb.asm.tree.VarInsnNode;

/** 仅挂接呈现与上下文清理边界，不修改 Minecraft 映射或游戏逻辑。 */
public final class MmcssTransformer implements ClassFileTransformer {
    private static final String HELPER = "pclmoon/startup/Mmcss";
    private final Instrumentation instrumentation;
    private final Module helperModule;

    public MmcssTransformer(Instrumentation instrumentation, Module helperModule) {
        this.instrumentation = instrumentation;
        this.helperModule = helperModule;
    }

    @Override public byte[] transform(Module module, ClassLoader loader, String name, Class<?> type,
            ProtectionDomain domain, byte[] bytes) {
        boolean glfw = "org/lwjgl/glfw/GLFW".equals(name);
        boolean display = "org/lwjgl/opengl/Display".equals(name);
        if (!glfw && !display) return null;
        try {
            ClassNode node = new ClassNode(Opcodes.ASM8);
            new ClassReader(bytes).accept(node, 0);
            boolean changed = false;
            for (MethodNode method : node.methods) {
                if ((method.access & Opcodes.ACC_STATIC) == 0
                        || (method.access & (Opcodes.ACC_NATIVE | Opcodes.ACC_ABSTRACT)) != 0) continue;
                boolean detach = glfw && method.name.equals("glfwMakeContextCurrent") && method.desc.equals("(J)V");
                boolean release = display && method.name.equals("releaseContext") && method.desc.equals("()V");
                String hook = null;
                if (glfw && method.name.equals("glfwSwapBuffers") && method.desc.equals("(J)V")
                        || display && method.name.equals("swapBuffers") && method.desc.equals("()V")) hook = "enter";
                if (glfw && (method.name.equals("glfwTerminate") && method.desc.equals("()V")
                        || method.name.equals("glfwDestroyWindow") && method.desc.equals("(J)V"))
                        || display && method.name.equals("destroy") && method.desc.equals("()V")) hook = "leave";
                if (hook == null && !detach && !release) continue;
                boolean hooked = false;
                for (var instruction : method.instructions)
                    if (instruction instanceof MethodInsnNode call && call.owner.equals(HELPER)) hooked = true;
                if (hooked) continue;
                if (detach || release) {
                    // 在实际解绑成功后，仍由拥有上下文的线程注销；异常返回不改变注册。
                    for (var instruction : method.instructions.toArray()) {
                        if (instruction.getOpcode() != Opcodes.RETURN) continue;
                        InsnList cleanup = new InsnList();
                        if (detach) cleanup.add(new VarInsnNode(Opcodes.LLOAD, 0));
                        cleanup.add(new MethodInsnNode(Opcodes.INVOKESTATIC, HELPER,
                            detach ? "contextChanged" : "leave", detach ? "(J)V" : "()V", false));
                        method.instructions.insertBefore(instruction, cleanup);
                    }
                } else {
                    method.instructions.insert(new MethodInsnNode(Opcodes.INVOKESTATIC, HELPER, hook, "()V", false));
                }
                changed = true;
            }
            if (!changed) return null;
            if (module != null && !module.canRead(helperModule))
                instrumentation.redefineModule(module, Set.of(helperModule), Map.of(), Map.of(), Set.of(), Map.of());
            // 不新增分支或局部变量，仅为解绑钩子的 long 参数重算最大栈深度。
            ClassWriter writer = new ClassWriter(ClassWriter.COMPUTE_MAXS);
            node.accept(writer);
            return writer.toByteArray();
        } catch (Throwable ignored) { return null; }
    }
}
