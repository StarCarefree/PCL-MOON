package pclmoon.framegen.test;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import org.objectweb.asm.*;
import pclmoon.framegen.NativeGameTransformer;
import pclmoon.framegen.bridge.FrameMetrics;
import pclmoon.framegen.bridge.FrameSettings;
import pclmoon.framegen.bridge.NativeGameHooks;
import pclmoon.framegen.bridge.PacingSettings;

/** Checks real transformer structure and executes only synthetic classes, never user Minecraft. */
public final class NativeGameHarness {
    public static void main(String[] args) throws Exception {
        NativeGameTransformer transformer = new NativeGameTransformer();
        ClassLoader loader = NativeGameHarness.class.getClassLoader();
        byte[] minecraft = read("net/minecraft/class_310.class");
        byte[] render = read("com/mojang/blaze3d/systems/RenderSystem.class");
        byte[] unknown = read("pclmoon/framegen/test/NativeGameHarness.class");
        check(transformer.transform(loader, "java/lang/Thread", null, null, unknown) == null, "JDK untouched");
        check(transformer.transform(loader, "net/minecraft/class_310", String.class, null, minecraft) == null, "redefine rejected");
        check(transformer.transform(loader, "net/minecraft/class_9999", null, null, unknown) == null, "unknown name untouched");
        check(transformer.transform(loader, "net/minecraft/class_310", null, null, unknown) == null, "wrong bytes rejected");
        byte[] patchedRun = transformer.transform(loader, "net/minecraft/class_310", null, null, minecraft);
        byte[] patchedLimit = transformer.transform(loader, "com/mojang/blaze3d/systems/RenderSystem", null, null, render);
        check(patchedRun != null && patchedLimit != null, "confirmed layout patched");
        check(transformer.transform(loader, "net/minecraft/class_310", null, null, patchedRun) == null, "no duplicate frame hook");
        check(transformer.transform(loader, "com/mojang/blaze3d/systems/RenderSystem", null, null, patchedLimit) == null, "no duplicate limiter hook");
        final int[] hooks = new int[3];
        new ClassReader(patchedRun).accept(new ClassVisitor(Opcodes.ASM9) {
            public MethodVisitor visitMethod(int access, final String name, String desc, String sig, String[] exceptions) {
                return new MethodVisitor(Opcodes.ASM9) {
                    public void visitMethodInsn(int op, String owner, String method, String desc, boolean iface) {
                        if (!owner.equals("pclmoon/framegen/bridge/NativeGameHooks")) return;
                        if (!name.equals("method_1523")) hooks[2]++;
                        else if (method.equals("frameStart")) hooks[0]++;
                        else if (method.equals("frameEnd")) hooks[1]++;
                    }
                };
            }
        }, 0);
        check(hooks[0] == 1 && hooks[1] == 1 && hooks[2] == 0, "only the per-frame method is sampled, not outer run/tick");
        ProbeLoader isolated = new ProbeLoader(loader);
        Class<?> game = isolated.define("net.minecraft.class_310", patchedRun);
        Class<?> system = isolated.define("com.mojang.blaze3d.systems.RenderSystem", patchedLimit);
        Object instance = game.getConstructor().newInstance();
        game.getMethod("method_1514").invoke(instance);
        check(game.getField("renders").getInt(instance) == 2 && game.getField("ticks").getInt(instance) == 1, "game render/tick bodies unchanged");
        check(((Number) game.getMethod("method_1574").invoke(instance)).intValue() == 2, "tick return preserved");
        FrameSettings.setGeneration(true); FrameSettings.setNativeFrameControl(true);
        FrameSettings.setOutputFps(240); FrameSettings.setMultiplier(2);
        NativeGameHooks.setPresentationActive(true);
        system.getMethod("limitDisplayFPS", int.class).invoke(null, 30);
        check(system.getField("originalCalls").getInt(null) == 0, "generation owns pacing, no second native wait");
        FrameSettings.setGeneration(false);
        system.getMethod("limitDisplayFPS", int.class).invoke(null, 999);
        system.getMethod("limitDisplayFPS", int.class).invoke(null, 999);
        check(system.getField("lastFps").getInt(null) == -1, "native hybrid pacing does not invoke old wait");
        PacingSettings.setFpsLimit(60); NativeGameHooks.setWindowActive(true);
        system.getMethod("limitDisplayFPS", int.class).invoke(null, 1);
        check(system.getField("originalCalls").getInt(null) == 0, "standalone cap owns presentation timing");
        PacingSettings.setFpsLimit(0); NativeGameHooks.setWindowActive(false);
        system.getMethod("pclmoon$limitDisplayFPS", int.class).invoke(null, 30);
        check(system.getField("lastFps").getInt(null) == 30, "original method preserved intact");
        system.getMethod("limitDisplayFPS", int.class).invoke(null, 1000);
        check(system.getField("lastFps").getInt(null) == 1000, "out-of-range native cap keeps original behavior");
        int calls = system.getField("originalCalls").getInt(null);
        system.getField("failLimit").setBoolean(null, true);
        try { system.getMethod("limitDisplayFPS", int.class).invoke(null, 1000); throw new AssertionError("limiter exception swallowed"); }
        catch (java.lang.reflect.InvocationTargetException expected) {
            check(expected.getCause() instanceof IllegalArgumentException, "original limiter exception preserved");
        }
        check(system.getField("originalCalls").getInt(null) == calls + 1, "failed original limiter never retried");
        system.getField("failLimit").setBoolean(null, false);
        system.getMethod("flipFrame").invoke(null);
        check(system.getField("waits").getInt(null) == 1, "unrelated methods unchanged");
        game.getField("threw").setBoolean(instance, true);
        try { game.getMethod("method_1514").invoke(instance); throw new AssertionError("exception swallowed"); }
        catch (java.lang.reflect.InvocationTargetException expected) { check(expected.getCause() instanceof IllegalStateException, "original exception preserved"); }
        check(FrameMetrics.snapshot().nativeHook.contains("限帧已接入") && FrameMetrics.snapshot().nativeHook.contains("单帧采样已接入"), "partial adapters reported accurately");
        NativeGameHooks.setPresentationActive(false);
        System.out.println("PASS native game transformer: per-frame location/limit/restore/tick/exception/idempotence");
    }
    private static byte[] read(String name) throws Exception {
        try (InputStream input = NativeGameHarness.class.getClassLoader().getResourceAsStream(name)) {
            if (input == null) throw new AssertionError("missing fixture: " + name);
            ByteArrayOutputStream output = new ByteArrayOutputStream(); byte[] buffer = new byte[8192];
            for (int count; (count = input.read(buffer)) != -1;) output.write(buffer, 0, count);
            return output.toByteArray();
        }
    }
    private static final class ProbeLoader extends ClassLoader {
        ProbeLoader(ClassLoader parent) { super(parent); }
        Class<?> define(String name, byte[] bytes) { return defineClass(name, bytes, 0, bytes.length); }
    }
    private static void check(boolean ok, String message) { if (!ok) throw new AssertionError(message); }
}
