package pclmoon.framegen.bridge;

import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.util.concurrent.ConcurrentHashMap;

/** Validated native adapters; no Minecraft symbolic dependency or persisted game-option writes. */
public final class NativeGameHooks {
    private static volatile boolean presentationActive, windowActive;
    private static volatile long frameStarted;
    private static final ConcurrentHashMap<Class<?>, NativeLimit> nativeLimits = new ConcurrentHashMap<Class<?>, NativeLimit>();
    private NativeGameHooks() { }
    public static void installed(String mode) {
        FrameMetrics.nativeHook(mode);
        System.err.println("[PCL FrameGen] native game adapter: " + mode);
    }
    public static void setPresentationActive(boolean active) { presentationActive = active; }
    public static void setWindowActive(boolean active) { windowActive = active; }
    public static int limitFps(int original) {
        if (!presentationActive || !FrameSettings.generation() || !FrameSettings.nativeFrameControl()) return original;
        int ceiling = FrameSettings.outputFps();
        if (PacingSettings.fpsLimit() > 0) ceiling = Math.min(ceiling, PacingSettings.fpsLimit());
        return Math.max(1, (ceiling + FrameSettings.multiplier() - 1) / FrameSettings.multiplier());
    }
    /** Wrapper target for RenderSystem.limitDisplayFPS; exactly one pacing authority is active. */
    public static void limitNative(int originalFps, Class<?> owner) {
        NativeLimit state = nativeLimits.get(owner);
        if (state == null) {
            try {
                NativeLimit created = new NativeLimit(owner.getMethod("pclmoon$limitDisplayFPS", int.class));
                NativeLimit raced = nativeLimits.putIfAbsent(owner, created);
                state = raced == null ? created : raced;
            } catch (ReflectiveOperationException invalid) { throw new IllegalStateException("Missing original native FPS limiter", invalid); }
        }
        // Final presentation pacing owns both real and generated swaps. Do not also sleep in Minecraft.
        if ((windowActive && PacingSettings.fpsLimit() > 0)
                || (presentationActive && FrameSettings.generation() && FrameSettings.nativeFrameControl())) {
            state.pacer.reset();
            return;
        }
        int fps = limitFps(originalFps);
        if (fps <= 0) { state.pacer.reset(); return; } // Mod's original implementation divides by zero here.
        // A native game cap outside our 1..999 independent range is valid; leave its behavior alone.
        if (fps > 999) { invokeOriginal(state.original, fps); return; }
        try { state.pacer.await(fps, PacingSettings.waitMode()); }
        catch (RuntimeException unavailable) { invokeOriginal(state.original, fps); }
    }
    private static void invokeOriginal(Method original, int fps) {
        try { original.invoke(null, fps); }
        catch (InvocationTargetException failure) {
            Throwable cause = failure.getCause();
            if (cause instanceof RuntimeException) throw (RuntimeException) cause;
            if (cause instanceof Error) throw (Error) cause;
            throw new IllegalStateException(cause);
        } catch (ReflectiveOperationException failure) { throw new IllegalStateException(failure); }
    }
    private static final class NativeLimit {
        final Method original;
        final PresentationLimiter pacer = new PresentationLimiter();
        NativeLimit(Method original) { this.original = original; }
    }
    public static void close() {
        presentationActive = windowActive = false; frameStarted = 0; nativeLimits.clear();
    }
    public static void frameStart() { frameStarted = System.nanoTime(); }
    public static void frameEnd() { frameStarted = 0; }
    public static void beforePresent(long now) {
        long start = frameStarted;
        if (start != 0 && now >= start) FrameMetrics.nativeMillis((now - start) / 1000000.0);
    }
}
