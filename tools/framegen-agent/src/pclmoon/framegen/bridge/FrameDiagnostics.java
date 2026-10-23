package pclmoon.framegen.bridge;

import java.lang.management.GarbageCollectorMXBean;
import java.lang.management.ManagementFactory;
import java.lang.ref.WeakReference;
import java.util.List;

/** Samples only on the daemon, never MXBeans or stack traces on the normal render path. */
public final class FrameDiagnostics implements Runnable {
    private final List<GarbageCollectorMXBean> collectors = ManagementFactory.getGarbageCollectorMXBeans();
    private volatile boolean running = true;
    private volatile long heartbeat;
    private volatile WeakReference<Thread> renderThread = new WeakReference<Thread>(null);
    private volatile boolean active;
    private long reportedHeartbeat;
    public FrameDiagnostics() {
        Thread worker = new Thread(this, "pcl-frame-time-diagnostics");
        worker.setDaemon(true);
        worker.start();
    }
    public void frame(boolean foreground) {
        if (renderThread.get() != Thread.currentThread()) renderThread = new WeakReference<Thread>(Thread.currentThread());
        heartbeat = System.nanoTime();
        active = foreground;
    }
    public void close() { active = false; running = false; }
    public static boolean isStall(long now, long lastFrame) {
        return lastFrame != 0 && now - lastFrame >= 250000000L;
    }
    public void presented() { heartbeat = System.nanoTime(); }
    private long gcTime() {
        long total = 0;
        for (GarbageCollectorMXBean gc : collectors) total += Math.max(0, gc.getCollectionTime());
        return total;
    }
    public void run() {
        long priorGc = gcTime();
        while (running) {
            try { Thread.sleep(100); }
            catch (InterruptedException stopped) { return; }
            try {
                long currentGc = gcTime();
                long last = heartbeat;
                long now = System.nanoTime();
                Thread thread = renderThread.get();
                if (active && thread != null && last != reportedHeartbeat && isStall(now, last)) {
                    reportedHeartbeat = last;
                    StringBuilder stack = new StringBuilder();
                    StackTraceElement[] frames = thread.getStackTrace();
                    for (int i = 0; i < Math.min(6, frames.length); i++) stack.append(i == 0 ? "" : " <- ").append(frames[i]);
                    System.err.println("[PCL FrameTime] presentation gap=" + (now - last) / 1000000L
                        + "ms, GC time since last sample=" + Math.max(0, currentGc - priorGc)
                        + "ms, render=" + thread.getState() + ", stack=" + stack
                        + "; gap may include loading/focus changes, not a root-cause verdict");
                }
                priorGc = currentGc;
            } catch (Throwable unavailable) {
                // Diagnostics must not affect rendering, even with unsupported MXBeans.
                running = false;
            }
        }
    }
}
