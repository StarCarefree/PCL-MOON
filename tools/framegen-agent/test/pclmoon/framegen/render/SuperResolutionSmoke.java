package pclmoon.framegen.render;

import org.lwjgl.glfw.GLFW;
import org.lwjgl.glfw.GLFWErrorCallback;
import org.lwjgl.opengl.GL;
import org.lwjgl.opengl.GL11;
import org.lwjgl.opengl.GL12;
import org.lwjgl.opengl.GL13;
import org.lwjgl.opengl.GL14;
import org.lwjgl.opengl.GL15;
import org.lwjgl.opengl.GL20;
import org.lwjgl.opengl.GL21;
import org.lwjgl.opengl.GL30;
import org.lwjgl.opengl.GL33;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.FloatBuffer;
import java.util.Arrays;

/** Real hidden GL 3.3 FSR1 EASU/RCAS check. CPU pixels and glFinish exist only here. */
public final class SuperResolutionSmoke {
    private static final int SRC_W = 64, SRC_H = 36, DST_W = 128, DST_H = 72;
    private static int userFbo, userVao, userProgram;
    private static final int[] userTextures = new int[4], userSamplers = new int[4];

    public static void main(String[] args) throws Exception {
        GLFWErrorCallback callback = GLFWErrorCallback.createPrint(System.err).set();
        if (!GLFW.glfwInit()) throw new AssertionError("GLFW unavailable");
        long window = 0;
        GlSuperResolutionRenderer renderer = null;
        int input = 0, output = 0, depthIn = 0, depthOut = 0, readFbo = 0;
        try {
            GLFW.glfwWindowHint(GLFW.GLFW_VISIBLE, GLFW.GLFW_FALSE);
            GLFW.glfwWindowHint(GLFW.GLFW_CONTEXT_VERSION_MAJOR, 3);
            GLFW.glfwWindowHint(GLFW.GLFW_CONTEXT_VERSION_MINOR, 3);
            GLFW.glfwWindowHint(GLFW.GLFW_OPENGL_PROFILE, GLFW.GLFW_OPENGL_CORE_PROFILE);
            window = GLFW.glfwCreateWindow(DST_W, DST_H, "PCL FSR1 smoke", 0, 0);
            check(window != 0, "no GL 3.3 window");
            GLFW.glfwMakeContextCurrent(window);
            check(GL.createCapabilities().OpenGL33, "GL 3.3 required");
            System.out.println("GPU: " + GL11.glGetString(GL11.GL_VENDOR) + " / " + GL11.glGetString(GL11.GL_RENDERER)
                + " / " + GL11.glGetString(GL11.GL_VERSION));
            createUserState();
            renderer = new GlSuperResolutionRenderer();
            check(!renderer.prepare(0, DST_H), "invalid size must be unsupported");
            long before = used();
            int unpack = GL15.glGenBuffers();
            GL15.glBindBuffer(GL21.GL_PIXEL_UNPACK_BUFFER, unpack);
            GL15.glBufferData(GL21.GL_PIXEL_UNPACK_BUFFER, 16L, GL15.GL_STATIC_DRAW);
            long t0 = System.nanoTime();
            check(renderer.prepare(DST_W, DST_H), "prepare must compile official EASU/RCAS");
            long prepareNs = System.nanoTime() - t0;
            check(renderer.prepare(DST_W, DST_H), "prepare is idempotent for the same output");
            check(GL11.glGetInteger(GL21.GL_PIXEL_UNPACK_BUFFER_BINDING) == unpack, "prepare lost external unpack buffer");
            GL15.glBindBuffer(GL21.GL_PIXEL_UNPACK_BUFFER, 0); GL15.glDeleteBuffers(unpack);
            input = colorTexture(SRC_W, SRC_H, false);
            output = colorTexture(DST_W, DST_H, false);
            upload(input, pattern(SRC_W, SRC_H, false), SRC_W, SRC_H);
            depthIn = depthTexture(SRC_W, SRC_H, 0.25f);
            depthOut = depthTexture(DST_W, DST_H, 0.9f);
            readFbo = GL30.glGenFramebuffers();

            bindUserState();
            int[] saved = snapshot();
            check(GL11.glGetError() == GL11.GL_NO_ERROR, "test state before EASU");
            int query = GL15.glGenQueries();
            GL15.glBeginQuery(GL33.GL_TIME_ELAPSED, query);
            t0 = System.nanoTime();
            check(renderer.upscale(input, 0, output, 0, SRC_W, SRC_H, DST_W, DST_H, 0f), "EASU scale");
            GL11.glFinish();
            long easuNs = System.nanoTime() - t0;
            check(GL15.glGetQueryi(GL33.GL_TIME_ELAPSED, GL15.GL_CURRENT_QUERY) == query, "stole an external timer query");
            GL15.glEndQuery(GL33.GL_TIME_ELAPSED);
            GL15.glDeleteQueries(query);
            same(saved, "EASU");
            float[] easu = read(readFbo, output, DST_W, DST_H);
            float[] reference = lanczosReference(pattern(SRC_W, SRC_H, false));
            double easuMae = mae(easu, reference);
            double nearestMae = nearestMae(reference);
            check(easuMae < nearestMae * 0.85, "EASU is not better than nearest MAE=" + easuMae + " nearest=" + nearestMae);
            check(easuMae < 0.08, "EASU diverged from the Lanczos-position reference MAE=" + easuMae);
            checkEdge(easu, reference);
            int depthSrc = input;
            bindUserState();
            saved = snapshot();
            check(renderer.upscale(input, depthIn, output, depthOut, SRC_W, SRC_H, DST_W, DST_H, 0f), "scaled depth EASU");
            same(saved, "scaled depth");
            check(depthAt(readFbo, depthOut, DST_W / 2, DST_H / 2) < 0.4f, "depth was not nearest-copied");
            bindUserState();
            saved = snapshot();
            check(renderer.upscale(input, 0, output, 0, SRC_W, SRC_H, DST_W, DST_H, 0.35f), "RCAS failed");
            same(saved, "RCAS");
            float[] sharp = read(readFbo, output, DST_W, DST_H);
            check(maxDelta(sharp, easu) > 0.004, "RCAS did not change an edge");
            check(maxDelta(sharp, easu) < 1.1, "RCAS changed a channel by more than its full range");
            int flatInput = colorTexture(SRC_W, SRC_H, false);
            upload(flatInput, pattern(SRC_W, SRC_H, true), SRC_W, SRC_H);
            check(renderer.upscale(flatInput, 0, output, 0, SRC_W, SRC_H, DST_W, DST_H, 0f), "flat EASU");
            float[] flat = read(readFbo, output, DST_W, DST_H);
            check(renderer.upscale(flatInput, 0, output, 0, SRC_W, SRC_H, DST_W, DST_H, 0.35f), "flat RCAS");
            check(flatMax(read(readFbo, output, DST_W, DST_H), flat) <= 1.01 / 255, "RCAS changed a truly constant area");
            GL11.glDeleteTextures(flatInput);

            int unity = colorTexture(DST_W, DST_H, false);
            upload(unity, pattern(DST_W, DST_H, false), DST_W, DST_H);
            int unityOut = colorTexture(DST_W, DST_H, false);
            check(renderer.upscale(unity, 0, unityOut, 0, DST_W, DST_H, DST_W, DST_H, 0f), "unity scale failed");
            check(mae(read(readFbo, unityOut, DST_W, DST_H), pattern(DST_W, DST_H, false)) < 0.004, "1:1 EASU changed texels");

            check(!renderer.upscale(0, 0, output, 0, SRC_W, SRC_H, DST_W, DST_H, 0f), "missing input must fail after fallback");
            check(renderer.upscale(input, 0, output, 0, SRC_W, SRC_H, DST_W, DST_H, 0f), "recovery after fallback failed");
            java.lang.reflect.Field programField = GlSuperResolutionRenderer.class.getDeclaredField("easuProgram");
            programField.setAccessible(true); int goodProgram = programField.getInt(renderer);
            programField.setInt(renderer, 0);
            check(renderer.upscale(unity, 0, unityOut, 0, DST_W, DST_H, DST_W, DST_H, 0.35f), "unity RCAS must not execute EASU");
            bindUserState(); saved = snapshot();
            check(!renderer.upscale(input, 0, output, 0, SRC_W, SRC_H, DST_W, DST_H, 0f), "injected shader failure must fall back");
            same(saved, "failure fallback");
            check(mean(read(readFbo, output, DST_W, DST_H)) > 0.05, "GPU fallback lost the current image");
            programField.setInt(renderer, goodProgram);
            check(renderer.upscale(input, 0, output, 0, SRC_W, SRC_H, DST_W, DST_H, 0f), "recovery after injected failure");
            float[] beforeHud = read(readFbo, output, DST_W, DST_H);
            int hudFbo = GL30.glGenFramebuffers();
            GL30.glBindFramebuffer(GL30.GL_DRAW_FRAMEBUFFER, hudFbo);
            GL30.glFramebufferTexture2D(GL30.GL_DRAW_FRAMEBUFFER, GL30.GL_COLOR_ATTACHMENT0, GL11.GL_TEXTURE_2D, output, 0);
            GL11.glDrawBuffer(GL30.GL_COLOR_ATTACHMENT0); GL30.glColorMaski(0, true, true, true, true);
            GL11.glEnable(GL11.GL_SCISSOR_TEST); GL11.glScissor(11, 13, 1, 1); GL11.glDisable(GL30.GL_FRAMEBUFFER_SRGB);
            GL11.glClearColor(1f, 0f, 1f, 1f); GL11.glClear(GL11.GL_COLOR_BUFFER_BIT);
            float[] afterHud = read(readFbo, output, DST_W, DST_H); int changed = 0;
            for (int i = 0; i < afterHud.length; i += 4)
                if (afterHud[i] != beforeHud[i] || afterHud[i+1] != beforeHud[i+1] || afterHud[i+2] != beforeHud[i+2]) changed++;
            check(changed == 1 && afterHud[(13 * DST_W + 11) * 4] == 1f, "native HUD pixel was rescaled or clipped");
            GL11.glDisable(GL11.GL_SCISSOR_TEST); GL30.glDeleteFramebuffers(hudFbo);

            GlSuperResolutionRenderer resized = new GlSuperResolutionRenderer();
            check(resized.prepare(96, 54), "resize prepare");
            int smallOut = colorTexture(96, 54, false);
            check(resized.upscale(input, 0, smallOut, 0, SRC_W, SRC_H, 96, 54, 0.2f), "resized upscale");
            check(mean(read(readFbo, smallOut, 96, 54)) > 0.05, "resized output is empty");
            int matchedDepth = depthTexture(96, 54, 0.33f);
            int matchedOut = depthTexture(96, 54, 1f);
            check(resized.upscale(input, depthIn, smallOut, matchedOut, SRC_W, SRC_H, 96, 54, 0.2f), "different-size nearest depth transfer");
            check(depthAt(readFbo, matchedOut, 48, 27) < 0.4f, "scaled depth was not copied");
            int matchedColor = colorTexture(96, 54, false);
            check(resized.upscale(matchedColor, matchedDepth, smallOut, matchedOut, 96, 54, 96, 54, 0f), "same-size nearest depth transfer");
            check(depthAt(readFbo, matchedOut, 48, 27) < 0.5f, "same-size depth was not nearest-copied");

            int srgbIn = colorTexture(SRC_W, SRC_H, true);
            int srgbOut = colorTexture(DST_W, DST_H, true);
            upload(srgbIn, pattern(SRC_W, SRC_H, false), SRC_W, SRC_H);
            check(renderer.upscale(srgbIn, 0, srgbOut, 0, SRC_W, SRC_H, DST_W, DST_H, 0f), "sRGB upscale");
            float[] encoded = read(readFbo, srgbOut, DST_W, DST_H);
            check(mae(encoded, easu) < 0.04, "sRGB output gamma changed the image");
            check(GL11.glGetError() == GL11.GL_NO_ERROR, "GL error leaked");
            bindUserState(); saved = snapshot();
            resized.close();
            renderer.close();
            renderer.close();
            same(saved, "close");
            long after = used();
            System.out.println("PASS FSR1 EASU/RCAS MAE=" + easuMae + " nearestMAE=" + nearestMae
                + " prepareMs=" + (prepareNs / 1_000_000.0) + " easuFinishMs=" + (easuNs / 1_000_000.0)
                + " heapDeltaKiB=" + ((after - before) / 1024));
            GL11.glDeleteTextures(new int[] {input, output, depthIn, depthOut, unity, unityOut, smallOut, srgbIn, srgbOut, matchedColor, matchedDepth, matchedOut});
            GL30.glDeleteFramebuffers(readFbo);
        } finally {
            if (renderer != null) renderer.close();
            if (window != 0) GLFW.glfwDestroyWindow(window);
            GLFW.glfwTerminate();
            callback.free();
        }
    }

    private static float[] pattern(int w, int h, boolean solid) {
        float[] px = new float[w * h * 4];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) {
            int i = (y * w + x) * 4;
            boolean edge = x * DST_W / w >= DST_W * 3 / 5;
            float v = solid ? 0.4f : (edge ? 0.9f : 0.08f + 0.04f * ((x / 4 + y / 4) & 1));
            px[i] = edge ? 0.15f : v;
            px[i + 1] = v;
            px[i + 2] = edge ? 0.75f : 0.2f;
            px[i + 3] = 1f;
        }
        return px;
    }

    /** Separable Lanczos-2 using official FsrEasuCon positions. A shape check, not the EASU accumulation. */
    private static float[] lanczosReference(float[] src) {
        float[] out = new float[DST_W * DST_H * 4];
        float[] con0 = new float[4], con1 = new float[4], con2 = new float[4], con3 = new float[4];
        GlSuperResolutionRenderer.easuConstants(con0, con1, con2, con3, SRC_W, SRC_H, DST_W, DST_H);
        for (int y = 0; y < DST_H; y++) for (int x = 0; x < DST_W; x++) {
            float ppx = x * con0[0] + con0[2], ppy = y * con0[1] + con0[3];
            int fx = (int) Math.floor(ppx), fy = (int) Math.floor(ppy);
            float[] acc = new float[3];
            float weight = 0f;
            for (int oy = -1; oy <= 2; oy++) for (int ox = -1; ox <= 2; ox++) {
                if ((ox == -1 || ox == 2) && (oy == -1 || oy == 2)) continue;
                float w = lanczos(ox - (ppx - fx)) * lanczos(oy - (ppy - fy));
                int sx = clamp(fx + ox, 0, SRC_W - 1), sy = clamp(fy + oy, 0, SRC_H - 1);
                for (int c = 0; c < 3; c++) acc[c] += src[(sy * SRC_W + sx) * 4 + c] * w;
                weight += w;
            }
            for (int c = 0; c < 3; c++) out[(y * DST_W + x) * 4 + c] = weight == 0f ? 0f : acc[c] / weight;
            out[(y * DST_W + x) * 4 + 3] = 1f;
        }
        return out;
    }
    private static float lanczos(float d) {
        d = Math.abs(d);
        if (d >= 2f) return 0f;
        if (d < 1e-5f) return 1f;
        double pi = Math.PI;
        return (float) (Math.sin(pi * d) * Math.sin(pi * d / 2.0) / (pi * d * pi * d / 2.0));
    }
    private static int clamp(int v, int lo, int hi) { return Math.max(lo, Math.min(hi, v)); }

    private static void checkEdge(float[] easu, float[] reference) {
        double edge = 0, count = 0;
        for (int y = 4; y < DST_H - 4; y++) {
            int x = DST_W * 3 / 5;
            for (int c = 0; c < 3; c++) {
                edge += Math.abs(easu[(y * DST_W + x) * 4 + c] - reference[(y * DST_W + x) * 4 + c]);
                count++;
            }
        }
        check(edge / count < 0.08, "edge ring does not match the Lanczos-position reference");
    }

    private static boolean flatUnchanged(float[] sharp, float[] easu) {
        return flatMax(sharp, easu) <= 0.03;
    }
    private static double flatMax(float[] sharp, float[] easu) {
        double max = 0;
        for (int y = 8; y < DST_H - 8; y++) for (int x = 8; x < DST_W / 3; x++) {
            for (int c = 0; c < 3; c++) max = Math.max(max, Math.abs(sharp[(y * DST_W + x) * 4 + c] - easu[(y * DST_W + x) * 4 + c]));
        }
        return max;
    }

    private static double nearestMae(float[] easu) {
        float[] src = pattern(SRC_W, SRC_H, false);
        double sum = 0; int n = 0;
        for (int y = 0; y < DST_H; y++) for (int x = 0; x < DST_W; x++) {
            int sx = Math.min(SRC_W - 1, x * SRC_W / DST_W), sy = Math.min(SRC_H - 1, y * SRC_H / DST_H);
            for (int c = 0; c < 3; c++) { sum += Math.abs(easu[(y * DST_W + x) * 4 + c] - src[(sy * SRC_W + sx) * 4 + c]); n++; }
        }
        return sum / n;
    }

    private static double mae(float[] a, float[] b) {
        double sum = 0; int n = 0;
        for (int i = 0; i < a.length; i += 4) for (int c = 0; c < 3; c++) { sum += Math.abs(a[i + c] - b[i + c]); n++; }
        return sum / n;
    }

    private static double maxDelta(float[] a, float[] b) {
        double max = 0;
        for (int i = 0; i < a.length; i += 4) for (int c = 0; c < 3; c++) max = Math.max(max, Math.abs(a[i + c] - b[i + c]));
        return max;
    }

    private static double mean(float[] px) {
        double sum = 0; int n = 0;
        for (int i = 0; i < px.length; i += 4) { sum += px[i]; n++; }
        return sum / n;
    }

    private static int colorTexture(int w, int h, boolean srgb) {
        int tex = GL11.glGenTextures();
        GL11.glBindTexture(GL11.GL_TEXTURE_2D, tex);
        GL11.glTexParameteri(GL11.GL_TEXTURE_2D, GL11.GL_TEXTURE_MIN_FILTER, GL11.GL_NEAREST);
        GL11.glTexParameteri(GL11.GL_TEXTURE_2D, GL11.GL_TEXTURE_MAG_FILTER, GL11.GL_NEAREST);
        GL11.glTexImage2D(GL11.GL_TEXTURE_2D, 0, srgb ? GL21.GL_SRGB8_ALPHA8 : GL11.GL_RGBA8, w, h, 0, GL11.GL_RGBA, GL11.GL_UNSIGNED_BYTE, 0L);
        return tex;
    }

    private static int depthTexture(int w, int h, float value) {
        int tex = GL11.glGenTextures();
        GL11.glBindTexture(GL11.GL_TEXTURE_2D, tex);
        GL11.glTexParameteri(GL11.GL_TEXTURE_2D, GL11.GL_TEXTURE_MIN_FILTER, GL11.GL_NEAREST);
        GL11.glTexParameteri(GL11.GL_TEXTURE_2D, GL11.GL_TEXTURE_MAG_FILTER, GL11.GL_NEAREST);
        GL11.glTexParameteri(GL11.GL_TEXTURE_2D, GL14.GL_TEXTURE_COMPARE_MODE, GL11.GL_NONE);
        FloatBuffer data = ByteBuffer.allocateDirect(w * h * 4).order(ByteOrder.nativeOrder()).asFloatBuffer();
        for (int i = 0; i < w * h; i++) data.put(value);
        data.flip();
        GL11.glTexImage2D(GL11.GL_TEXTURE_2D, 0, GL30.GL_DEPTH_COMPONENT32F, w, h, 0, GL11.GL_DEPTH_COMPONENT, GL11.GL_FLOAT, data);
        check(GL11.glGetTexLevelParameteri(GL11.GL_TEXTURE_2D, 0, GL11.GL_TEXTURE_INTERNAL_FORMAT) == GL30.GL_DEPTH_COMPONENT32F, "depth32f metadata");
        return tex;
    }

    private static void upload(int tex, float[] px, int w, int h) {
        ByteBuffer data = ByteBuffer.allocateDirect(px.length);
        for (float v : px) data.put((byte) Math.max(0, Math.min(255, Math.round(v * 255f))));
        data.flip();
        GL11.glBindTexture(GL11.GL_TEXTURE_2D, tex);
        GL11.glTexSubImage2D(GL11.GL_TEXTURE_2D, 0, 0, 0, w, h, GL11.GL_RGBA, GL11.GL_UNSIGNED_BYTE, data);
    }

    private static float[] read(int fbo, int tex, int w, int h) {
        GL30.glBindFramebuffer(GL30.GL_READ_FRAMEBUFFER, fbo);
        GL30.glFramebufferTexture2D(GL30.GL_READ_FRAMEBUFFER, GL30.GL_COLOR_ATTACHMENT0, GL11.GL_TEXTURE_2D, tex, 0);
        GL11.glReadBuffer(GL30.GL_COLOR_ATTACHMENT0);
        ByteBuffer data = ByteBuffer.allocateDirect(w * h * 4);
        GL11.glReadPixels(0, 0, w, h, GL11.GL_RGBA, GL11.GL_UNSIGNED_BYTE, data);
        float[] px = new float[w * h * 4];
        for (int i = 0; i < px.length; i++) px[i] = (data.get(i) & 255) / 255f;
        return px;
    }

    private static float depthAt(int fbo, int tex, int x, int y) {
        GL30.glBindFramebuffer(GL30.GL_READ_FRAMEBUFFER, fbo);
        GL30.glFramebufferTexture2D(GL30.GL_READ_FRAMEBUFFER, GL30.GL_DEPTH_ATTACHMENT, GL11.GL_TEXTURE_2D, tex, 0);
        FloatBuffer data = ByteBuffer.allocateDirect(4).order(ByteOrder.nativeOrder()).asFloatBuffer();
        GL11.glReadPixels(x, y, 1, 1, GL11.GL_DEPTH_COMPONENT, GL11.GL_FLOAT, data);
        GL30.glFramebufferTexture2D(GL30.GL_READ_FRAMEBUFFER, GL30.GL_DEPTH_ATTACHMENT, GL11.GL_TEXTURE_2D, 0, 0);
        return data.get(0);
    }

    private static void createUserState() {
        userVao = GL30.glGenVertexArrays();
        userFbo = GL30.glGenFramebuffers();
        userProgram = GL20.glCreateProgram();
        int vs = GL20.glCreateShader(GL20.GL_VERTEX_SHADER), fs = GL20.glCreateShader(GL20.GL_FRAGMENT_SHADER);
        GL20.glShaderSource(vs, "#version 330 core\nvoid main(){gl_Position=vec4(0,0,0,1);}");
        GL20.glShaderSource(fs, "#version 330 core\nout vec4 color;void main(){color=vec4(1);}");
        GL20.glCompileShader(vs); GL20.glCompileShader(fs);
        GL20.glAttachShader(userProgram, vs); GL20.glAttachShader(userProgram, fs); GL20.glLinkProgram(userProgram);
        check(GL20.glGetProgrami(userProgram, GL20.GL_LINK_STATUS) != 0, "valid external program");
        GL20.glDeleteShader(vs); GL20.glDeleteShader(fs);
        GL30.glBindFramebuffer(GL30.GL_FRAMEBUFFER, userFbo);
        for (int i = 0; i < 4; i++) {
            userTextures[i] = GL11.glGenTextures();
            GL11.glBindTexture(GL11.GL_TEXTURE_2D, userTextures[i]);
            GL11.glTexParameteri(GL11.GL_TEXTURE_2D, GL11.GL_TEXTURE_MIN_FILTER, GL11.GL_NEAREST);
            GL11.glTexImage2D(GL11.GL_TEXTURE_2D, 0, GL11.GL_RGBA8, 8, 8, 0, GL11.GL_RGBA, GL11.GL_UNSIGNED_BYTE, 0L);
            if (i < 2) GL30.glFramebufferTexture2D(GL30.GL_FRAMEBUFFER, GL30.GL_COLOR_ATTACHMENT0 + i, GL11.GL_TEXTURE_2D, userTextures[i], 0);
            userSamplers[i] = GL33.glGenSamplers();
            GL33.glSamplerParameteri(userSamplers[i], GL11.GL_TEXTURE_MIN_FILTER, GL11.GL_NEAREST);
        }
        GL20.glDrawBuffers(new int[] {GL30.GL_COLOR_ATTACHMENT0, GL30.GL_COLOR_ATTACHMENT1});
        GL11.glReadBuffer(GL30.GL_COLOR_ATTACHMENT1);
    }

    private static void bindUserState() {
        GL30.glBindFramebuffer(GL30.GL_FRAMEBUFFER, userFbo);
        GL30.glBindVertexArray(userVao);
        GL20.glUseProgram(userProgram);
        GL11.glViewport(3, 4, 20, 12);
        GL11.glScissor(1, 2, 9, 8);
        GL11.glEnable(GL11.GL_SCISSOR_TEST);
        GL11.glEnable(GL11.GL_DEPTH_TEST);
        GL11.glDepthMask(false);
        GL30.glEnablei(GL11.GL_BLEND, 1);
        GL30.glColorMaski(0, false, true, false, true);
        GL30.glColorMaski(1, false, true, true, false);
        GL11.glEnable(GL30.GL_FRAMEBUFFER_SRGB);
        for (int i = 0; i < 4; i++) {
            GL13.glActiveTexture(GL13.GL_TEXTURE0 + i);
            GL11.glBindTexture(GL11.GL_TEXTURE_2D, userTextures[i]);
            GL33.glBindSampler(i, userSamplers[i]);
        }
        GL13.glActiveTexture(GL13.GL_TEXTURE1);
        GL11.glPixelStorei(GL11.GL_UNPACK_ALIGNMENT, 1);
        GL11.glPixelStorei(GL11.GL_PACK_ALIGNMENT, 2);
    }

    private static int[] snapshot() {
        int[] result = new int[48]; int p = 0;
        for (int name : new int[] {GL30.GL_READ_FRAMEBUFFER_BINDING, GL30.GL_DRAW_FRAMEBUFFER_BINDING,
                GL11.GL_READ_BUFFER, GL20.GL_DRAW_BUFFER0, GL20.GL_DRAW_BUFFER1, GL20.GL_CURRENT_PROGRAM,
                GL30.GL_VERTEX_ARRAY_BINDING, GL13.GL_ACTIVE_TEXTURE, GL21.GL_PIXEL_UNPACK_BUFFER_BINDING,
                GL21.GL_PIXEL_PACK_BUFFER_BINDING, GL11.GL_UNPACK_ALIGNMENT, GL11.GL_PACK_ALIGNMENT}) result[p++] = GL11.glGetInteger(name);
        int active = GL11.glGetInteger(GL13.GL_ACTIVE_TEXTURE);
        for (int i = 0; i < 4; i++) {
            GL13.glActiveTexture(GL13.GL_TEXTURE0 + i);
            result[p++] = GL11.glGetInteger(GL11.GL_TEXTURE_BINDING_2D);
            result[p++] = GL11.glGetInteger(GL33.GL_SAMPLER_BINDING);
        }
        GL13.glActiveTexture(active);
        int[] box = new int[4];
        GL11.glGetIntegerv(GL11.GL_VIEWPORT, box); for (int v : box) result[p++] = v;
        GL11.glGetIntegerv(GL11.GL_SCISSOR_BOX, box); for (int v : box) result[p++] = v;
        for (int cap : new int[] {GL11.GL_SCISSOR_TEST, GL11.GL_DEPTH_TEST, GL11.GL_CULL_FACE, GL30.GL_FRAMEBUFFER_SRGB}) result[p++] = GL11.glIsEnabled(cap) ? 1 : 0;
        result[p++] = GL11.glGetBoolean(GL11.GL_DEPTH_WRITEMASK) ? 1 : 0;
        for (int i = 0; i < 2; i++) {
            result[p++] = GL30.glIsEnabledi(GL11.GL_BLEND, i) ? 1 : 0;
            ByteBuffer mask = ByteBuffer.allocateDirect(4);
            GL30.glGetBooleani_v(GL11.GL_COLOR_WRITEMASK, i, mask);
            for (int j = 0; j < 4; j++) result[p++] = mask.get(j);
        }
        check(p <= result.length, "snapshot size " + p);
        return Arrays.copyOf(result, p);
    }

    private static void same(int[] expected, String step) {
        check(Arrays.equals(expected, snapshot()), step + " leaked GL state");
        int error = GL11.glGetError();
        check(error == GL11.GL_NO_ERROR, step + " GL error=0x" + Integer.toHexString(error));
    }

    private static long used() {
        Runtime rt = Runtime.getRuntime();
        return rt.totalMemory() - rt.freeMemory();
    }

    private static void check(boolean ok, String message) { if (!ok) throw new AssertionError(message); }
}
