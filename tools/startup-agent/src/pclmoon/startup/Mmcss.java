package pclmoon.startup;

/** Bootstrap-visible boundary; no Minecraft, Fabric or JNA classes are required. */
public final class Mmcss {
    private static final ThreadLocal<Boolean> ATTEMPTED = new ThreadLocal<>();
    private static volatile String library;
    private static volatile boolean loaded;
    private static volatile boolean unavailable;

    private Mmcss() { }

    public static void configure(String path) { library = path; }

    /** Called by the presentation API on its caller, never on the premain/worker thread. */
    public static void enter() {
        try {
            if (unavailable || library == null || ATTEMPTED.get() != null
                    || Boolean.getBoolean("pclmoon.startup.mmcss.disabled")) return;
            ATTEMPTED.set(Boolean.TRUE);
            if (!load()) return;
            if (acquire0()) {
                System.err.println("[PCL-MOON MMCSS] Games registered on presentation thread: "
                    + Thread.currentThread().getName());
            } else {
                System.err.println("[PCL-MOON MMCSS] Games registration unavailable; original scheduling retained");
            }
        } catch (Throwable ignored) {
            unavailable = true;
            System.err.println("[PCL-MOON MMCSS] unavailable; original scheduling retained");
        }
    }

    private static synchronized boolean load() {
        if (unavailable) return false;
        if (!loaded) {
            try { System.load(library); loaded = true; }
            catch (Throwable ignored) {
                unavailable = true;
                System.err.println("[PCL-MOON MMCSS] native bridge unavailable; original scheduling retained");
                return false;
            }
        }
        return true;
    }

    /** Window cleanup is on the render thread. Native FLS also reverts on thread exit. */
    public static void leave() {
        try {
            if (loaded && Boolean.TRUE.equals(ATTEMPTED.get()) && !release0()) return;
            ATTEMPTED.remove();
        } catch (Throwable ignored) { /* Never break window destruction. */ }
    }

    /** Diagnostic/test query asks avrt about the current native thread's task handle. */
    public static boolean registered() {
        try { return loaded && registered0(); }
        catch (Throwable ignored) { return false; }
    }

    private static native boolean acquire0();
    private static native boolean release0();
    private static native boolean registered0();
}
