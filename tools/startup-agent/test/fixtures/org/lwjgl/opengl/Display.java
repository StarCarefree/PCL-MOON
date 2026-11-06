package org.lwjgl.opengl;

public final class Display {
    public static int swaps;
    public static void swapBuffers() { swaps++; }
    public static void destroy() { }
}
