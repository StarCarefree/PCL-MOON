package pclmoon.framegen.test;

import pclmoon.framegen.bridge.HybridWait;
import pclmoon.framegen.bridge.PacingSettings;
import pclmoon.framegen.bridge.PresentationLimiter;

/** Deterministic clock tests: no wall-clock performance assertions. */
public final class FramePacingHarness {
    private static void check(boolean ok, String message) { if (!ok) throw new AssertionError(message); }
    private static final class Clock implements HybridWait.Clock {
        long time = 1000000000L, step, parked, extra;
        int parks, reads, cancelAfter = Integer.MAX_VALUE;
        boolean early, cancelled;
        public long now() {
            if (++reads > 100000) throw new AssertionError("unbounded wait");
            long result = time; time += step; return result;
        }
        public void park(long nanos) {
            check(nanos > 0 && nanos <= 50000000L, "bounded positive coarse wait");
            parks++; parked += nanos;
            time += early ? Math.max(1, nanos / 2) : nanos + extra;
            if (parks >= cancelAfter) cancelled = true;
        }
        public boolean interrupted() { return cancelled; }
    }
    public static void main(String[] args) {
        for (int mode = 0; mode < 3; mode++) {
            Clock c = new Clock(); c.step = 1000;
            long start = c.time;
            check(HybridWait.await(start + 10000000L, mode, c), "precision completes");
            long expectedMargin = mode == 2 ? 1000000L : mode == 1 ? 100000L : 0L;
            check(c.parked >= 10000000L - expectedMargin - 2000L
                && c.parked <= 10000000L - expectedMargin, "coarse/fine precision split " + mode);
            check(c.time >= start + 10000000L && c.time <= start + 10002000L, "absolute deadline " + mode);
        }
        Clock early = new Clock(); early.early = true;
        check(HybridWait.await(early.time + 80000000L, 0, early) && early.parks > 2, "spurious early wake rechecks deadline");
        Clock late = new Clock(); late.extra = 5000000L;
        long target = late.time + 10000000L;
        check(HybridWait.await(target, 2, late) && late.parks == 1 && late.time >= target, "oversleep never adds fine wait debt");
        Clock cancelled = new Clock(); cancelled.cancelAfter = 1;
        check(!HybridWait.await(cancelled.time + 100000000L, 0, cancelled) && cancelled.cancelled, "cancelled wait stays cancelled");
        Clock invalid = new Clock();
        check(!HybridWait.await(invalid.time + 2000000000L, 0, invalid) && invalid.parks == 0, "corrupt deadline rejected");
        check(HybridWait.await(invalid.time - 1, 2, invalid), "past deadline does not wait");
        for (int fps = 1; fps <= 999; fps++) {
            Clock c = new Clock(); PresentationLimiter limiter = new PresentationLimiter();
            check(limiter.await(fps, 0, c) && c.parks == 0, "first frame unlimited " + fps);
            long first = c.time;
            check(limiter.await(fps, 0, c) && c.time == first + 1000000000L / fps, "ceiling " + fps);
            c.time += 3000000000L;
            long stalled = c.time;
            limiter.await(fps, 0, c);
            check(c.time == stalled, "late frame does not replay debt");
            limiter.await(fps, 0, c);
            check(c.time == stalled + 1000000000L / fps, "next frame anchors actual wake");
            limiter.await(0, 0, c); long off = c.time;
            limiter.await(0, 0, c); limiter.await(-1, 0, c); limiter.await(1000, 0, c);
            check(c.time == off, "off/invalid cap stays open");
            limiter.await(fps, 0, c); check(c.time == off, "reenable starts clean");
            limiter.reset(); limiter.await(fps, 0, c); check(c.time == off, "reset starts clean");
        }
        Clock c = new Clock(); PresentationLimiter limiter = new PresentationLimiter();
        limiter.await(30, 0, c); long before = c.time;
        limiter.await(60, 0, c); check(c.time == before, "cap change discards obsolete deadline");
        limiter.await(60, 0, before + 25000000L, c);
        check(c.time == before + 25000000L && c.parked == 25000000L, "FG and independent cap merge, not sum");
        limiter.await(0, 0, c.time + 5000000L, c);
        check(c.time == before + 30000000L, "FG schedule works without independent cap");
        Thread.currentThread().interrupt();
        check(!HybridWait.await(System.nanoTime() + 1000000L, 2) && Thread.currentThread().isInterrupted(), "real interrupt preserved");
        Thread.interrupted();
        long revision = PacingSettings.revision();
        PacingSettings.apply(PacingSettings.fpsLimit(), PacingSettings.waitMode(), PacingSettings.telemetry());
        check(PacingSettings.revision() == revision, "no revision for equal snapshot");
        System.out.println("PASS frame pacing: all 1..999 caps / precision / wakeups / no catch-up / merged authority / interrupts");
    }
}
