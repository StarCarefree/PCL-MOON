package org.lwjgl.glfw;

/** No graphics/native window required: exercise the real transformed LWJGL API boundary. */
public final class GLFW {
    public static int swaps;
    public static void glfwSwapBuffers(long window) { swaps++; }
    public static void glfwDestroyWindow(long window) { }
    public static void glfwTerminate() { }
}
