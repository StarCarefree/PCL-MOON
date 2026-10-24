package net.minecraft;

/** Synthetic 1.21.11 intermediary run loop. Not a Minecraft class. */
public class class_310 {
    public int ticks;
    public int renders;
    public boolean threw;

    public void method_1514() {
        int guard = 0;
        while (guard++ < 2) {
            method_1523(guard == 1);
        }
    }

    private void method_1523(boolean first) {
        renders++;
        if (first) ticks++;
        if (threw) throw new IllegalStateException("frame");
    }

    public int method_1574() {
        ticks++;
        return ticks;
    }
}
