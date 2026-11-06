import java.lang.reflect.Method;

/** Run with real lwjgl + glfw + Windows native JARs, without the API fixtures on the classpath. */
public final class MmcssGlfwSmoke {
    public static void main(String[] args) throws Exception {
        Class<?> glfw = Class.forName("org.lwjgl.glfw.GLFW");
        Class<?> helper = Class.forName("pclmoon.startup.Mmcss", true, null);
        Method registered = helper.getMethod("registered");
        if ((Boolean) registered.invoke(null)) throw new AssertionError("Registered before presentation");
        if (!(Boolean) glfw.getMethod("glfwInit").invoke(null)) throw new AssertionError("GLFW init failed");
        long window = 0;
        try {
            glfw.getMethod("glfwWindowHint", int.class, int.class).invoke(null, 0x00020004, 0); // GLFW_VISIBLE=false
            window = (Long) glfw.getMethod("glfwCreateWindow", int.class, int.class, CharSequence.class, long.class, long.class)
                .invoke(null, 32, 32, "MMCSS isolated smoke", 0L, 0L);
            if (window == 0) throw new AssertionError("Hidden window creation failed");
            glfw.getMethod("glfwMakeContextCurrent", long.class).invoke(null, window);
            Method swap = glfw.getMethod("glfwSwapBuffers", long.class);
            for (int i = 0; i < 5; i++) swap.invoke(null, window);
            if (!(Boolean) registered.invoke(null)) throw new AssertionError("Real GLFW presentation did not register Games");
            glfw.getMethod("glfwDestroyWindow", long.class).invoke(null, window);
            window = 0;
            if ((Boolean) registered.invoke(null)) throw new AssertionError("Real GLFW destruction did not revert Games");
            System.out.println("PASS real LWJGL GLFW: hidden window, native Games registration/query, five swaps, native reversion");
        } finally {
            if (window != 0) glfw.getMethod("glfwDestroyWindow", long.class).invoke(null, window);
            glfw.getMethod("glfwTerminate").invoke(null);
        }
    }
}
