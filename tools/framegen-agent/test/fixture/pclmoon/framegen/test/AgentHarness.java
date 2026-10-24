package pclmoon.framegen.test;

import java.io.File;
import org.lwjgl.glfw.GLFW;
import pclmoon.framegen.bridge.FrameDiagnostics;
import pclmoon.framegen.bridge.FramePacer;
import pclmoon.framegen.bridge.SwapHook;
import pclmoon.framegen.bridge.FrameSettings;
import pclmoon.framegen.bridge.FrameMetrics;
import pclmoon.framegen.bridge.NativeGameHooks;
import pclmoon.framegen.bridge.PacingSettings;

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
            } else if (mode.equals("triple") || mode.equals("quadruple")) {
                int factor = FrameSettings.multiplier();
                GLFW.glfwSwapBuffers(1);
                check(Probe.intermediates >= 1 && Probe.intermediates < factor && GLFW.swaps == Probe.intermediates + 2,
                    "latency-budgeted generation count equals actual successful submissions");
                int plannedFactor = Math.round(1f / Probe.alphas[0]);
                for (int i = 0; i < Probe.intermediates; i++) check(Math.abs(Probe.alphas[i] - (i + 1) / (float) plannedFactor) < 0.0001,
                    "each effective multiplier frame needs a distinct interpolation time");
                FrameMetrics.Snapshot measured = FrameMetrics.snapshot();
                check(Math.abs(measured.presentedFps - measured.renderFps - measured.generatedFps) < 0.001,
                    "presented count equals successful true + generated submissions");
            } else if (mode.equals("slow-input")) {
                Thread.sleep(33);
                GLFW.glfwSwapBuffers(1);
                check(Probe.intermediates == 1 && GLFW.swaps == 3, "30 FPS input / high target must not disable every interpolation");
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
                for (int i = 0; i < 2; i++) GLFW.glfwSwapBuffers(1);
                check(Probe.captures == 1 && Probe.resets == 1, "short cooldown avoids capture/flow work while lowering quality");
                GLFW.glfwSwapBuffers(1);
                check(Probe.captures == 2 && Probe.intermediates == 0, "resume with fresh history, not stale frames");
            } else if (mode.equals("runtime-settings")) {
                FrameSettings.setGeneration(false);
                GLFW.glfwSwapBuffers(1);
                check(Probe.captures == 1 && Probe.closes == 1 && GLFW.swaps == 2, "live disable must stop capture and release renderer");
                FrameSettings.setGeneration(true);
                GLFW.glfwSwapBuffers(1);
                check(Probe.captures == 2 && Probe.intermediates == 0, "live enable starts cold");
                GLFW.glfwSwapBuffers(1);
                check(Probe.intermediates == 1, "live enable generates after warm-up");
                FrameSettings.setOutputFps(60);
                GLFW.glfwSwapBuffers(1);
                check(Probe.resets >= 1 && Probe.intermediates == 1, "FPS change must reset history and pacing");
                FrameSettings.setDiagnostics(true);
                GLFW.glfwSwapBuffers(1);
                FrameSettings.setDiagnostics(false);
                GLFW.glfwSwapBuffers(1);
                FrameSettings.setOutputFps(0);
                check(FrameSettings.outputFps() == 30, "low FPS clamped");
                FrameSettings.setOutputFps(1000);
                check(FrameSettings.outputFps() == 240, "high FPS clamped");
            } else if (mode.equals("overlay-unavailable")) {
                GLFW.glfwSwapBuffers(1);
                check(Probe.captures == 2 && Probe.intermediates == 1, "optional UI failure must not disable generation");
            } else if (mode.equals("gpu-pending")) {
                Probe.gpuPending = true;
                GLFW.glfwSwapBuffers(1);
                check(Probe.captures == 1 && Probe.intermediates == 0 && Probe.resets == 1, "busy GPU must not receive another generation batch");
                Probe.gpuPending = false;
                GLFW.glfwSwapBuffers(1);
                check(Probe.captures == 2 && Probe.intermediates == 0, "backpressure resumes with cold history");
            } else if (mode.equals("latency-cap")) {
                Thread.sleep(40);
                GLFW.glfwSwapBuffers(1);
                check(Probe.intermediates == 0 && GLFW.swaps == 2, "60 FPS ceiling must not force real frames down to 30 for generation");
            } else if (mode.equals("standalone-cap") || mode.equals("combined-cap")) {
                boolean combined = mode.equals("combined-cap");
                for (int i = 0; i < 3; i++) GLFW.glfwSwapBuffers(1);
                check(combined ? Probe.intermediates == 3 : Probe.captures == 0, "independent cap must not force generation");
                for (int i = 1; i < GLFW.swaps; i++)
                    check(GLFW.submissions[i] - GLFW.submissions[i - 1] >= (combined ? 3900000L : 31000000L), "every actual presentation respects cap");
                check(GLFW.swaps == (combined ? 7 : 4), "capped presentation counters reflect submitted frames");
                FrameSettings.setGeneration(false); FrameSettings.setShowFps(false);
                long last = GLFW.submissions[GLFW.swaps - 1];
                GLFW.glfwSwapBuffers(1);
                check(GLFW.submissions[GLFW.swaps - 1] - last >= (combined ? 3900000L : 31000000L), "cap survives hidden HUD and disabled FG");
                PacingSettings.setFpsLimit(0);
            } else if (mode.equals("low-cap")) {
                GLFW.glfwSwapBuffers(1);
                check(Probe.intermediates == 0 && GLFW.swaps == 2, "low standalone cap pauses generation");
                PacingSettings.setFpsLimit(0);
            } else {
                GLFW.glfwSwapBuffers(1);
                check(GLFW.swaps == 2 && Probe.intermediates == 0, "disabled/unsupported presentation stays native");
            }
            GLFW.glfwDestroyWindow(1);
            check(GLFW.destroys == 1, "destroy original called once");
            if (mode.equals("generate")) check(Probe.closes == 1, "renderer disposed on context owner thread");
        }
        testTiming();
        testMetricsAndNativeCap();
        System.out.println("PASS " + mode);
    }
    private static void testTiming() {
        FramePacer pacer = new FramePacer(240);
        check(!pacer.canGenerate(100), "no previous real frame");
        pacer.presented(1000000000L);
        check(pacer.plan(1001000000L), "on-time frame fits low latency budget");
        check(pacer.midpoint() == 1004166666L && pacer.realDeadline() == 1008333332L, "absolute evenly spaced bounded deadlines");
        check(pacer.plan(1040000000L) && pacer.realDeadline() - 1040000000L <= 10000000L,
            "slower input must not accumulate another input-sized wait");
        FramePacer fixed = new FramePacer(60, 2, false, 150);
        fixed.presented(1000000000L);
        check(!fixed.canGenerate(1040000000L), "fixed pacing remains an explicit option");
        check(pacer.historyExpired(1250000000L), "long stall invalidates history");
        pacer.reset();
        check(!pacer.canGenerate(1250000000L), "no catch-up after stall");
        check(!FrameDiagnostics.isStall(1249999999L, 1000000000L), "stall boundary below threshold");
        check(FrameDiagnostics.isStall(1250000000L, 1000000000L), "250ms is a diagnostic long frame");
        Thread.currentThread().interrupt();
        check(!FramePacer.await(System.nanoTime() + 1000000L) && Thread.currentThread().isInterrupted(), "no spin or lost interrupt");
        Thread.interrupted();
    }
    private static void testMetricsAndNativeCap() {
        FrameMetrics.reset();
        for (int i = 0; i < 30; i++) {
            FrameMetrics.realPresented(1000000000L + i * 33333333L);
            FrameMetrics.generatedPresented(1016666666L + i * 33333333L);
        }
        FrameMetrics.Snapshot sample = FrameMetrics.snapshotAt(2000000000L);
        check(sample.renderFps == 29 && sample.generatedFps == 30 && sample.presentedFps == 59,
            "rolling FPS uses actual timestamped submissions, not a multiplier formula");
        FrameMetrics.Snapshot stalled = FrameMetrics.snapshotAt(3500000000L);
        check(stalled.presentedFps == 0 && stalled.onePercentLow == 0, "no frozen good FPS during a genuine stall");
        FrameSettings.setGeneration(true);
        FrameSettings.setNativeFrameControl(true);
        FrameSettings.setMultiplier(2);
        FrameSettings.setOutputFps(240);
        NativeGameHooks.setPresentationActive(true);
        check(NativeGameHooks.limitFps(30) == 120, "temporary native game cap must not conflict with selected ceiling");
        FrameSettings.setGeneration(false);
        check(NativeGameHooks.limitFps(30) == 30, "disabling generation immediately restores original game cap");
        NativeGameHooks.setPresentationActive(false);
    }
}
