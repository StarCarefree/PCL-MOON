import java.lang.reflect.Method;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

/** 只使用真实 LWJGL 与 Windows 原生库，不将 API 测试替身加入类路径。 */
public final class MmcssGlfwSmoke {
    public static void main(String[] args) throws Exception {
        Class<?> glfw = Class.forName("org.lwjgl.glfw.GLFW");
        Class<?> helper = Class.forName("pclmoon.startup.Mmcss", true, null);
        Method registered = helper.getMethod("registered");
        Method context = glfw.getMethod("glfwMakeContextCurrent", long.class);
        Method swap = glfw.getMethod("glfwSwapBuffers", long.class);
        if ((Boolean) registered.invoke(null)) throw new AssertionError("Registered before presentation");
        if (!(Boolean) glfw.getMethod("glfwInit").invoke(null)) throw new AssertionError("GLFW init failed");
        long window = 0;
        try {
            glfw.getMethod("glfwWindowHint", int.class, int.class).invoke(null, 0x00020004, 0); // 隐藏窗口
            Method create = glfw.getMethod("glfwCreateWindow", int.class, int.class, CharSequence.class, long.class, long.class);
            window = (Long) create.invoke(null, 32, 32, "MMCSS isolated smoke", 0L, 0L);
            if (window == 0) throw new AssertionError("Hidden window creation failed");
            context.invoke(null, window);
            for (int i = 0; i < 5; i++) swap.invoke(null, window);
            if (!(Boolean) registered.invoke(null)) throw new AssertionError("Real GLFW presentation did not register Games");
            context.invoke(null, 0L);
            if ((Boolean) registered.invoke(null)) throw new AssertionError("Real GLFW detach did not revert Games");
            context.invoke(null, window);
            swap.invoke(null, window);
            if (!(Boolean) registered.invoke(null)) throw new AssertionError("Real GLFW rebind did not register Games");
            glfw.getMethod("glfwDestroyWindow", long.class).invoke(null, window);
            window = 0;
            if ((Boolean) registered.invoke(null)) throw new AssertionError("Real GLFW destruction did not revert Games");

            window = (Long) create.invoke(null, 32, 32, "MMCSS cross-thread smoke", 0L, 0L);
            if (window == 0) throw new AssertionError("Second hidden window creation failed");
            long sharedWindow = window;
            var error = new AtomicReference<Throwable>();
            var detached = new CountDownLatch(1);
            var destroyed = new CountDownLatch(1);
            Thread render = new Thread(() -> {
                try {
                    context.invoke(null, sharedWindow);
                    swap.invoke(null, sharedWindow);
                    if (!(Boolean) registered.invoke(null)) throw new AssertionError("Worker did not register Games");
                    context.invoke(null, 0L);
                    if ((Boolean) registered.invoke(null)) throw new AssertionError("Worker detach did not revert Games");
                    detached.countDown();
                    if (!destroyed.await(5, TimeUnit.SECONDS)) throw new AssertionError("Main thread did not destroy the window");
                    if ((Boolean) registered.invoke(null)) throw new AssertionError("Live worker retained Games after destruction");
                } catch (Throwable failure) { error.set(failure); }
                finally {
                    try { context.invoke(null, 0L); } catch (Throwable ignored) { }
                    detached.countDown();
                }
            }, "mmcss-real-glfw-worker");
            render.start();
            try {
                if (!detached.await(10, TimeUnit.SECONDS)) throw new AssertionError("Worker did not detach");
                if (error.get() != null) throw new AssertionError("Worker failure", error.get());
                if (!render.isAlive()) throw new AssertionError("Worker must stay alive during destruction");
                glfw.getMethod("glfwDestroyWindow", long.class).invoke(null, window);
                window = 0;
            } finally {
                destroyed.countDown();
                render.join(10000);
            }
            if (render.isAlive()) throw new AssertionError("Worker did not finish");
            if (error.get() != null) throw new AssertionError("Worker cleanup failure", error.get());
            System.out.println("PASS real LWJGL GLFW: native registration/query, detach/rebind, same-thread and cross-thread destruction with live worker");
        } finally {
            if (window != 0) glfw.getMethod("glfwDestroyWindow", long.class).invoke(null, window);
            glfw.getMethod("glfwTerminate").invoke(null);
        }
    }
}
