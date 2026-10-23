package pclmoon.framegen.bridge;

import java.util.concurrent.locks.LockSupport;

/** Absolute deadlines, no spin waits and no catch-up queue after a slow frame. */
public final class FramePacer {
    private final long period;
    private long lastReal;
    private boolean primed;
    public FramePacer(int outputFps) {
        period = 1000000000L / Math.max(30, Math.min(240, outputFps));
    }
    public void presented(long now) { lastReal = now; primed = true; }
    public void reset() { primed = false; }
    public boolean historyExpired(long now) { return primed && now - lastReal > 100000000L; }
    public boolean canGenerate(long now) {
        long elapsed = now - lastReal;
        return primed && elapsed >= 0 && elapsed <= period + period / 2;
    }
    public long midpoint() { return lastReal + period; }
    public long realDeadline() { return lastReal + period * 2; }
    public static boolean await(long deadline) {
        while (!Thread.currentThread().isInterrupted()) {
            long remaining = deadline - System.nanoTime();
            if (remaining <= 0) return true;
            // Never retain a deadline which could freeze a render thread after clock/reset errors.
            if (remaining > 100000000L) return false;
            LockSupport.parkNanos(remaining);
        }
        return false;
    }
}
