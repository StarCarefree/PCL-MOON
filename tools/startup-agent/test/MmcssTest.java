import java.lang.reflect.Method;
import java.util.concurrent.atomic.AtomicReference;
import org.lwjgl.glfw.GLFW;
import org.lwjgl.opengl.Display;

/** Real Windows avrt registration/query/reversion, called only via transformed presentation APIs. */
public final class MmcssTest {
    private static Class<?> helper;
    private static Method registered;
    private static boolean expected;

    private static boolean registered() throws Exception {
        return helper != null && (Boolean) registered.invoke(null);
    }
    private static void check(boolean result, String message) {
        if (!result) throw new AssertionError(message);
    }
    public static void main(String[] args) throws Exception {
        expected = args.length > 1 && args[1].equals("on");
        try {
            helper = Class.forName("pclmoon.startup.Mmcss", true, null);
            registered = helper.getMethod("registered");
        } catch (ClassNotFoundException disabled) { }
        check(!registered(), "premain must not boost the Java main thread");
        var error = new AtomicReference<Throwable>();
        Thread render = new Thread(() -> {
            try {
                check(!registered(), "Render thread started with an unexpected registration");
                GLFW.glfwSwapBuffers(1L);
                check(registered() == expected, "GLFW hook did not register the calling native thread with avrt");
                for (int i = 0; i < 200; i++) GLFW.glfwSwapBuffers(1L);
                check(GLFW.swaps == 201, "Hook changed original presentation calls");
                GLFW.glfwDestroyWindow(1L);
                check(!registered(), "Window destruction did not revert MMCSS");
                GLFW.glfwSwapBuffers(2L);
                check(registered() == expected, "Recreated window did not register");
                GLFW.glfwTerminate();
                check(!registered(), "GLFW termination did not revert MMCSS");
                Display.swapBuffers();
                check(registered() == expected && Display.swaps == 1, "LWJGL2 hook failed");
                Display.destroy();
                check(!registered(), "LWJGL2 cleanup did not revert MMCSS");
                // Also exercise native FLS cleanup on thread exit, without an explicit destroy.
                GLFW.glfwSwapBuffers(3L);
                check(registered() == expected, "Thread-exit registration failed");
            } catch (Throwable failure) { error.set(failure); }
        }, "mmcss-fixture-render");
        render.start(); render.join(10000);
        check(!render.isAlive(), "MMCSS hook stalled the render thread");
        if (error.get() != null) throw new AssertionError("render-thread failure", error.get());
        check(!registered(), "Render registration leaked onto the main thread");
        System.out.println("PASS MMCSS: caller-thread avrt query, repeated frames, GLFW/LWJGL2 cleanup, thread exit, mode=" + expected);
    }
}
