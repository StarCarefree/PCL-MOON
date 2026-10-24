package pclmoon.framegen.bridge;

/** Bootstrap-shared SR requests and observed render status; no filesystem, GL or UI dependency. */
public final class UpscaleSettings {
    private static volatile Values values = new Values(Boolean.parseBoolean(System.getProperty("pclmoon.framegen.superResolution", "false")),
        number("renderScale", 67, 50, 100), number("upscaleSharpness", 20, 0, 100), 0);
    private static volatile Info info = new Info(false, "等待世界渲染适配", 0, 0, 0, 0);
    private UpscaleSettings() { }
    public static boolean enabled() { return values.enabled; }
    public static int scalePercent() { return values.scalePercent; }
    public static int sharpnessPercent() { return values.sharpnessPercent; }
    public static long revision() { return values.revision; }
    public static Values snapshot() { return values; }
    public static Info info() { return info; }
    public static boolean active() { return values.enabled && info.active; }
    public static String status() { return values.enabled ? info.state : "超分已关闭"; }
    public static synchronized void setEnabled(boolean enabled) { apply(enabled, values.scalePercent, values.sharpnessPercent); }
    public static synchronized void setScalePercent(int percent) { apply(values.enabled, percent, values.sharpnessPercent); }
    public static synchronized void setSharpnessPercent(int percent) { apply(values.enabled, values.scalePercent, percent); }
    public static synchronized void apply(boolean enabled, int scale, int sharpness) {
        scale = Math.max(50, Math.min(100, scale)); sharpness = Math.max(0, Math.min(100, sharpness));
        Values old = values;
        if (old.enabled == enabled && old.scalePercent == scale && old.sharpnessPercent == sharpness) return;
        values = new Values(enabled, scale, sharpness, old.revision + 1);
    }
    /** Published by render-thread adapters only; does not modify the requested configuration. */
    public static void runtime(boolean active, String state, int srcW, int srcH, int dstW, int dstH) {
        Info old = info;
        String text = state == null ? "超分状态不可用" : state;
        if (old.active == active && old.state.equals(text) && old.inputWidth == srcW && old.inputHeight == srcH
                && old.outputWidth == dstW && old.outputHeight == dstH) return;
        info = new Info(active, text, srcW, srcH, dstW, dstH);
    }
    private static int number(String name, int fallback, int min, int max) {
        return Math.max(min, Math.min(max, Integer.getInteger("pclmoon.framegen." + name, fallback)));
    }
    public static final class Values {
        public final boolean enabled;
        public final int scalePercent, sharpnessPercent;
        public final long revision;
        private Values(boolean enabled, int scale, int sharpness, long revision) {
            this.enabled = enabled; scalePercent = scale; sharpnessPercent = sharpness; this.revision = revision;
        }
    }
    public static final class Info {
        public final boolean active;
        public final String state;
        public final int inputWidth, inputHeight, outputWidth, outputHeight;
        private Info(boolean active, String state, int srcW, int srcH, int dstW, int dstH) {
            this.active = active; this.state = state; inputWidth = srcW; inputHeight = srcH; outputWidth = dstW; outputHeight = dstH;
        }
    }
}
