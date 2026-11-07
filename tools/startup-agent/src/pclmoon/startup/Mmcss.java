package pclmoon.startup;

/** bootstrap 可见的桥接入口，不依赖 Minecraft、Fabric 或 JNA。 */
public final class Mmcss {
    private static final ThreadLocal<Boolean> ATTEMPTED = new ThreadLocal<>();
    private static volatile String library;
    private static volatile boolean loaded;
    private static volatile boolean unavailable;

    private Mmcss() { }

    public static void configure(String path) { library = path; }

    /** 由交换缓冲区的实际调用线程执行，不在 premain 线程上注册。 */
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

    /** 上下文解绑或同线程销毁时注销；线程退出还有原生 FLS 清理兜底。 */
    public static void leave() {
        try {
            if (loaded && Boolean.TRUE.equals(ATTEMPTED.get()) && !release0()) return;
            ATTEMPTED.remove();
        } catch (Throwable ignored) { /* 不影响原有窗口清理。 */ }
    }

    public static void contextChanged(long window) {
        if (window == 0) leave();
    }

    /** 读取 FLS 回调中成功注销的数量，供线程退出回归使用。 */
    public static long threadExitReleases() {
        try { return loaded ? threadExitReleases0() : 0; }
        catch (Throwable ignored) { return 0; }
    }

    /** 向 avrt 查询当前原生线程的注册状态。 */
    public static boolean registered() {
        try { return loaded && registered0(); }
        catch (Throwable ignored) { return false; }
    }

    private static native boolean acquire0();
    private static native boolean release0();
    private static native boolean registered0();
    private static native long threadExitReleases0();
}
