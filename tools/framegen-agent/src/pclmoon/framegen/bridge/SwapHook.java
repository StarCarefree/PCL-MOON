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
        final FramePacer pacer = new FramePacer(Integer.getInteger("pclmoon.framegen.outputFps", 60));
        final FrameDiagnostics diagnostics;
        boolean enabled = Boolean.parseBoolean(System.getProperty("pclmoon.framegen.enabled", "true"));
        Object renderer;
        URLClassLoader rendererLoader;
        Method capture, intermediate, current, reset, dispose, overBudget;
        int cooldown;
        boolean initialised;

        Session(long window, Class<?> glfw) {
            this.window = window;
            this.glfw = glfw;
            swap = method(glfw, "pclmoon$glfwSwapBuffers", long.class);
            context = method(glfw, "glfwGetCurrentContext");
            attribute = method(glfw, "glfwGetWindowAttrib", long.class, int.class);
            FrameDiagnostics monitor = null;
            if (Boolean.getBoolean("pclmoon.framegen.diagnostics")) {
                try { monitor = new FrameDiagnostics(); }
                catch (Throwable ignored) { System.err.println("[PCL FrameTime] diagnostics unavailable"); }
            }
            diagnostics = monitor;
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
            capture = method(type, "capture"); intermediate = method(type, "drawIntermediate");
            current = method(type, "drawCurrent"); reset = method(type, "resetHistory");
            dispose = method(type, "close"); overBudget = method(type, "isOverBudget");
            System.err.println("[PCL FrameGen] experimental 2x motion-compensated presentation enabled; output target="
                + Math.max(30, Math.min(240, Integer.getInteger("pclmoon.framegen.outputFps", 60)))
                + " FPS; true rendering rate is lower, interpolation adds latency");
        }
        void present() {
            boolean touched = false;
            try {
                boolean active = foreground();
                if (diagnostics != null) diagnostics.frame(active);
                if (enabled && !initialised) initialise();
                if (enabled && active && ((Long) call(context, null)).longValue() == window) {
                    long now = System.nanoTime();
                    if (pacer.historyExpired(now)) { call(reset, renderer); pacer.reset(); }
                    if (cooldown > 0) { cooldown--; pacer.reset(); }
                    else if ((Boolean) call(overBudget, renderer)) {
                        cooldown = 120;
                        call(reset, renderer);
                        pacer.reset();
                        System.err.println("[PCL FrameGen] GPU budget exceeded; pausing generation for 120 real frames");
                    } else {
                        boolean ready = (Boolean) call(capture, renderer);
                        if (ready && pacer.canGenerate(System.nanoTime())) {
                            long realDeadline = pacer.realDeadline();
                            if (FramePacer.await(pacer.midpoint())) {
                                touched = true;
                                call(intermediate, renderer);
                                // If flow computation itself missed the useful deadline, do not emit a burst of stale frames.
                                if (System.nanoTime() < realDeadline) {
                                    nativeCall(swap, window);
                                    FramePacer.await(realDeadline);
                                }
                                call(current, renderer);
                                touched = false;
                            }
                        }
                    }
                } else if (renderer != null) { call(reset, renderer); pacer.reset(); }
            } catch (NativeFailure originalFailure) {
                throw originalFailure; // A failed native presentation is never retried.
            } catch (Throwable unavailable) {
                if (touched && renderer != null && current != null) {
                    try { call(current, renderer); } catch (Throwable ignored) { }
                }
                disable(unavailable);
            }
            nativeCall(swap, window);
            pacer.presented(System.nanoTime());
            if (diagnostics != null) {
                // Keep the current focus state; normal rendering only updates a heartbeat.
                diagnostics.presented();
            }
        }
        void disable(Throwable error) {
            enabled = false;
            pacer.reset();
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
            releaseRenderer();
        }
    }
}
