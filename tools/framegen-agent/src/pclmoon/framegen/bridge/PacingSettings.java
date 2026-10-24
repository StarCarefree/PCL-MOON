package pclmoon.framegen.bridge;

/** One immutable effective snapshot for the independent presentation pacer. */
public final class PacingSettings {
    private static volatile Values values = new Values(number("fpsLimit", 0, 0, 999),
        number("waitMode", 1, 0, 2), Boolean.parseBoolean(System.getProperty("pclmoon.framegen.telemetry", "true")), 0);
    private static volatile String rendererName = "";
    private PacingSettings() { }
    public static int fpsLimit() { return values.fpsLimit; }
    public static int waitMode() { return values.waitMode; }
    public static boolean telemetry() { return values.telemetry; }
    public static long revision() { return values.revision; }
    public static Values snapshot() { return values; }
    public static String rendererName() { return rendererName; }
    public static void setRendererName(String value) { rendererName = value == null ? "" : value; }
    public static synchronized void setFpsLimit(int value) { apply(value, values.waitMode, values.telemetry); }
    public static synchronized void setWaitMode(int value) { apply(values.fpsLimit, value, values.telemetry); }
    public static synchronized void setTelemetry(boolean value) { apply(values.fpsLimit, values.waitMode, value); }
    public static synchronized void apply(int fps, int mode, boolean telemetry) {
        int cap = Math.max(0, Math.min(999, fps)), wait = Math.max(0, Math.min(2, mode));
        Values old = values;
        if (old.fpsLimit == cap && old.waitMode == wait && old.telemetry == telemetry) return;
        values = new Values(cap, wait, telemetry, old.revision + 1);
    }
    private static int number(String name, int fallback, int min, int max) {
        return Math.max(min, Math.min(max, Integer.getInteger("pclmoon.framegen." + name, fallback)));
    }
    public static final class Values {
        public final int fpsLimit, waitMode;
        public final boolean telemetry;
        public final long revision;
        private Values(int fps, int mode, boolean telemetry, long revision) {
            fpsLimit = fps; waitMode = mode; this.telemetry = telemetry; this.revision = revision;
        }
    }
}
