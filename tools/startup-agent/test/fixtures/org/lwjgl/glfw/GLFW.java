package org.lwjgl.glfw;

/** 无需图形窗口，仍通过实际转换的 LWJGL API 边界执行测试。 */
public final class GLFW {
    public static int swaps;
    public static void glfwSwapBuffers(long window) { swaps++; }
    public static boolean failDetach;
    public static void glfwMakeContextCurrent(long window) {
        if (window == 0 && failDetach) throw new IllegalArgumentException("fixture detach failure");
    }
    public static void glfwDestroyWindow(long window) { }
    public static void glfwTerminate() { }
}
