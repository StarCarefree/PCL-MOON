package org.lwjgl.opengl;

public final class Display {
    public static int swaps;
    public static void swapBuffers() { swaps++; }
    public static boolean failRelease;
    public static void releaseContext() {
        if (failRelease) throw new IllegalStateException("fixture release failure");
    }
    public static void destroy() { }
}
