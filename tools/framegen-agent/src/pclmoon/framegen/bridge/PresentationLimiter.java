package pclmoon.framegen.bridge;

/** Per-stream ceiling. Missed deadlines reset to the new presentation; no catch-up debt. */
public final class PresentationLimiter {
    private int fps;
    private long last;
    private boolean started;
    public void reset() { started = false; fps = 0; }
    private long revision;
    private final HybridWait.Clock systemClock = new HybridWait.Clock() {
        public long now() { return System.nanoTime(); }
        public void park(long nanos) { java.util.concurrent.locks.LockSupport.parkNanos(nanos); }
        public boolean interrupted() {
            return Thread.currentThread().isInterrupted() || PacingSettings.revision() != revision;
        }
    };
    public boolean await(int limit, int mode) { return await(limit, mode, 0L); }
    public boolean await(int limit, int mode, long notBefore) {
        revision = PacingSettings.revision();
        return await(limit, mode, notBefore, systemClock);
    }
    public boolean await(int limit, int mode, HybridWait.Clock clock) {
        return await(limit, mode, 0L, clock);
    }
    /** Merge generated-frame timing and independent cap into one absolute wait. */
    public boolean await(int limit, int mode, long notBefore, HybridWait.Clock clock) {
        boolean capped = limit >= 1 && limit <= 999;
        long now = clock.now(), deadline = now;
        if (capped && started && fps == limit && now - last >= 0) deadline = last + 1000000000L / limit;
        if (notBefore != 0L && notBefore - deadline > 0) deadline = notBefore;
        boolean completed = HybridWait.await(deadline, mode, clock);
        last = clock.now(); fps = capped ? limit : 0;
        started = capped && completed;
        return completed;
    }
}
