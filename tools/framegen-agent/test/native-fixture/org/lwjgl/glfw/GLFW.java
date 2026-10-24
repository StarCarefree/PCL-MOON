package org.lwjgl.glfw;

/** Present so the transformer can recognise the real wait call. */
public final class GLFW {
    private GLFW() { }
    public static void glfwWaitEventsTimeout(double timeout) { }
    public static double glfwGetTime() { return 0; }
}
