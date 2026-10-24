package pclmoon.framegen.bridge;

/** Bootstrap-shared session settings. No options.txt writes or forced JVM/GC changes. */
public final class FrameSettings {
    private static volatile boolean generation = Boolean.parseBoolean(System.getProperty("pclmoon.framegen.enabled", "true"));
    private static volatile boolean diagnostics = Boolean.getBoolean("pclmoon.framegen.diagnostics");
    private static volatile int outputFps = number("outputFps", 60, 30, 240);
    private static volatile int multiplier = number("multiplier", 2, 2, 4);
    private static volatile int quality = number("quality", 1, 0, 2);
    private static volatile int flowWidth = number("flowWidth", 160, 80, 320);
    private static volatile int gpuBudgetMs = number("gpuBudgetMs", 8, 2, 20);
    private static volatile int stallThresholdMs = number("stallThresholdMs", 150, 50, 500);
    private static volatile boolean adaptivePacing = flag("adaptivePacing", true);
    private static volatile boolean lowFpsProtection = flag("lowFpsProtection", true);
    private static volatile boolean nativeFrameControl = flag("nativeFrameControl", true);
    private static volatile boolean showFps = flag("showFps", false);
    private FrameSettings() { }
    public static boolean generation() { return generation; }
    public static boolean diagnostics() { return diagnostics; }
    public static int outputFps() { return outputFps; }
    public static int multiplier() { return multiplier; }
    public static int quality() { return quality; }
    public static int flowWidth() { return flowWidth; }
    public static int gpuBudgetMs() { return gpuBudgetMs; }
    public static int stallThresholdMs() { return stallThresholdMs; }
    public static boolean adaptivePacing() { return adaptivePacing; }
    public static boolean lowFpsProtection() { return lowFpsProtection; }
    public static boolean nativeFrameControl() { return nativeFrameControl; }
    public static boolean showFps() { return showFps; }
    public static void setGeneration(boolean value) { generation = value; }
    public static void setDiagnostics(boolean value) { diagnostics = value; }
    public static void setOutputFps(int value) { outputFps = clamp(value, 30, 240); }
    public static void setMultiplier(int value) { multiplier = clamp(value, 2, 4); }
    public static void setQuality(int value) { quality = clamp(value, 0, 2); }
    public static void setFlowWidth(int value) { flowWidth = clamp(value, 80, 320); }
    public static void setGpuBudgetMs(int value) { gpuBudgetMs = clamp(value, 2, 20); }
    public static void setStallThresholdMs(int value) { stallThresholdMs = clamp(value, 50, 500); }
    public static void setAdaptivePacing(boolean value) { adaptivePacing = value; }
    public static void setLowFpsProtection(boolean value) { lowFpsProtection = value; }
    public static void setNativeFrameControl(boolean value) { nativeFrameControl = value; }
    public static void setShowFps(boolean value) { showFps = value; }
    private static boolean flag(String name, boolean fallback) {
        return Boolean.parseBoolean(System.getProperty("pclmoon.framegen." + name, Boolean.toString(fallback)));
    }
    private static int number(String name, int fallback, int min, int max) {
        return clamp(Integer.getInteger("pclmoon.framegen." + name, fallback), min, max);
    }
    private static int clamp(int value, int min, int max) { return Math.max(min, Math.min(max, value)); }
}
