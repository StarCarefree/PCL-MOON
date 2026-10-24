package pclmoon.framegen.render;

import java.util.Arrays;
import org.lwjgl.glfw.GLFW;
import org.lwjgl.opengl.*;

/** Test-only component overhead, NOT Minecraft FPS. Completion waits never enter production. */
public final class SuperResolutionBenchmark {
    public static void main(String[] args) {
        int width = Integer.getInteger("bench.w", 1920), height = Integer.getInteger("bench.h", 1080);
        int frames = Integer.getInteger("bench.frames", 120), warmup = 40;
        if (!GLFW.glfwInit()) throw new AssertionError("GLFW unavailable");
        long window = 0;
        try {
            GLFW.glfwWindowHint(GLFW.GLFW_VISIBLE, GLFW.GLFW_FALSE);
            GLFW.glfwWindowHint(GLFW.GLFW_CONTEXT_VERSION_MAJOR, 3);
            GLFW.glfwWindowHint(GLFW.GLFW_CONTEXT_VERSION_MINOR, 3);
            GLFW.glfwWindowHint(GLFW.GLFW_OPENGL_PROFILE, GLFW.GLFW_OPENGL_CORE_PROFILE);
            window = GLFW.glfwCreateWindow(width, height, "super-resolution-overhead", 0, 0);
            if (window == 0) throw new AssertionError("GL3.3 window");
            GLFW.glfwMakeContextCurrent(window); GL.createCapabilities();
            System.out.println("SR BENCH " + GL11.glGetString(GL11.GL_RENDERER) + " " + width + "x" + height);
            for (int[] mode : new int[][] {{100, 0}, {100, 20}, {67, 20}, {50, 20}}) {
                int sw = mode[0] == 100 ? width : Math.max(2, (width * mode[0] / 100) & ~1);
                int sh = mode[0] == 100 ? height : Math.max(2, (height * mode[0] / 100) & ~1);
                int color = texture(sw, sh, false), depth = texture(sw, sh, true);
                int out = texture(width, height, false), outDepth = texture(width, height, true);
                int fbo = GL30.glGenFramebuffers();
                GL30.glBindFramebuffer(GL30.GL_FRAMEBUFFER, fbo);
                GL30.glFramebufferTexture2D(GL30.GL_FRAMEBUFFER, GL30.GL_COLOR_ATTACHMENT0, GL11.GL_TEXTURE_2D, color, 0);
                GL30.glFramebufferTexture2D(GL30.GL_FRAMEBUFFER, GL30.GL_DEPTH_ATTACHMENT, GL11.GL_TEXTURE_2D, depth, 0);
                GL11.glDrawBuffer(GL30.GL_COLOR_ATTACHMENT0);
                GL11.glClearColor(0.2f, 0.4f, 0.7f, 1f);
                GL11.glClear(GL11.GL_COLOR_BUFFER_BIT | GL11.GL_DEPTH_BUFFER_BIT);
                GL30.glBindFramebuffer(GL30.GL_FRAMEBUFFER, 0);
                GlSuperResolutionRenderer renderer = new GlSuperResolutionRenderer();
                try {
                    if (!renderer.prepare(width, height)) throw new AssertionError("FSR unavailable");
                    double[] cpu = new double[frames], completed = new double[frames];
                    for (int i = 0; i < warmup + frames; i++) {
                        GL11.glFinish();
                        long start = System.nanoTime();
                        if (!renderer.upscale(color, depth, out, outDepth, sw, sh, width, height, mode[1] / 100f))
                            throw new AssertionError("FSR unexpectedly fell back");
                        long submitted = System.nanoTime();
                        GL11.glFinish();
                        long done = System.nanoTime();
                        if (i >= warmup) {
                            cpu[i - warmup] = (submitted - start) / 1e6;
                            completed[i - warmup] = (done - start) / 1e6;
                        }
                    }
                    Arrays.sort(cpu); Arrays.sort(completed);
                    System.out.println("SR BENCH scale=" + mode[0] + " sharpness=" + mode[1] + " n=" + frames
                        + " cpuMedianMs=" + cpu[frames / 2] + " completedMedianMs=" + completed[frames / 2]
                        + " completedP95Ms=" + completed[Math.min(frames - 1, (int) (frames * 0.95))]);
                } finally {
                    renderer.close(); GL30.glDeleteFramebuffers(fbo);
                    GL11.glDeleteTextures(new int[] {color, depth, out, outDepth});
                }
            }
            if (GL11.glGetError() != GL11.GL_NO_ERROR) throw new AssertionError("benchmark GL error");
        } finally {
            if (window != 0) GLFW.glfwDestroyWindow(window);
            GLFW.glfwTerminate();
        }
    }
    private static int texture(int w, int h, boolean depth) {
        int id = GL11.glGenTextures(); GL11.glBindTexture(GL11.GL_TEXTURE_2D, id);
        GL11.glTexParameteri(GL11.GL_TEXTURE_2D, GL11.GL_TEXTURE_MIN_FILTER, GL11.GL_NEAREST);
        GL11.glTexParameteri(GL11.GL_TEXTURE_2D, GL11.GL_TEXTURE_MAG_FILTER, GL11.GL_NEAREST);
        GL11.glTexImage2D(GL11.GL_TEXTURE_2D, 0, depth ? GL30.GL_DEPTH_COMPONENT32F : GL11.GL_RGBA8,
            w, h, 0, depth ? GL11.GL_DEPTH_COMPONENT : GL11.GL_RGBA,
            depth ? GL11.GL_FLOAT : GL11.GL_UNSIGNED_BYTE, 0L);
        return id;
    }
}
