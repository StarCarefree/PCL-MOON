package pclmoon.framegen.bridge;

import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.net.URL;
import java.net.URLClassLoader;
import java.util.concurrent.ConcurrentHashMap;

/** Bootstrap-visible entry point; it has no symbolic dependency on LWJGL or Minecraft. */
public final class SwapHook {
    private static volatile String agentUrl;
    private static final ConcurrentHashMap<Class<?>, Session> sessions = new ConcurrentHashMap<Class<?>, Session>();
    public static void configure(String url) { agentUrl = url; }

    public static void swap(long window, Class<?> glfw) {
        Session session = sessions.get(glfw);
        if (session == null) {
            synchronized (sessions) {
                session = sessions.get(glfw);
                if (session == null) {
                    try { session = new Session(window, glfw); sessions.put(glfw, session); }
                    catch (Throwable unavailable) { nativeCall(method(glfw, "pclmoon$glfwSwapBuffers", long.class), window); return; }
                }
            }
        }
        // A secondary/utility context is never processed with the main window's GL resources.
        if (session.window != window) { nativeCall(session.swap, window); return; }
        session.present();
    }

    public static void destroy(long window, Class<?> glfw) {
        Session session = sessions.get(glfw);
        if (session != null && session.window == window && sessions.remove(glfw, session)) session.close();
        nativeCall(method(glfw, "pclmoon$glfwDestroyWindow", long.class), window);
    }

    private static Method method(Class<?> owner, String name, Class<?>... params) {
        try { return owner.getMethod(name, params); }
        catch (ReflectiveOperationException invalid) { throw new IllegalStateException("Missing GLFW entry point", invalid); }
    }
    private static Object call(Method method, Object receiver, Object... args) throws Throwable {
        try { return method.invoke(receiver, args); }
        catch (InvocationTargetException target) { throw target.getCause(); }
    }
    private static void nativeCall(Method method, long window) {
        try { call(method, null, window); }
        catch (Throwable error) { throw new NativeFailure(error); }
    }
    private static final class NativeFailure extends RuntimeException {
        NativeFailure(Throwable cause) { super(cause); }
    }

    private static final class Session {
        final long window;
        final Class<?> glfw;
        final Method swap, context, attribute;
        int outputFps = FrameSettings.outputFps();
        int multiplier = FrameSettings.multiplier(), staleMs = FrameSettings.stallThresholdMs();
        boolean adaptive = FrameSettings.adaptivePacing();
        FramePacer pacer = new FramePacer(outputFps, multiplier, adaptive, staleMs);
        final PresentationLimiter presentationLimiter = new PresentationLimiter();
        boolean frameActive, deviceIdentified;
        long realPresentationDeadline;
        int requestedFlow = FrameSettings.flowWidth(), effectiveFlow = requestedFlow;
        int quality = FrameSettings.quality(), gpuBudget = FrameSettings.gpuBudgetMs();
        int healthyFrames;
        FrameDiagnostics diagnostics;
        boolean requestedGeneration = FrameSettings.generation();
        boolean enabled = requestedGeneration;
        boolean overlayUnavailable;
        GameSettingsOverlay overlay;
        Method windowPosition, windowSize;
        final int[] x = new int[1], y = new int[1], width = new int[1], height = new int[1];
        long nextOverlayUpdate;
        boolean overlayPolled;
        Object renderer;
        URLClassLoader rendererLoader;
        Method capture, intermediate, current, reset, dispose, overBudget, configureRenderer, gpuTime, rendererReady;
        int cooldown;
        boolean initialised;

        Session(long window, Class<?> glfw) {
            this.window = window;
            this.glfw = glfw;
            swap = method(glfw, "pclmoon$glfwSwapBuffers", long.class);
            context = method(glfw, "glfwGetCurrentContext");
            attribute = method(glfw, "glfwGetWindowAttrib", long.class, int.class);
            FrameDiagnostics monitor = null;
            if (FrameSettings.diagnostics()) {
                try { monitor = new FrameDiagnostics(); }
                catch (Throwable ignored) { System.err.println("[PCL FrameTime] diagnostics unavailable"); }
            }
            diagnostics = monitor;
        }
        void applySettings() throws Throwable {
            FramePacerProfiles.applyPending();
            boolean requested = FrameSettings.generation();
            if (requestedGeneration != requested) {
                requestedGeneration = requested;
                enabled = requested;
                pacer.reset();
                cooldown = 0;
                releaseRenderer();
                initialised = false;
            }
            int target = FrameSettings.outputFps(), factor = FrameSettings.multiplier(), threshold = FrameSettings.stallThresholdMs();
            int independent = PacingSettings.fpsLimit();
            if (independent >= 30) target = Math.min(target, independent);
            boolean adapt = FrameSettings.adaptivePacing();
            if (target != outputFps || factor != multiplier || adapt != adaptive || threshold != staleMs) {
                outputFps = target; multiplier = factor; adaptive = adapt; staleMs = threshold;
                pacer = new FramePacer(target, factor, adapt, threshold);
                if (renderer != null) call(reset, renderer);
            }
            int flow = FrameSettings.flowWidth(), mode = FrameSettings.quality(), budget = FrameSettings.gpuBudgetMs();
            if (flow != requestedFlow || mode != quality || budget != gpuBudget) {
                requestedFlow = flow; effectiveFlow = flow; quality = mode; gpuBudget = budget;
                cooldown = healthyFrames = 0;
                pacer.reset();
            }
            if (renderer != null) call(configureRenderer, renderer, effectiveFlow, quality, 0.22f, (float) gpuBudget);
            if (FrameSettings.diagnostics() != (diagnostics != null)) {
                if (diagnostics != null) { diagnostics.close(); diagnostics = null; }
                else {
                    try { diagnostics = new FrameDiagnostics(); }
                    catch (Throwable unavailable) { FrameSettings.setDiagnostics(false); }
                }
            }
        }
        void updateOverlay(boolean active) {
            if (overlayUnavailable || !Boolean.getBoolean("pclmoon.framegen.overlay")) return;
            long now = System.nanoTime();
            if (overlayPolled && now - nextOverlayUpdate < 0) return;
            overlayPolled = true;
            nextOverlayUpdate = now + 100000000L;
            try {
                if (windowPosition == null) {
                    windowPosition = method(glfw, "glfwGetWindowPos", long.class, int[].class, int[].class);
                    windowSize = method(glfw, "glfwGetWindowSize", long.class, int[].class, int[].class);
                }
                call(windowPosition, null, window, x, y);
                call(windowSize, null, window, width, height);
                if (overlay == null) {
                    if (!active) return;
                    overlay = new GameSettingsOverlay();
                }
                boolean visible = ((Integer) call(attribute, null, window, 0x00020002)) == 0
                    && ((Integer) call(attribute, null, window, 0x00020004)) != 0;
                overlay.update(x[0], y[0], visible ? width[0] : 0, visible ? height[0] : 0, active);
            } catch (Throwable unavailable) {
                overlayUnavailable = true;
                closeOverlay();
                System.err.println("[PCL FrameGen] settings button unavailable (" + unavailable.getClass().getSimpleName()
                    + "); game presentation unchanged");
            }
        }
        void closeOverlay() {
            if (overlay != null) {
                try { overlay.close(); } catch (Throwable ignored) { }
                overlay = null;
            }
        }
        boolean foreground() throws Throwable {
            return ((Integer) call(attribute, null, window, 0x00020001)) != 0 // GLFW_FOCUSED
                && ((Integer) call(attribute, null, window, 0x00020002)) == 0 // GLFW_ICONIFIED
                && ((Integer) call(attribute, null, window, 0x00020004)) != 0; // GLFW_VISIBLE
        }
        void initialise() throws Throwable {
            initialised = true;
            if (((Long) call(context, null)).longValue() != window) {
                enabled = false;
                System.err.println("[PCL FrameGen] no current OpenGL context (e.g. Vulkan); original presentation retained");
                return;
            }
            rendererLoader = new URLClassLoader(new URL[] { new URL(agentUrl) }, glfw.getClassLoader()) {
                protected Class<?> loadClass(String name, boolean resolve) throws ClassNotFoundException {
                    if (!name.startsWith("pclmoon.framegen.render.")) return super.loadClass(name, resolve);
                    synchronized (getClassLoadingLock(name)) {
                        Class<?> type = findLoadedClass(name);
                        if (type == null) type = findClass(name);
                        if (resolve) resolveClass(type);
                        return type;
                    }
                }
            };
            Class<?> type = Class.forName("pclmoon.framegen.render.GlInterpolator", true, rendererLoader);
            renderer = type.getConstructor(long.class).newInstance(window);
            capture = method(type, "capture"); intermediate = method(type, "drawInterpolated", float.class);
            current = method(type, "drawCurrent"); reset = method(type, "resetHistory");
            dispose = method(type, "close"); overBudget = method(type, "isOverBudget");
            configureRenderer = method(type, "configure", int.class, int.class, float.class, float.class);
            gpuTime = method(type, "lastGpuMillis"); rendererReady = method(type, "ready");
            call(configureRenderer, renderer, effectiveFlow, quality, 0.22f, (float) gpuBudget);
            System.err.println("[PCL FrameGen] motion-compensated presentation enabled; target ceiling=" + outputFps
                + " FPS, multiplier=" + multiplier + "; adaptive pacing=" + adaptive + "; generation adds latency");
        }
        void present() {
            boolean active = false;
            frameActive = false;
            realPresentationDeadline = 0;
            NativeGameHooks.setWindowActive(false);
            try {
                active = foreground();
                frameActive = active;
                NativeGameHooks.setWindowActive(active);
                updateOverlay(active);
                applySettings();
                identifyTelemetryDevice(active);
                if (diagnostics != null) diagnostics.frame(active);
                NativeGameHooks.beforePresent(System.nanoTime());
                if (enabled && !initialised) initialise();
                boolean lowCap = PacingSettings.fpsLimit() > 0 && PacingSettings.fpsLimit() < 30;
                if (enabled && active && !lowCap && ((Long) call(context, null)).longValue() == window) {
                    long now = System.nanoTime();
                    if (pacer.historyExpired(now)) {
                        call(reset, renderer); pacer.reset();
                        FrameMetrics.skip("长帧恢复：丢弃旧历史");
                    }
                    if (cooldown > 0) { cooldown--; pacer.reset(); FrameMetrics.skip("GPU 保护冷却"); }
                    else if (FrameSettings.lowFpsProtection() && (Boolean) call(overBudget, renderer)) {
                        int lower = Math.max(80, effectiveFlow * 3 / 4);
                        cooldown = lower < effectiveFlow ? 2 : 30;
                        effectiveFlow = lower;
                        healthyFrames = 0;
                        call(configureRenderer, renderer, effectiveFlow, quality, 0.22f, (float) gpuBudget);
                        call(reset, renderer); pacer.reset();
                        FrameMetrics.skip("GPU 超预算：运动场降至 " + effectiveFlow);
                    } else {
                        if (++healthyFrames >= 300 && effectiveFlow < requestedFlow) {
                            effectiveFlow = Math.min(requestedFlow, effectiveFlow + 40);
                            call(configureRenderer, renderer, effectiveFlow, quality, 0.22f, (float) gpuBudget);
                            pacer.reset(); healthyFrames = 0;
                        }
                        boolean ready = (Boolean) call(capture, renderer);
                        FrameMetrics.gpuMillis(((Number) call(gpuTime, renderer)).doubleValue());
                        if (ready) generate();
                        else FrameMetrics.status("预热 / 调整缓冲");
                    }
                    NativeGameHooks.setPresentationActive(cooldown == 0 && (Boolean) call(rendererReady, renderer));
                } else {
                    if (renderer != null) { call(reset, renderer); pacer.reset(); }
                    NativeGameHooks.setPresentationActive(false);
                    FrameMetrics.status(lowCap ? "独立限帧低于 30：暂停插帧" : enabled ? "窗口未激活 / 后端不支持" : "插帧已关闭");
                }
            } catch (NativeFailure originalFailure) {
                throw originalFailure;
            } catch (Throwable unavailable) {
                disable(unavailable);
            }
            presentNative(realPresentationDeadline);
            long presented = System.nanoTime();
            pacer.presented(presented);
            FrameMetrics.realPresented(presented);
            if (diagnostics != null) {
                // Keep the current focus state; normal rendering only updates a heartbeat.
                diagnostics.presented();
            }
        }
        void presentNative() { presentNative(0L); }
        void presentNative(long deadline) {
            try { presentationLimiter.await(frameActive ? PacingSettings.fpsLimit() : 0, PacingSettings.waitMode(), deadline); }
            catch (RuntimeException unavailable) { presentationLimiter.reset(); FrameMetrics.status("独立限帧回退"); }
            nativeCall(swap, window); // A failed native swap is outside the pacing catch: never retry it.
        }
        void identifyTelemetryDevice(boolean active) {
            if (deviceIdentified || !active || !FrameSettings.showFps() || !PacingSettings.telemetry()) return;
            try {
                if (((Long) call(context, null)).longValue() != window) return;
                Class<?> gl = Class.forName("org.lwjgl.opengl.GL11", false, glfw.getClassLoader());
                Object name = call(method(gl, "glGetString", int.class), null, 0x1F01); // GL_RENDERER
                PacingSettings.setRendererName(name instanceof String ? (String) name : "");
                deviceIdentified = true;
            } catch (Throwable unsupported) { deviceIdentified = true; }
        }
        void generate() throws Throwable {
            long now = System.nanoTime();
            if (!pacer.plan(now)) { FrameMetrics.skip("真帧过期 / 固定节奏未赶上"); return; }
            boolean touched = false, nativeFailed = false;
            int submitted = 0;
            try {
                for (int i = 0; i < multiplier - 1; i++) {
                    if (System.nanoTime() > pacer.realDeadline()) break;
                    touched = true;
                    call(intermediate, renderer, (i + 1) / (float) multiplier);
                    if (System.nanoTime() > pacer.realDeadline()) break;
                    presentNative(pacer.deadline(i));
                    FrameMetrics.generatedPresented(System.nanoTime());
                    submitted++;
                }
            } catch (NativeFailure originalFailure) {
                nativeFailed = true;
                throw originalFailure;
            } finally {
                // A restore error must not mask a failed native swap and cause a duplicate retry.
                if (touched) {
                    try { call(current, renderer); }
                    catch (Throwable restoreFailure) { if (!nativeFailed) throw restoreFailure; }
                }
            }
            if (submitted > 0) {
                realPresentationDeadline = pacer.realDeadline();
                FrameMetrics.status("运行中 " + multiplier + "× · 运动场 " + effectiveFlow);
            } else FrameMetrics.skip("生成错过预算：优先新真帧");
        }
        void disable(Throwable error) {
            enabled = false;
            pacer.reset();
            NativeGameHooks.setPresentationActive(false);
            FrameMetrics.status("插帧异常回退：" + error.getClass().getSimpleName());
            System.err.println("[PCL FrameGen] generation disabled (" + error.getClass().getSimpleName() + "); original presentation retained");
            releaseRenderer();
        }
        void releaseRenderer() {
            if (renderer != null && dispose != null) {
                try {
                    if (((Long) call(context, null)).longValue() == window) call(dispose, renderer);
                } catch (Throwable ignored) { }
            }
            renderer = null;
            if (rendererLoader != null) { try { rendererLoader.close(); } catch (Exception ignored) { } rendererLoader = null; }
        }
        void close() {
            if (diagnostics != null) diagnostics.close();
            NativeGameHooks.setPresentationActive(false);
            closeOverlay();
            NativeGameHooks.close();
            FramePacerProfiles.close();
            FrameTelemetry.stop();
            releaseRenderer();
        }
    }
}
