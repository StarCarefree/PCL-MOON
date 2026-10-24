package pclmoon.framegen.render;

import org.lwjgl.glfw.GLFW;
import org.lwjgl.glfw.GLFWErrorCallback;
import org.lwjgl.opengl.*;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.Arrays;

/** Real GL core-profile image/state regression. CPU pixels/readback exist only in this test. */
public final class GlInterpolatorSmoke {
    private static final int W = 160, H = 90, SHIFT = Integer.getInteger("pcl.framegen.test.shift", 8);
    private static int userFbo, userVao;
    private static final int[] userTextures = new int[4], userSamplers = new int[4];
    private static boolean gl33;

    public static void main(String[] args) {
        GLFWErrorCallback callback = GLFWErrorCallback.createPrint(System.err).set();
        if (!GLFW.glfwInit()) throw new AssertionError("GLFW unavailable");
        long window = 0;
        GlInterpolator interpolator = null;
        try {
            GLFW.glfwWindowHint(GLFW.GLFW_VISIBLE, GLFW.GLFW_FALSE);
            GLFW.glfwWindowHint(GLFW.GLFW_CONTEXT_VERSION_MAJOR, 3);
            GLFW.glfwWindowHint(GLFW.GLFW_CONTEXT_VERSION_MINOR, Integer.getInteger("pcl.framegen.test.glMinor", 2));
            GLFW.glfwWindowHint(GLFW.GLFW_OPENGL_PROFILE, GLFW.GLFW_OPENGL_CORE_PROFILE);
            GLFW.glfwWindowHint(GLFW.GLFW_DOUBLEBUFFER, GLFW.GLFW_TRUE);
            window = GLFW.glfwCreateWindow(W, H, "PCL frame interpolation test", 0, 0);
            check(window != 0, "no core-profile test window");
            GLFW.glfwMakeContextCurrent(window);
            gl33 = GL.createCapabilities().OpenGL33;
            System.out.println("GPU: " + GL11.glGetString(GL11.GL_VENDOR) + " / " + GL11.glGetString(GL11.GL_RENDERER)
                + " / " + GL11.glGetString(GL11.GL_VERSION));
            GLUtil.setupDebugMessageCallback(System.err);
            createUserState();
            int unpack = GL15.glGenBuffers();
            GL15.glBindBuffer(GL21.GL_PIXEL_UNPACK_BUFFER, unpack);
            GL15.glBufferData(GL21.GL_PIXEL_UNPACK_BUFFER, 16L, GL15.GL_STATIC_DRAW);
            interpolator = new GlInterpolator(window);
            interpolator.configure(160, Integer.getInteger("pcl.framegen.test.quality", 1), 0.22f, 8);
            paint(0, false);
            bindUserState();
            int[] saved = snapshot();
            check(!interpolator.capture(), "first real frame must be cold");
            same(saved, "first capture");
            paint(SHIFT, false);
            bindUserState();
            GL11.glEnable(GL30.GL_RASTERIZER_DISCARD);
            saved = snapshot();
            check(interpolator.capture(), "second real frame must have history");
            same(saved, "second capture");
            interpolator.drawIntermediate();
            same(saved, "intermediate pass");
            ByteBuffer pixels = readBack();
            double actualError = error(pixels, SHIFT / 2), currentError = error(pixels, SHIFT);
            double crossfadeError = crossfadeError();
            check(actualError < 0.035, "not at the true halfway position: MAE=" + actualError);
            check(currentError > actualError * 2.5, "merely repeating the current real frame");
            check(actualError < crossfadeError * 0.45, "not better than a plain crossfade");
            for (float alpha : new float[] {0.25f, 0.75f}) {
                bindUserState(); saved = snapshot();
                interpolator.drawInterpolated(alpha);
                same(saved, "alpha " + alpha);
                double alphaError = error(readBack(), Math.round(SHIFT * alpha));
                check(alphaError < 0.035, "incorrect fractional motion alpha=" + alpha + " MAE=" + alphaError);
            }
            bindUserState();
            saved = snapshot();
            interpolator.drawCurrent();
            same(saved, "current-frame restore");
            check(error(readBack(), SHIFT) < 0.005, "current-frame pixels changed");
            // A query owned by another renderer must neither be ended nor replaced.
            if (gl33) {
                int query = GL15.glGenQueries();
                GL15.glBeginQuery(GL33.GL_TIME_ELAPSED, query);
                bindUserState();
                interpolator.drawIntermediate();
                check(GL15.glGetQueryi(GL33.GL_TIME_ELAPSED, GL15.GL_CURRENT_QUERY) == query, "stole another renderer's timer query");
                GL15.glEndQuery(GL33.GL_TIME_ELAPSED);
                GL15.glDeleteQueries(query);
            }
            // Abrupt cuts should use the current frame, not an old/new mixture.
            interpolator.resetHistory();
            paint(0, true);
            check(!interpolator.capture(), "reset must invalidate history");
            paint(-1, true);
            check(interpolator.capture(), "cut fixture warm-up");
            interpolator.drawIntermediate();
            ByteBuffer cut = readBack();
            check((cut.get((H / 2 * W + W / 2) * 4) & 255) > 245, "scene cut retained old black image");
            GLFW.glfwSetWindowSize(window, 200, 100);
            GLFW.glfwPollEvents();
            bindUserState();
            saved = snapshot();
            check(!interpolator.capture(), "resize must invalidate history");
            same(saved, "resize");
            interpolator.close();
            interpolator.close();
            same(saved, "idempotent close");
            GL15.glBindBuffer(GL21.GL_PIXEL_UNPACK_BUFFER, 0); GL15.glDeleteBuffers(unpack);
            check(GL11.glGetError() == GL11.GL_NO_ERROR, "OpenGL error leaked");
            System.out.println("PASS GPU halfway MAE=" + actualError + ", current MAE=" + currentError
                + ", crossfade MAE=" + crossfadeError + "; core state/MRT/query/cut/resize/close");
        } finally {
            if (interpolator != null) interpolator.close();
            if (window != 0) GLFW.glfwDestroyWindow(window);
            GLFW.glfwTerminate();
            callback.free();
        }
    }
    private static float value(int x, int y, int channel) {
        return (float) (0.5 + 0.23 * Math.sin(x * (0.27 + channel * 0.03) + y * 0.11)
            + 0.20 * Math.sin(x * 0.17 - y * (0.23 + channel * 0.04)));
    }
    private static void paint(int shift, boolean solid) {
        GL30.glBindFramebuffer(GL30.GL_FRAMEBUFFER, 0);
        GL11.glDrawBuffer(GL11.GL_BACK);
        GL11.glDisable(GL30.GL_RASTERIZER_DISCARD);
        GL30.glColorMaski(0, true, true, true, true);
        GL11.glDisable(GL30.GL_FRAMEBUFFER_SRGB);
        GL11.glDisable(GL11.GL_DITHER);
        GL11.glEnable(GL11.GL_SCISSOR_TEST);
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) {
            GL11.glScissor(x, y, 1, 1);
            if (solid) { float v = shift < 0 ? 1 : 0; GL11.glClearColor(v, v, v, 1); }
            else GL11.glClearColor(value(x - shift, y, 0), value(x - shift, y, 1), value(x - shift, y, 2), 1);
            GL11.glClear(GL11.GL_COLOR_BUFFER_BIT);
        }
        GL11.glDisable(GL11.GL_SCISSOR_TEST);
    }
    private static ByteBuffer readBack() {
        GL30.glBindFramebuffer(GL30.GL_READ_FRAMEBUFFER, 0);
        GL11.glReadBuffer(GL11.GL_BACK);
        ByteBuffer pixels = ByteBuffer.allocateDirect(W * H * 4);
        GL11.glReadPixels(0, 0, W, H, GL11.GL_RGBA, GL11.GL_UNSIGNED_BYTE, pixels);
        return pixels;
    }
    private static double error(ByteBuffer pixels, int shift) {
        double sum = 0; int count = 0;
        for (int y = 12; y < H - 12; y++) for (int x = 20; x < W - 20; x++) for (int c = 0; c < 3; c++) {
            sum += Math.abs((pixels.get((y * W + x) * 4 + c) & 255) / 255.0 - value(x - shift, y, c)); count++;
        }
        return sum / count;
    }
    private static double crossfadeError() {
        double sum = 0; int count = 0;
        for (int y = 12; y < H - 12; y++) for (int x = 20; x < W - 20; x++) for (int c = 0; c < 3; c++) {
            sum += Math.abs((value(x, y, c) + value(x - SHIFT, y, c)) * 0.5 - value(x - SHIFT / 2, y, c)); count++;
        }
        return sum / count;
    }
    private static void createUserState() {
        userVao = GL30.glGenVertexArrays();
        userFbo = GL30.glGenFramebuffers();
        GL30.glBindFramebuffer(GL30.GL_FRAMEBUFFER, userFbo);
        for (int i = 0; i < 4; i++) {
            userTextures[i] = GL11.glGenTextures();
            GL11.glBindTexture(GL11.GL_TEXTURE_2D, userTextures[i]);
            GL11.glTexImage2D(GL11.GL_TEXTURE_2D, 0, GL11.GL_RGBA8, W, H, 0, GL11.GL_RGBA, GL11.GL_UNSIGNED_BYTE, 0L);
            if (i < 2) GL30.glFramebufferTexture2D(GL30.GL_FRAMEBUFFER, GL30.GL_COLOR_ATTACHMENT0 + i, GL11.GL_TEXTURE_2D, userTextures[i], 0);
            if (gl33) userSamplers[i] = GL33.glGenSamplers();
        }
        GL20.glDrawBuffers(new int[] {GL30.GL_COLOR_ATTACHMENT0, GL30.GL_COLOR_ATTACHMENT1});
        GL11.glReadBuffer(GL30.GL_COLOR_ATTACHMENT1);
        check(GL30.glCheckFramebufferStatus(GL30.GL_FRAMEBUFFER) == GL30.GL_FRAMEBUFFER_COMPLETE, "fixture FBO");
    }
    private static void bindUserState() {
        GL30.glBindFramebuffer(GL30.GL_FRAMEBUFFER, userFbo);
        GL30.glBindVertexArray(userVao);
        GL11.glViewport(3, 4, 110, 72);
        GL11.glEnable(GL11.GL_SCISSOR_TEST);
        GL11.glEnable(GL11.GL_DEPTH_TEST);
        GL30.glEnablei(GL11.GL_BLEND, 1);
        GL30.glColorMaski(0, false, true, false, true);
        GL30.glColorMaski(1, false, true, true, false);
        for (int i = 0; i < 4; i++) {
            GL13.glActiveTexture(GL13.GL_TEXTURE0 + i);
            GL11.glBindTexture(GL11.GL_TEXTURE_2D, userTextures[i]);
            if (gl33) GL33.glBindSampler(i, userSamplers[i]);
        }
        GL13.glActiveTexture(GL13.GL_TEXTURE0 + 5);
    }
    private static int[] snapshot() {
        int[] result = new int[37]; int p = 0;
        for (int name : new int[] {GL30.GL_READ_FRAMEBUFFER_BINDING, GL30.GL_DRAW_FRAMEBUFFER_BINDING,
                GL11.GL_READ_BUFFER, GL20.GL_DRAW_BUFFER0, GL20.GL_DRAW_BUFFER1, GL20.GL_CURRENT_PROGRAM,
                GL30.GL_VERTEX_ARRAY_BINDING, GL13.GL_ACTIVE_TEXTURE, GL21.GL_PIXEL_UNPACK_BUFFER_BINDING}) result[p++] = GL11.glGetInteger(name);
        int active = GL11.glGetInteger(GL13.GL_ACTIVE_TEXTURE);
        for (int i = 0; i < 4; i++) {
            GL13.glActiveTexture(GL13.GL_TEXTURE0 + i);
            result[p++] = GL11.glGetInteger(GL11.GL_TEXTURE_BINDING_2D);
            result[p++] = gl33 ? GL11.glGetInteger(GL33.GL_SAMPLER_BINDING) : 0;
        }
        GL13.glActiveTexture(active);
        int[] viewport = new int[4]; GL11.glGetIntegerv(GL11.GL_VIEWPORT, viewport);
        for (int v : viewport) result[p++] = v;
        for (int cap : new int[] {GL11.GL_SCISSOR_TEST, GL11.GL_DEPTH_TEST, GL11.GL_CULL_FACE, GL30.GL_FRAMEBUFFER_SRGB, GL30.GL_RASTERIZER_DISCARD}) result[p++] = GL11.glIsEnabled(cap) ? 1 : 0;
        for (int i = 0; i < 2; i++) {
            result[p++] = GL30.glIsEnabledi(GL11.GL_BLEND, i) ? 1 : 0;
            ByteBuffer mask = ByteBuffer.allocateDirect(4);
            GL30.glGetBooleani_v(GL11.GL_COLOR_WRITEMASK, i, mask);
            for (int j = 0; j < 4; j++) result[p++] = mask.get(j);
        }
        check(p <= result.length, "snapshot size");
        return result;
    }
    private static void same(int[] expected, String step) {
        check(Arrays.equals(expected, snapshot()), step + " leaked game GL state");
        int error = GL11.glGetError();
        check(error == GL11.GL_NO_ERROR, step + " GL error=0x" + Integer.toHexString(error));
    }
    private static void check(boolean ok, String message) { if (!ok) throw new AssertionError(message); }
}
