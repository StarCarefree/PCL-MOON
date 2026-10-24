package pclmoon.framegen.bridge;


/** Bounded adaptive presentation plan. A target is a ceiling, not a demand to discard slower inputs. */
public final class FramePacer {
    private final long minimumPeriod;
    private final int multiplier;
    private final boolean adaptive;
    private final long staleNanos;
    private long lastReal, firstDeadline, slotPeriod;
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
    public void reset() { primed = false; }
    public boolean historyExpired(long now) { return primed && now - lastReal >= staleNanos; }
    public boolean canGenerate(long now) {
        long elapsed = now - lastReal;
        return primed && elapsed >= 0 && elapsed < staleNanos
            && (adaptive || elapsed <= minimumPeriod + minimumPeriod / 2);
    }
    public boolean plan(long now) {
        if (!canGenerate(now)) return false;
        long inputWork = Math.max(0, now - lastReal);
        slotPeriod = adaptive ? Math.max(minimumPeriod, inputWork / multiplier) : minimumPeriod;
        firstDeadline = Math.max(now, lastReal + slotPeriod);
        return true;
    }
    /** Index 0 is the first generated frame; multiplier-1 is the current true frame. */
    public long deadline(int index) { return firstDeadline + slotPeriod * Math.max(0, Math.min(multiplier - 1, index)); }
    public long periodNanos() { return slotPeriod; }
    public long midpoint() { return deadline(0); }
    public long realDeadline() { return deadline(multiplier - 1); }
    public static boolean await(long deadline) {
        if (deadline - System.nanoTime() > 100000000L) return false;
        return HybridWait.await(deadline, PacingSettings.waitMode());
    }
}
