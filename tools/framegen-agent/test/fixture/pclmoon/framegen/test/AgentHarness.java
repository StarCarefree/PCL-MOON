package pclmoon.framegen.test;

import java.io.File;
import org.lwjgl.glfw.GLFW;
import pclmoon.framegen.bridge.FrameDiagnostics;
import pclmoon.framegen.bridge.FramePacer;
import pclmoon.framegen.bridge.SwapHook;

public final class AgentHarness {
    private static void check(boolean ok, String message) { if (!ok) throw new AssertionError(message); }
    public static void main(String[] args) throws Exception {
        String mode = args[0];
        if (mode.equals("disabled")) {
            GLFW.glfwSwapBuffers(1);
            try { GLFW.class.getMethod("pclmoon$glfwSwapBuffers", long.class); throw new AssertionError("disabled agent transformed GLFW"); }
            catch (NoSuchMethodException expected) { }
            check(GLFW.swaps == 1, "disabled passthrough");
        } else {
            check(SwapHook.class.getClassLoader() == null, "bridge must be bootstrap-visible");
            GLFW.class.getMethod("pclmoon$glfwSwapBuffers", long.class);
            SwapHook.configure(new File(args[1]).toURI().toString());
            if (mode.equals("no-context")) GLFW.context = 0;
            if (mode.equals("unfocused")) GLFW.focused = 0;
            GLFW.glfwSwapBuffers(1);
            check(GLFW.swaps == 1, "warm-up presents exactly one real frame");
            if (mode.equals("generate")) {
                GLFW.glfwSwapBuffers(1);
                check(GLFW.swaps == 3 && Probe.intermediates == 1 && Probe.currents == 1, "one compensated midpoint then current");
                GLFW.glfwSwapBuffers(2);
                check(GLFW.swaps == 4 && Probe.captures == 2, "secondary context must not reuse GL resources");
            } else if (mode.equals("long-gap")) {
                Thread.sleep(150);
                GLFW.glfwSwapBuffers(1);
                check(GLFW.swaps == 2 && Probe.resets == 1 && Probe.intermediates == 0,
                    "long gap must discard history instead of catching up");
            } else if (mode.equals("stall-diagnostics")) {
                Thread.sleep(420);
                GLFW.glfwSwapBuffers(1);
                check(GLFW.swaps == 2 && Probe.captures == 0, "diagnostic sampling must not initialise interpolation");
            } else if (mode.equals("render-failure")) {
                Probe.failIntermediate = true;
                GLFW.glfwSwapBuffers(1);
                GLFW.glfwSwapBuffers(1);
                check(GLFW.swaps == 3 && Probe.intermediates == 1 && Probe.currents == 1 && Probe.closes == 1,
                    "restore current, disable once and retain native presentation");
            } else if (mode.equals("native-failure")) {
                GLFW.nativeFailure = true;
                try { GLFW.glfwSwapBuffers(2); throw new AssertionError("native error swallowed"); }
                catch (RuntimeException expected) { }
                check(GLFW.swaps == 2, "failed native swap must not be retried");
                GLFW.nativeFailure = false;
            } else if (mode.equals("budget")) {
                Probe.budget = true;
                GLFW.glfwSwapBuffers(1);
                for (int i = 0; i < 120; i++) GLFW.glfwSwapBuffers(1);
                check(Probe.captures == 1 && Probe.resets == 1, "cooldown must avoid capture/flow work");
                GLFW.glfwSwapBuffers(1);
                check(Probe.captures == 2 && Probe.intermediates == 0, "resume with fresh history, not stale frames");
            } else {
                GLFW.glfwSwapBuffers(1);
                check(GLFW.swaps == 2 && Probe.intermediates == 0, "disabled/unsupported presentation stays native");
            }
            GLFW.glfwDestroyWindow(1);
            check(GLFW.destroys == 1, "destroy original called once");
            if (mode.equals("generate")) check(Probe.closes == 1, "renderer disposed on context owner thread");
        }
        testTiming();
        System.out.println("PASS " + mode);
    }
    private static void testTiming() {
        FramePacer pacer = new FramePacer(60);
        check(!pacer.canGenerate(100), "no previous real frame");
        pacer.presented(1000000000L);
        check(pacer.canGenerate(1001000000L), "on-time frame");
        check(pacer.midpoint() == 1016666666L && pacer.realDeadline() == 1033333332L, "absolute evenly spaced deadlines");
        check(!pacer.canGenerate(1040000000L), "late frame not queued");
        check(pacer.historyExpired(1250000000L), "long stall invalidates history");
        pacer.reset();
        check(!pacer.canGenerate(1250000000L), "no catch-up after stall");
        check(!FrameDiagnostics.isStall(1249999999L, 1000000000L), "stall boundary below threshold");
        check(FrameDiagnostics.isStall(1250000000L, 1000000000L), "250ms is a diagnostic long frame");
        Thread.currentThread().interrupt();
        check(!FramePacer.await(System.nanoTime() + 1000000L) && Thread.currentThread().isInterrupted(), "no spin or lost interrupt");
        Thread.interrupted();
    }
}
