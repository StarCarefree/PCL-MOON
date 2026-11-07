import java.lang.reflect.Method;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;
import org.lwjgl.glfw.GLFW;
import org.lwjgl.opengl.Display;

/** 通过变换后的 API 验证 avrt 注册、上下文解绑与原生线程退出清理。 */
public final class MmcssTest {
    private static Class<?> helper;
    private static Method registered;
    private static Method exitReleases;
    private static boolean expected;

    private static boolean registered() throws Exception {
        return helper != null && (Boolean) registered.invoke(null);
    }
    private static long exitReleases() throws Exception {
        return helper == null ? 0 : (Long) exitReleases.invoke(null);
    }
    private static void check(boolean result, String message) {
        if (!result) throw new AssertionError(message);
    }
    public static void main(String[] args) throws Exception {
        expected = args.length > 1 && args[1].equals("on");
        try {
            helper = Class.forName("pclmoon.startup.Mmcss", true, null);
            registered = helper.getMethod("registered");
            exitReleases = helper.getMethod("threadExitReleases");
        } catch (ClassNotFoundException disabled) { }
        check(!registered(), "premain must not boost the Java main thread");
        long beforeExit = exitReleases();
        var error = new AtomicReference<Throwable>();
        Thread render = new Thread(() -> {
            try {
                check(!registered(), "Render thread started with an unexpected registration");
                GLFW.glfwSwapBuffers(1L);
                check(registered() == expected, "GLFW hook did not register the calling native thread with avrt");
                for (int i = 0; i < 200; i++) GLFW.glfwSwapBuffers(1L);
                check(GLFW.swaps == 201, "Hook changed original presentation calls");
                GLFW.glfwMakeContextCurrent(1L);
                check(registered() == expected, "Binding a context must not release registration");
                GLFW.failDetach = true;
                try { GLFW.glfwMakeContextCurrent(0L); throw new AssertionError("Fixture must fail"); }
                catch (IllegalArgumentException failure) { }
                finally { GLFW.failDetach = false; }
                check(registered() == expected, "Failed context change must retain registration");
                GLFW.glfwMakeContextCurrent(0L);
                check(!registered(), "GLFW detach did not revert MMCSS on the owning thread");
                GLFW.glfwSwapBuffers(1L);
                check(registered() == expected, "Detached thread did not register after resuming presentation");
                GLFW.glfwDestroyWindow(1L);
                check(!registered(), "Window destruction did not revert MMCSS");
                GLFW.glfwSwapBuffers(2L);
                check(registered() == expected, "Recreated window did not register");
                GLFW.glfwTerminate();
                check(!registered(), "GLFW termination did not revert MMCSS");
                Display.swapBuffers();
                check(registered() == expected && Display.swaps == 1, "LWJGL2 hook failed");
                Display.failRelease = true;
                try { Display.releaseContext(); throw new AssertionError("Fixture must fail"); }
                catch (IllegalStateException failure) { }
                finally { Display.failRelease = false; }
                check(registered() == expected, "Failed LWJGL2 release must retain registration");
                Display.releaseContext();
                check(!registered(), "LWJGL2 release did not revert MMCSS");
                Display.swapBuffers();
                check(registered() == expected, "LWJGL2 reattachment did not register");
                Display.destroy();
                check(!registered(), "LWJGL2 cleanup did not revert MMCSS");
                // 故意不注销最后一次注册，退出后必须看到 FLS 成功释放计数增加。
                GLFW.glfwSwapBuffers(3L);
                check(registered() == expected, "Thread-exit registration failed");
            } catch (Throwable failure) { error.set(failure); }
        }, "mmcss-fixture-render");
        render.start(); render.join(10000);
        check(!render.isAlive(), "MMCSS hook stalled the render thread");
        if (error.get() != null) throw new AssertionError("render-thread failure", error.get());
        long wanted = beforeExit + (expected ? 1 : 0);
        long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5);
        // Java join 可能先于原生线程的最终 FLS 回调完成。
        while (exitReleases() < wanted && System.nanoTime() < deadline) Thread.sleep(10);
        check(exitReleases() == wanted, "Native FLS callback did not successfully release the exited thread's registration");
        check(!registered(), "Render registration leaked onto the main thread");

        CountDownLatch detached = new CountDownLatch(1);
        CountDownLatch destroyed = new CountDownLatch(1);
        Thread worker = new Thread(() -> {
            try {
                GLFW.glfwMakeContextCurrent(4L);
                GLFW.glfwSwapBuffers(4L);
                check(registered() == expected, "Separate render thread failed to register");
                GLFW.glfwMakeContextCurrent(0L);
                check(!registered(), "Registration survived context detach");
                detached.countDown();
                check(destroyed.await(5, TimeUnit.SECONDS), "Main thread did not destroy the window");
                check(!registered(), "Live worker retained MMCSS after cross-thread window destruction");
            } catch (Throwable failure) { error.set(failure); }
            finally { detached.countDown(); }
        }, "mmcss-live-worker");
        worker.start();
        try {
            check(detached.await(5, TimeUnit.SECONDS), "Worker did not detach its context");
            if (error.get() != null) throw new AssertionError("detach failure", error.get());
            check(worker.isAlive(), "Worker must remain alive during window destruction");
            GLFW.glfwDestroyWindow(4L);
        } finally {
            destroyed.countDown();
            worker.join(10000);
        }
        check(!worker.isAlive(), "Cross-thread cleanup stalled");
        if (error.get() != null) throw new AssertionError("cross-thread failure", error.get());
        System.out.println("PASS MMCSS: avrt query, repeated frames, context detach/rebind/failure, cross-thread destruction, native FLS releases="
            + (exitReleases() - beforeExit) + ", mode=" + expected);
    }
}
