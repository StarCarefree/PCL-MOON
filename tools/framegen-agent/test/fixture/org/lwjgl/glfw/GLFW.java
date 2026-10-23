package org.lwjgl.glfw;

/** Deliberately mirrors LWJGL's non-native public wrappers, without a display or native library. */
public final class GLFW {
    public static int swaps, destroys;
    public static boolean nativeFailure;
    public static long context = 1;
    public static int focused = 1;
    public static void glfwSwapBuffers(long window) {
        swaps++;
        if (nativeFailure) throw new IllegalStateException("synthetic native failure");
    }
    public static void glfwDestroyWindow(long window) { destroys++; }
    public static long glfwGetCurrentContext() { return context; }
    public static int glfwGetWindowAttrib(long window, int attribute) {
        return attribute == 0x00020002 ? 0 : attribute == 0x00020001 ? focused : 1;
    }
}
