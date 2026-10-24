package com.mojang.blaze3d.systems;

/** Synthetic 1.21.11 limiter. Not a Minecraft class. */
public class RenderSystem {
    public static double lastDrawTime;
    public static int waits;
    public static int lastFps = -1;
    public static int originalCalls;
    public static boolean failLimit;

    public static void limitDisplayFPS(int fps) {
        originalCalls++;
        if (failLimit) throw new IllegalArgumentException("original limiter failure");
        lastFps = fps;
        double deadline = lastDrawTime + 1.0 / fps;
        double now = glfwGetTime();
        while (now < deadline) {
            org.lwjgl.glfw.GLFW.glfwWaitEventsTimeout(deadline - now);
            now = glfwGetTime();
        }
        lastDrawTime = now;
    }

    public static void flipFrame() {
        waits++;
    }

    private static double glfwGetTime() { return lastDrawTime + 1.0; }
}
