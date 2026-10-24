package pclmoon.framegen.render;

import java.awt.Component;
import java.awt.Container;
import java.awt.Window;
import java.util.concurrent.Callable;
import java.util.concurrent.FutureTask;
import javax.swing.AbstractButton;
import javax.swing.JComboBox;
import javax.swing.SwingUtilities;
import org.lwjgl.glfw.GLFW;
import org.lwjgl.opengl.GL;
import org.lwjgl.opengl.GL11;
import pclmoon.framegen.bridge.FrameSettings;

/** Exercises real GLFW -> transformed swap -> bootstrap overlay, without launching Minecraft. */
public final class GameWindowOverlaySmoke {
    private static long window;
    public static void main(String[] args) throws Exception {
        if (!GLFW.glfwInit()) throw new AssertionError("GLFW unavailable");
        try {
            GLFW.glfwWindowHint(GLFW.GLFW_CONTEXT_VERSION_MAJOR, 3);
            GLFW.glfwWindowHint(GLFW.GLFW_CONTEXT_VERSION_MINOR, 2);
            GLFW.glfwWindowHint(GLFW.GLFW_OPENGL_PROFILE, GLFW.GLFW_OPENGL_CORE_PROFILE);
            window = GLFW.glfwCreateWindow(640, 420, "PCL settings test (not Minecraft)", 0, 0);
            check(window != 0, "test game window");
            GLFW.glfwSetWindowPos(window, 100, 90);
            GLFW.glfwMakeContextCurrent(window);
            GL.createCapabilities();
            GLFW.glfwSwapInterval(0);
            GLFW.glfwFocusWindow(window);
            until(new Callable<Boolean>() { public Boolean call() throws Exception { return shown("pcl-framegen-gear-window"); } }, "gear attached to real GLFW window");
            final int oldX = onEdt(new Callable<Integer>() { public Integer call() { return findWindow("pcl-framegen-gear-window").getX(); } });
            GLFW.glfwSetWindowPos(window, 230, 140);
            until(new Callable<Boolean>() { public Boolean call() throws Exception {
                return onEdt(new Callable<Boolean>() { public Boolean call() {
                    Window gear = findWindow("pcl-framegen-gear-window");
                    return gear != null && gear.isVisible() && gear.getX() != oldX;
                } });
            } }, "gear follows actual GLFW movement");
            onEdt(new Callable<Void>() { public Void call() {
                ((AbstractButton) find(findWindow("pcl-framegen-gear-window"), "pcl-framegen-gear")).doClick(); return null;
            } });
            until(new Callable<Boolean>() { public Boolean call() throws Exception { return shown("pcl-framegen-panel"); } }, "gear opens settings");
            onEdt(new Callable<Void>() { public Void call() {
                Window panel = findWindow("pcl-framegen-panel");
                ((JComboBox<?>) find(panel, "pcl-framegen-fps")).setSelectedItem(Integer.valueOf(120));
                ((AbstractButton) find(panel, "pcl-framegen-generation")).doClick();
                return null;
            } });
            check(FrameSettings.generation() && FrameSettings.outputFps() == 120, "live panel writes shared settings");
            GLFW.glfwFocusWindow(window);
            tick();
            tick();
            // Minimise must hide chrome even if its settings panel previously owned AWT focus.
            GLFW.glfwIconifyWindow(window);
            until(new Callable<Boolean>() { public Boolean call() throws Exception {
                return !shown("pcl-framegen-gear-window") && !shown("pcl-framegen-panel");
            } }, "minimised GLFW hides chrome");
            GLFW.glfwDestroyWindow(window);
            window = 0;
            onEdt(new Callable<Void>() { public Void call() { return null; } });
            check(onEdt(new Callable<Boolean>() { public Boolean call() {
                return findWindow("pcl-framegen-gear-window") == null && findWindow("pcl-framegen-panel") == null;
            } }), "GLFW destruction disposes overlay");
            System.out.println("PASS real GLFW settings attachment/movement/live controls/minimise/destroy");
        } finally {
            if (window != 0) GLFW.glfwDestroyWindow(window);
            GLFW.glfwTerminate();
        }
    }
    private static void tick() throws Exception {
        GLFW.glfwPollEvents();
        GL11.glClearColor(0.08f, 0.11f, 0.16f, 1);
        GL11.glClear(GL11.GL_COLOR_BUFFER_BIT);
        GLFW.glfwSwapBuffers(window);
        Thread.sleep(20);
    }
    private static void until(Callable<Boolean> predicate, String message) throws Exception {
        long deadline = System.nanoTime() + 5000000000L;
        do { tick(); if (predicate.call()) return; } while (System.nanoTime() - deadline < 0);
        throw new AssertionError(message);
    }
    private static boolean shown(final String name) throws Exception {
        return onEdt(new Callable<Boolean>() { public Boolean call() {
            Window target = findWindow(name); return target != null && target.isVisible();
        } });
    }
    private static Window findWindow(String name) {
        for (Window candidate : Window.getWindows()) if (name.equals(candidate.getName()) && candidate.isDisplayable()) return candidate;
        return null;
    }
    private static Component find(Component parent, String name) {
        if (parent == null) return null;
        if (name.equals(parent.getName())) return parent;
        if (parent instanceof Container) for (Component child : ((Container) parent).getComponents()) {
            Component result = find(child, name); if (result != null) return result;
        }
        return null;
    }
    private static <T> T onEdt(Callable<T> action) throws Exception {
        FutureTask<T> task = new FutureTask<T>(action);
        SwingUtilities.invokeAndWait(task);
        return task.get();
    }
    private static void check(boolean ok, String message) { if (!ok) throw new AssertionError(message); }
}
