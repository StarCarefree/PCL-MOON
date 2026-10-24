package pclmoon.framegen.bridge;


/** Bounded adaptive presentation plan. A target is a ceiling, not a demand to discard slower inputs. */
public final class FramePacer {
    private final long minimumPeriod;
    private final int multiplier;
    private final boolean adaptive;
    private final long staleNanos;
    private long lastReal, firstDeadline, slotPeriod;
    private int plannedGenerated;
    private static final long MAX_EXTRA_DELAY = 10000000L;
    private boolean primed;
    public FramePacer(int outputFps) { this(outputFps, 2, true, 150); }
    public FramePacer(int outputFps, int multiplier, boolean adaptive, int staleMs) {
        minimumPeriod = 1000000000L / Math.max(30, Math.min(240, outputFps));
        this.multiplier = Math.max(2, Math.min(4, multiplier));
        this.adaptive = adaptive;
        staleNanos = Math.max(50, Math.min(500, staleMs)) * 1000000L;
        slotPeriod = minimumPeriod;
    }
    public void presented(long now) {
        lastReal = now;
        firstDeadline = now + minimumPeriod;
        slotPeriod = minimumPeriod;
        primed = true;
    }
    public void reset() { primed = false; plannedGenerated = 0; }
    public boolean historyExpired(long now) { return primed && now - lastReal >= staleNanos; }
    public boolean canGenerate(long now) {
        long elapsed = now - lastReal;
        return primed && elapsed >= 0 && elapsed < staleNanos
            && (adaptive || elapsed <= minimumPeriod + minimumPeriod / 2);
    }
    public boolean plan(long now) {
        plannedGenerated = 0;
        if (!canGenerate(now)) return false;
        slotPeriod = minimumPeriod;
        firstDeadline = Math.max(now, lastReal + slotPeriod);
        // Never stretch a slow input period and then hold the newest image for that period again.
        for (int count = multiplier - 1; count >= 1; count--) {
            long real = firstDeadline + slotPeriod * count;
            if (real - now <= MAX_EXTRA_DELAY) { plannedGenerated = count; return true; }
        }
        return false;
    }
    public int generatedCount() { return plannedGenerated; }
    /** Index 0 is the first generated frame; generatedCount is the current true frame. */
    public long deadline(int index) { return firstDeadline + slotPeriod * Math.max(0, Math.min(plannedGenerated, index)); }
    public long periodNanos() { return slotPeriod; }
    public long midpoint() { return firstDeadline; }
    public long realDeadline() { return plannedGenerated == 0 ? lastReal : deadline(plannedGenerated); }
    public static boolean await(long deadline) {
        if (deadline - System.nanoTime() > 100000000L) return false;
        return HybridWait.await(deadline, PacingSettings.waitMode());
    }
}
