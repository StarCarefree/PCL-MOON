package pclmoon.framegen.render;

import java.nio.ByteBuffer;
import java.io.File;
import java.awt.image.BufferedImage;
import javax.imageio.ImageIO;
import org.lwjgl.glfw.GLFW;
import org.lwjgl.opengl.*;

/** High-frequency scene + stationary thin HUD regression; CPU upload/readback are test-only. */
public final class GlSceneRegression {
    private static final int W = 640, H = 360, SHIFT = 16;
    private static int tex, fbo;
    public static void main(String[] args) throws Exception {
        if (!GLFW.glfwInit()) throw new AssertionError("GLFW unavailable");
        long window = 0; GlInterpolator renderer = null;
        try {
            GLFW.glfwWindowHint(GLFW.GLFW_VISIBLE, GLFW.GLFW_FALSE);
            GLFW.glfwWindowHint(GLFW.GLFW_CONTEXT_VERSION_MAJOR, 3);
            GLFW.glfwWindowHint(GLFW.GLFW_CONTEXT_VERSION_MINOR, 3);
            GLFW.glfwWindowHint(GLFW.GLFW_OPENGL_PROFILE, GLFW.GLFW_OPENGL_CORE_PROFILE);
            window = GLFW.glfwCreateWindow(W, H, "framegen-scene-regression", 0, 0);
            if (window == 0) throw new AssertionError("test window");
            GLFW.glfwMakeContextCurrent(window); GL.createCapabilities();
            tex = GL11.glGenTextures(); fbo = GL30.glGenFramebuffers();
            renderer = new GlInterpolator(window); renderer.configure(160, 1, .22f, 8);
            upload(image(0)); renderer.capture();
            upload(image(1)); if (!renderer.capture()) throw new AssertionError("history");
            renderer.drawInterpolated(.5f);
            ByteBuffer actual = ByteBuffer.allocateDirect(W * H * 4);
            GL30.glBindFramebuffer(GL30.GL_READ_FRAMEBUFFER, 0); GL11.glReadBuffer(GL11.GL_BACK);
            GL11.glReadPixels(0, 0, W, H, GL11.GL_RGBA, GL11.GL_UNSIGNED_BYTE, actual);
            ByteBuffer expected = image(.5f), current = image(1);
            double stable = 0, world = 0, crossfade = 0; int ns = 0, nw = 0, damaged = 0;
            ByteBuffer previous = image(0);
            for (int y = 20; y < H - 8; y++) for (int x = 32; x < W - 32; x++) {
                boolean hud = hud(x, y); // Identical in both true frames, must remain pixel-stable.
                boolean worldInterior = !hud && y < H - 78 && (y < 110 || y > 270 || x < 110 || x > 360);
                double pixelError = 0;
                for (int c = 0; c < 3; c++) {
                    int p = (y * W + x) * 4 + c;
                    double e = Math.abs((actual.get(p) & 255) - (expected.get(p) & 255)) / 255.0;
                    pixelError += e;
                    if (hud) { stable += e; ns++; }
                    if (worldInterior) {
                        world += e; nw++;
                        crossfade += Math.abs(((previous.get(p) & 255) + (current.get(p) & 255)) * .5 - (expected.get(p) & 255)) / 255.0;
                    }
                }
                if (hud && pixelError > .10) damaged++;
            }
            stable /= ns; world /= nw; crossfade /= nw;
            System.out.println("SCENE staticHudMAE=" + stable + " damagedHudPixels=" + damaged + " worldMAE=" + world + " crossfadeMAE=" + crossfade);
            String output = System.getProperty("framegen.test.out");
            if (output != null) { save(actual, output + "-actual.png"); save(expected, output + "-expected.png"); }
            if (stable > .003 || damaged > 4) throw new AssertionError("stationary HUD is warped/blurred");
            if (world > .045 || world > crossfade * .5) throw new AssertionError("fragmented high-frequency world motion");
            if (GL11.glGetError() != GL11.GL_NO_ERROR) throw new AssertionError("GL state error");
            System.out.println("PASS detailed scene / stationary HUD");
        } finally {
            if (renderer != null) renderer.close();
            if (window != 0) { GL11.glDeleteTextures(tex); GL30.glDeleteFramebuffers(fbo); GLFW.glfwDestroyWindow(window); }
            GLFW.glfwTerminate();
        }
    }
    private static boolean hud(int x, int y) {
        return (x >= 36 && x < 300 && y >= H - 60 && y < H - 18)
            || (Math.abs(x - W / 2) <= 1 && Math.abs(y - H / 2) < 9)
            || (Math.abs(y - H / 2) <= 1 && Math.abs(x - W / 2) < 9);
    }
    private static int hash(int x, int y) { int v = x * 374761393 + y * 668265263; v = (v ^ (v >>> 13)) * 1274126177; return v ^ (v >>> 16); }
    private static ByteBuffer image(float t) {
        ByteBuffer pixels = ByteBuffer.allocateDirect(W * H * 4);
        int shift = Math.round(SHIFT * t), object = Math.round(-24 * t);
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) {
            int sx = x - shift, brick = hash(Math.floorDiv(sx, 12), y / 10);
            for (int c = 0; c < 3; c++) {
                int v = 65 + ((brick >>> (c * 5)) & 63) + (int) (45 * Math.sin(sx * .072 + y * .037 + c));
                if (Math.floorMod(sx, 12) == 0 || y % 10 == 0) v = 35;
                if (x >= 160 + object && x < 310 + object && y >= 125 && y < 250) v = c == 1 ? 140 + ((hash(x - object, y) >>> 4) & 31) : 25;
                if (hud(x, y)) v = y >= H - 60 ? (((x / 3 + y / 2) % 7 == 0 || y % 11 == 0) ? 245 : 24) : 250;
                pixels.put((byte) Math.max(0, Math.min(255, v)));
            }
            pixels.put((byte) 255);
        }
        pixels.flip(); return pixels;
    }
    private static void upload(ByteBuffer pixels) {
        GL13.glActiveTexture(GL13.GL_TEXTURE0); GL11.glBindTexture(GL11.GL_TEXTURE_2D, tex);
        GL11.glTexImage2D(GL11.GL_TEXTURE_2D, 0, GL11.GL_RGBA8, W, H, 0, GL11.GL_RGBA, GL11.GL_UNSIGNED_BYTE, pixels);
        GL30.glBindFramebuffer(GL30.GL_READ_FRAMEBUFFER, fbo);
        GL30.glFramebufferTexture2D(GL30.GL_READ_FRAMEBUFFER, GL30.GL_COLOR_ATTACHMENT0, GL11.GL_TEXTURE_2D, tex, 0);
        GL11.glReadBuffer(GL30.GL_COLOR_ATTACHMENT0);
        GL30.glBindFramebuffer(GL30.GL_DRAW_FRAMEBUFFER, 0); GL11.glDrawBuffer(GL11.GL_BACK);
        GL30.glBlitFramebuffer(0, 0, W, H, 0, 0, W, H, GL11.GL_COLOR_BUFFER_BIT, GL11.GL_NEAREST);
        GL30.glBindFramebuffer(GL30.GL_FRAMEBUFFER, 0);
    }
    private static void save(ByteBuffer pixels, String path) throws Exception {
        BufferedImage image = new BufferedImage(W, H, BufferedImage.TYPE_INT_RGB);
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) {
            int p = (y * W + x) * 4;
            image.setRGB(x, H - 1 - y, ((pixels.get(p) & 255) << 16) | ((pixels.get(p + 1) & 255) << 8) | (pixels.get(p + 2) & 255));
        }
        ImageIO.write(image, "png", new File(path));
    }
}
