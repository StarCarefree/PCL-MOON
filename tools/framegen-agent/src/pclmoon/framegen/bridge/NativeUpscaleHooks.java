package pclmoon.framegen.bridge;

import java.lang.reflect.*;
import java.net.URL;
import java.net.URLClassLoader;
import java.util.Map;
import java.util.WeakHashMap;

/** Bootstrap-only reflection bridge. All GPU work stays on the world/render owner thread. */
public final class NativeUpscaleHooks {
    private static final Map<ClassLoader, Integer> installed = new WeakHashMap<ClassLoader, Integer>();
    private static final ThreadLocal<Session> scope = new ThreadLocal<Session>();
    private static volatile String agentUrl;
    private static Session session;
    private static final ClassValue<Method> originals = new ClassValue<Method>() {
        protected Method computeValue(Class<?> owner) {
            try {
                Method m = owner.getDeclaredMethod("pclmoon$srRenderWorld", Class.forName("net.minecraft.class_9779", false, owner.getClassLoader()));
                m.setAccessible(true); return m;
            } catch (Exception e) { throw new IllegalStateException("missing original world render", e); }
        }
    };
    private NativeUpscaleHooks() { }
    public static void configure(String url) { agentUrl = url; }
    public static synchronized void installed(ClassLoader loader, int bits) {
        Integer old = installed.get(loader); installed.put(loader, (old == null ? 0 : old) | bits);
    }
    private static synchronized boolean complete(ClassLoader loader) { return Integer.valueOf(7).equals(installed.get(loader)); }
    public static Object selectFramebuffer(Object original) {
        Session s = scope.get(); return s != null && original == s.output ? s.scaled : original;
    }
    public static int framebufferWidth(int original, Object window) {
        Session s = scope.get(); return s != null && s.window == window ? s.sw : original;
    }
    public static int framebufferHeight(int original, Object window) {
        Session s = scope.get(); return s != null && s.window == window ? s.sh : original;
    }
    public static int framebufferWidth(int original) { Session s = scope.get(); return s == null ? original : s.sw; }
    public static int framebufferHeight(int original) { Session s = scope.get(); return s == null ? original : s.sh; }

    public static void renderWorld(Object renderer, Object counter, Class<?> owner) throws Throwable {
        Method original = originals.get(owner);
        // A recursive invocation must retain its enclosing scope, not clear or upscale it.
        if (scope.get() != null) { invoke(original, renderer, counter); return; }
        UpscaleSettings.Values settings = UpscaleSettings.snapshot();
        Session current = null;
        if (!settings.enabled) {
            close(); UpscaleSettings.runtime(false, "超分已关闭", 0, 0, 0, 0);
        } else if (!complete(owner.getClassLoader())) {
            UpscaleSettings.runtime(false, "不支持的渲染结构：保留原生分辨率", 0, 0, 0, 0);
        } else {
            try {
                if (session == null) session = new Session(renderer, owner);
                if (session.ownerThread != Thread.currentThread() || session.renderer != renderer)
                    throw new IllegalStateException("different render owner");
                if (session.prepare(settings)) current = session;
            } catch (Throwable unavailable) {
                if (session != null && session.ownerThread == Thread.currentThread()) session.fail(settings.revision, unavailable);
                else UpscaleSettings.runtime(false, "超分适配不可用：保留原生分辨率", 0, 0, 0, 0);
            }
        }
        if (current != null) scope.set(current);
        try { invoke(original, renderer, counter); }
        finally { scope.remove(); }
        // Deliberately outside the finally: an original game exception propagates exactly once.
        if (current != null) current.resolve(settings);
    }
    public static void close() {
        Session s = session;
        if (s == null || s.ownerThread != Thread.currentThread()) return;
        scope.remove(); session = null; s.dispose();
        UpscaleSettings.runtime(false, "等待世界渲染", 0, 0, 0, 0);
    }
    private static Object invoke(Method m, Object receiver, Object... args) throws Throwable {
        try { return m.invoke(receiver, args); }
        catch (InvocationTargetException original) { throw original.getCause(); }
    }
    private static Class<?> type(ClassLoader loader, String name) throws ClassNotFoundException { return Class.forName(name, false, loader); }

    private static final class Session {
        final Thread ownerThread = Thread.currentThread();
        final Object renderer, client, window;
        final Method framebuffer, windowWidth, windowHeight, color, depth, textureId, delete, encoder, getDevice, clear;
        final Field width, height, usesDepth;
        final Constructor<?> create;
        final Class<?> glTexture;
        URLClassLoader loader;
        Object backend, scaled, output;
        Method prepare, upscale, release;
        int sw, sh, dw, dh;
        long failedRevision = Long.MIN_VALUE;
        String failure;
        Method irisPack;
        Session(Object renderer, Class<?> owner) throws Throwable {
            this.renderer = renderer;
            ClassLoader game = owner.getClassLoader();
            client = invoke(owner.getMethod("method_35772"), renderer);
            framebuffer = client.getClass().getMethod("method_1522");
            window = invoke(client.getClass().getMethod("method_22683"), client);
            Class<?> win = type(game, "net.minecraft.class_1041");
            windowWidth = win.getMethod("method_4489"); windowHeight = win.getMethod("method_4506");
            Class<?> fb = type(game, "net.minecraft.class_276");
            color = fb.getMethod("method_30277"); depth = fb.getMethod("method_30278"); delete = fb.getMethod("method_1238");
            width = fb.getField("field_1482"); height = fb.getField("field_1481"); usesDepth = fb.getField("field_1478");
            create = type(game, "net.minecraft.class_6367").getConstructor(String.class, int.class, int.class, boolean.class);
            glTexture = type(game, "net.minecraft.class_10868"); textureId = glTexture.getMethod("method_68427");
            Class<?> gpu = type(game, "com.mojang.blaze3d.textures.GpuTexture");
            getDevice = type(game, "com.mojang.blaze3d.systems.RenderSystem").getMethod("getDevice");
            encoder = type(game, "com.mojang.blaze3d.systems.GpuDevice").getMethod("createCommandEncoder");
            clear = type(game, "com.mojang.blaze3d.systems.CommandEncoder").getMethod("clearColorAndDepthTextures", gpu, int.class, gpu, double.class);
            try { irisPack = type(game, "net.irisshaders.iris.Iris").getMethod("getCurrentPack"); }
            catch (ClassNotFoundException absent) { }
            catch (NoSuchMethodException unknownIris) { throw new IllegalStateException("unknown shader pipeline", unknownIris); }
        }
        boolean prepare(UpscaleSettings.Values settings) throws Throwable {
            if (failedRevision == settings.revision) {
                UpscaleSettings.runtime(false, failure, sw, sh, dw, dh); return false;
            }
            if (irisPack != null && ((java.util.Optional<?>) invoke(irisPack, null)).isPresent()) {
                UpscaleSettings.runtime(false, "Iris 光影管线尚未适配：保留原生分辨率", 0, 0, 0, 0); return false;
            }
            output = invoke(framebuffer, client);
            int w = width.getInt(output), h = height.getInt(output);
            if (w < 2 || h < 2 || w != ((Integer) invoke(windowWidth, window)).intValue()
                || h != ((Integer) invoke(windowHeight, window)).intValue() || !usesDepth.getBoolean(output))
                throw new IllegalStateException("non-native framebuffer dimensions/depth");
            id(invoke(color, output)); id(invoke(depth, output));
            if (backend == null) {
                if (agentUrl == null) throw new IllegalStateException("no renderer URL");
                loader = new URLClassLoader(new URL[] { new URL(agentUrl) }, renderer.getClass().getClassLoader()) {
                    protected Class<?> loadClass(String name, boolean resolve) throws ClassNotFoundException {
                        if (!name.startsWith("pclmoon.framegen.render.")) return super.loadClass(name, resolve);
                        synchronized (getClassLoadingLock(name)) {
                            Class<?> t = findLoadedClass(name); if (t == null) t = findClass(name);
                            if (resolve) resolveClass(t); return t;
                        }
                    }
                };
                Class<?> b = Class.forName("pclmoon.framegen.render.GlSuperResolutionRenderer", true, loader);
                backend = b.getConstructor().newInstance();
                prepare = b.getMethod("prepare", int.class, int.class);
                upscale = b.getMethod("upscale", int.class, int.class, int.class, int.class, int.class, int.class, int.class, int.class, float.class);
                release = b.getMethod("close");
            }
            if (!((Boolean) invoke(prepare, backend, w, h)).booleanValue()) throw new IllegalStateException("OpenGL 3.3/FSR unavailable");
            int x = settings.scalePercent == 100 ? w : Math.max(2, (w * settings.scalePercent / 100) & ~1);
            int y = settings.scalePercent == 100 ? h : Math.max(2, (h * settings.scalePercent / 100) & ~1);
            if (scaled == null || sw != x || sh != y) {
                if (scaled != null) { invoke(delete, scaled); scaled = null; }
                scaled = create.newInstance("PCL spatial super resolution world", x, y, true);
                sw = x; sh = y;
            }
            dw = w; dh = h;
            Object c = invoke(color, scaled), d = invoke(depth, scaled);
            id(c); id(d);
            invoke(clear, invoke(encoder, invoke(getDevice, null)), c, 0, d, 1.0);
            return true;
        }
        int id(Object texture) throws Throwable {
            if (!glTexture.isInstance(texture)) throw new IllegalStateException("not an OpenGL texture");
            int id = ((Integer) invoke(textureId, texture)).intValue();
            if (id <= 0) throw new IllegalStateException("invalid texture"); return id;
        }
        void resolve(UpscaleSettings.Values settings) {
            try {
                boolean reconstructed = ((Boolean) invoke(upscale, backend, id(invoke(color, scaled)), id(invoke(depth, scaled)),
                    id(invoke(color, output)), id(invoke(depth, output)), sw, sh, dw, dh, settings.sharpnessPercent / 100f)).booleanValue();
                if (!reconstructed) throw new IllegalStateException("GPU fallback blit");
                UpscaleSettings.runtime(true, "FSR1 EASU + RCAS（空间超分）", sw, sh, dw, dh);
            } catch (Throwable unavailable) { fail(settings.revision, unavailable); }
        }
        void fail(long revision, Throwable reason) {
            failedRevision = revision;
            failure = "超分回退原生：" + reason.getClass().getSimpleName();
            System.err.println("[PCL SuperResolution] " + failure + " / " + reason.getMessage());
            UpscaleSettings.runtime(false, failure, sw, sh, dw, dh);
            dispose();
        }
        void dispose() {
            try { if (scaled != null) invoke(delete, scaled); } catch (Throwable ignored) { }
            scaled = null;
            try { if (backend != null && release != null) invoke(release, backend); } catch (Throwable ignored) { }
            backend = null;
            try { if (loader != null) loader.close(); } catch (Throwable ignored) { }
            loader = null;
        }
    }
}
