package pclmoon.framegen.test;
import pclmoon.framegen.bridge.FramePacer;
/** A late game frame must not be held for another synthetic frame interval. */
public final class LatencyHarness {
    public static void main(String[] args) {
        FramePacer pacer = new FramePacer(60, 2, true, 150);
        pacer.presented(1000000000L);
        long arrived = 1040000000L;
        boolean planned = pacer.plan(arrived);
        long extra = planned ? pacer.realDeadline() - arrived : 0;
        System.out.println("LATENCY slow true frame extraDelayMs=" + extra / 1000000.0);
        if (extra > 10000000L) throw new AssertionError("late current frame unnecessarily delayed for generation");
        for (int fps = 30; fps <= 240; fps++) for (int factor = 2; factor <= 4; factor++) {
            for (int ms = 0; ms < 150; ms += 2) {
                FramePacer p = new FramePacer(fps, factor, true, 150);
                p.presented(1000000000L);
                long now = 1000000000L + ms * 1000000L;
                if (!p.plan(now)) continue;
                if (p.generatedCount() < 1 || p.generatedCount() >= factor) throw new AssertionError("invalid effective multiplier");
                if (p.realDeadline() - now > 10000000L || p.deadline(0) < now) throw new AssertionError("unbounded synthetic scheduling delay");
                for (int i = 1; i <= p.generatedCount(); i++)
                    if (p.deadline(i) - p.deadline(i - 1) != p.periodNanos()) throw new AssertionError("uneven presentation slots");
            }
        }
        System.out.println("PASS latency budget: 30..240 FPS / 2x..4x / early and late arrivals");
    }
}
