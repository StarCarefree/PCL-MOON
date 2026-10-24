package pclmoon.framegen.bridge;

import java.util.concurrent.locks.LockSupport;

/** Absolute coarse park plus optional bounded fine wait, shared by native and generated frames. */
public final class HybridWait {
    public interface Clock {
        long now();
        void park(long nanos);
        boolean interrupted();
    }
    private static final Clock SYSTEM = new Clock() {
        public long now() { return System.nanoTime(); }
        public void park(long nanos) { LockSupport.parkNanos(nanos); }
        public boolean interrupted() { return Thread.currentThread().isInterrupted(); }
    };
    private HybridWait() { }
    public static boolean await(long deadline, int mode) { return await(deadline, mode, SYSTEM); }
    public static boolean await(long deadline, int mode, Clock clock) {
        if (clock == null) throw new IllegalArgumentException("clock");
        long margin = mode == 2 ? 1000000L : mode == 1 ? 100000L : 0;
        while (!clock.interrupted()) {
            long remaining = deadline - clock.now();
            if (remaining <= 0) return true;
            // Supports a deliberate 1-FPS cap, but not corrupt/unbounded deadlines.
            if (remaining > 1100000000L) return false;
            if (remaining > margin) clock.park(Math.min(50000000L, remaining - margin));
            // Fine phase only observes the monotonic clock; maximum busy interval is margin.
        }
        return false; // Preserve the interrupt flag; never retry a cancelled wait.
    }
}
