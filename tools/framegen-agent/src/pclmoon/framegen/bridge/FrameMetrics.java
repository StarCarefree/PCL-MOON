package pclmoon.framegen.bridge;

import java.util.Arrays;

/** Counts successful presentation submissions, not monitor scanout and never configured FPS x multiplier. */
public final class FrameMetrics {
    private static final long WINDOW = 1000000000L;
    private static final long[] real = new long[2048], generated = new long[2048];
    private static final double[] intervals = new double[256];
    private static int realIndex, generatedIndex, intervalIndex, intervalCount;
    private static long first, lastReal, lastPresentation, skipped;
    private static double gpuMs = -1, nativeMs = -1;
    private static String reason = "等待游戏画面", nativeHook = "未接入";
    private FrameMetrics() { }

    public static synchronized void realPresented(long now) {
        if (first == 0) first = now;
        recordInterval(now);
        lastReal = now;
        real[realIndex] = now;
        realIndex = (realIndex + 1) % real.length;
    }
    public static synchronized void generatedPresented(long now) {
        if (first == 0) first = now;
        recordInterval(now);
        generated[generatedIndex] = now;
        generatedIndex = (generatedIndex + 1) % generated.length;
    }
    public static synchronized void skip(String why) { skipped++; reason = why; }
    public static synchronized void status(String value) { reason = value; }
    private static void recordInterval(long now) {
        if (lastPresentation != 0 && now > lastPresentation) {
            intervals[intervalIndex] = (now - lastPresentation) / 1000000.0;
            intervalIndex = (intervalIndex + 1) % intervals.length;
            intervalCount = Math.min(intervalCount + 1, intervals.length);
        }
        lastPresentation = now;
    }
    public static synchronized void gpuMillis(double value) { if (Double.isFinite(value)) gpuMs = value; }
    public static synchronized void nativeMillis(double value) { if (Double.isFinite(value)) nativeMs = value; }
    public static synchronized void nativeHook(String value) { nativeHook = value; }
    public static Snapshot snapshot() { return snapshotAt(System.nanoTime()); }

    /** Deterministic clock seam used by the submission-counter regression. */
    public static synchronized Snapshot snapshotAt(long now) {
        double seconds = first == 0 ? 1 : Math.max(0.25, Math.min(1, (now - first) / 1000000000.0));
        double render = count(real, now) / seconds;
        double synth = count(generated, now) / seconds;
        double p95 = 0, low = 0;
        if (intervalCount > 0) {
            double[] sorted = Arrays.copyOf(intervals, intervalCount);
            Arrays.sort(sorted);
            p95 = sorted[Math.min(sorted.length - 1, (int) Math.ceil(sorted.length * 0.95) - 1)];
            int tail = Math.max(1, (int) Math.ceil(sorted.length * 0.01));
            double total = 0;
            for (int i = sorted.length - tail; i < sorted.length; i++) total += sorted[i];
            low = total > 0 ? 1000.0 * tail / total : 0;
        }
        // A stalled process does not keep advertising the last good FPS or 1% low.
        if (lastPresentation == 0 || now - lastPresentation > WINDOW) low = 0;
        return new Snapshot(render, synth, render + synth, low, p95, gpuMs, nativeMs, skipped, reason, nativeHook);
    }
    private static int count(long[] timestamps, long now) {
        int count = 0;
        for (long value : timestamps) if (value != 0 && now >= value && now - value < WINDOW) count++;
        return count;
    }
    /** Allocation-free display path; uses the same actual one-second submission window as the HUD. */
    public static synchronized int presentedFpsForDisplay() {
        if (first == 0) return -1;
        long now = System.nanoTime();
        double seconds = Math.max(0.25, Math.min(1, (now - first) / 1000000000.0));
        return (int) Math.round((count(real, now) + count(generated, now)) / seconds);
    }
    public static synchronized void reset() {
        Arrays.fill(real, 0); Arrays.fill(generated, 0); Arrays.fill(intervals, 0);
        realIndex = generatedIndex = intervalIndex = intervalCount = 0;
        first = lastReal = lastPresentation = skipped = 0;
        gpuMs = nativeMs = -1;
        reason = "等待游戏画面";
    }
    public static final class Snapshot {
        public final double renderFps, generatedFps, presentedFps, onePercentLow, p95Millis, gpuMillis, nativeRenderMillis;
        public final long skippedFrames;
        public final String reason, nativeHook;
        private Snapshot(double render, double generated, double presented, double low, double p95, double gpu,
                         double nativeTime, long skipped, String reason, String hook) {
            renderFps = render; generatedFps = generated; presentedFps = presented; onePercentLow = low;
            p95Millis = p95; gpuMillis = gpu; nativeRenderMillis = nativeTime;
            skippedFrames = skipped; this.reason = reason; nativeHook = hook;
        }
    }
}
