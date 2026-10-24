package pclmoon.framegen.test;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import pclmoon.framegen.bridge.FramePacerProfiles;
import pclmoon.framegen.bridge.FrameSettings;
import pclmoon.framegen.bridge.PacingSettings;
import pclmoon.framegen.bridge.UpscaleSettings;

/** Deterministic temp-dir coverage for the profile overlay. Args: tempDir. */
public final class ProfilesHarness {
    private static int failures;

    public static void main(String[] args) throws Exception {
        if (args.length < 1) throw new IllegalArgumentException("tempDir required");
        Path root = Paths.get(args[0]);
        Files.createDirectories(root);
        try {
            unconfigured();
            overlay(root.resolve("overlay"));
            aliasAndInvalid(root.resolve("alias"));
            semanticUnchanged(root.resolve("semantic"));
            saveReset(root.resolve("save"));
            limits(root.resolve("limits"));
            symlink(root.resolve("link"));
            reconfigure(root.resolve("re"));
            superResolution(root.resolve("sr"));
        } finally {
            FramePacerProfiles.close();
        }
        if (failures != 0) throw new AssertionError(failures + " profile checks failed");
        System.out.println("profiles harness ok");
    }

    private static void unconfigured() throws Exception {
        FramePacerProfiles.close();
        FramePacerProfiles.configure(null, "   ");
        Thread.sleep(50L);
        check(FramePacerProfiles.profilePath().length() == 0, "no path stays empty");
        check(FramePacerProfiles.status().length() == 0, "no path stays unavailable");
        FramePacerProfiles.saveInstance();
        FramePacerProfiles.resetInstance();
        FramePacerProfiles.applyPending();
    }

    private static void overlay(Path dir) throws Exception {
        Files.createDirectories(dir);
        Path global = dir.resolve("global.properties");
        Path instance = dir.resolve("instance.properties");
        write(global, "fps_limit=40\npacing_mode=efficient\ntelemetry=off\noutput_fps=90\nmultiplier=3\n# comment\n");
        write(instance, "fps_limit=120\npacing_mode=precise\nhud=on\nquality=2\nunknown=1\n");
        seed(0, 1, true, false, true, 60, 2, 1, 160, 8, 150, true, true, true, false);
        FramePacerProfiles.configure(global.toString(), instance.toString());
        check(instance.toString().equals(FramePacerProfiles.profilePath()), "profilePath is the instance path");
        await(120, 2, false, true, true, 90, 3, 2, 160, 8, 150, true, true, true, false);
        check("已加载".equals(FramePacerProfiles.status()) || "读取中".equals(FramePacerProfiles.status())
            || FramePacerProfiles.status().length() > 0, "status is meaningful after load");
    }

    private static void aliasAndInvalid(Path dir) throws Exception {
        Files.createDirectories(dir);
        Path global = dir.resolve("global.properties");
        Path instance = dir.resolve("instance.properties");
        write(global, "global_fps_limit=77\nfps_limit=10\npacing_mode=balanced\ndiagnostics=true\n");
        write(instance, "global_fps_limit=9\noutput_fps=999\nmultiplier=9\ntelemetry=maybe\nflow_width=10\n");
        seed(5, 0, false, true, false, 60, 2, 0, 80, 2, 50, false, false, false, false);
        FramePacerProfiles.configure(global.toString(), instance.toString());
        await(10, 1, false, true, false, 60, 2, 0, 80, 2, 50, false, false, false, true);
        write(global, "fps_limit=nope\npacing_mode=nope\ngpu_budget_ms=1\nstall_threshold_ms=9999\nadaptive=off\n");
         await(0, 0, false, true, false, 60, 2, 0, 80, 2, 50, false, false, false, false);
    }

    private static void semanticUnchanged(Path dir) throws Exception {
        Files.createDirectories(dir);
        Path global = dir.resolve("global.properties");
        Path instance = dir.resolve("instance.properties");
        write(global, "fps_limit=60\n");
        write(instance, "fps_limit = 80\noutput_fps : 120\n");
        seed(1, 1, true, false, true, 60, 2, 1, 160, 8, 150, true, true, true, false);
        FramePacerProfiles.configure(global.toString(), instance.toString());
        await(80, 1, true, false, true, 120, 2, 1, 160, 8, 150, true, true, true, false);
        PacingSettings.setFpsLimit(33);
        FrameSettings.setOutputFps(144);
        Thread.sleep(1100L);
        write(instance, "# still the same\nfps_limit=080\noutput_fps=0120\n");
        Thread.sleep(1200L);
        FramePacerProfiles.applyPending();
        check(PacingSettings.fpsLimit() == 33 && FrameSettings.outputFps() == 144, "comment/mtime-only rewrite keeps unsaved UI");
     }

    private static void saveReset(Path dir) throws Exception {
        Files.createDirectories(dir);
        Path global = dir.resolve("global.properties");
        Path instance = dir.resolve("nested").resolve("instance.properties");
        write(global, "fps_limit=50\npacing_mode=efficient\ntelemetry=off\n");
        seed(9, 2, true, true, false, 90, 3, 2, 240, 12, 200, false, false, false, true);
        FramePacerProfiles.configure(global.toString(), instance.toString());
        await(50, 0, false, true, false, 90, 3, 2, 240, 12, 200, false, false, false, true);
        PacingSettings.setFpsLimit(144);
        FrameSettings.setOutputFps(100);
        FramePacerProfiles.saveInstance();
        PacingSettings.setFpsLimit(166);
        awaitStatus("已保存");
        check(Files.isRegularFile(instance), "save creates the instance file and parents");
        byte[] saved = Files.readAllBytes(instance);
        String text = new String(saved, StandardCharsets.UTF_8);
        check(text.indexOf("fps_limit=144") >= 0 && text.indexOf("output_fps=100") >= 0, "save writes captured UI");
        check(text.indexOf("global_fps_limit") < 0, "save does not write the global alias");
        FramePacerProfiles.applyPending();
        check(PacingSettings.fpsLimit() == 166, "async save must not roll back later UI changes");
        byte[] globalBefore = Files.readAllBytes(global);
        FramePacerProfiles.resetInstance();
        awaitStatus("已重置");
        check(!Files.exists(instance), "reset removes only the instance file");
        check(java.util.Arrays.equals(globalBefore, Files.readAllBytes(global)), "reset does not rewrite global");
        await(50, 0, false, true, false, 90, 3, 2, 240, 12, 200, false, false, false, true);
    }

    private static void limits(Path dir) throws Exception {
        Files.createDirectories(dir);
        Path global = dir.resolve("global.properties");
        Path instance = dir.resolve("instance.properties");
        byte[] huge = new byte[1024 * 1024 + 8];
        java.util.Arrays.fill(huge, (byte) 'a');
        Files.write(global, huge);
        write(instance, "fps_limit=70\n");
        seed(3, 1, true, false, true, 60, 2, 1, 160, 8, 150, true, true, true, false);
        FramePacerProfiles.configure(global.toString(), instance.toString());
        await(70, 1, true, false, true, 60, 2, 1, 160, 8, 150, true, true, true, false);
        Files.write(instance, new byte[] { (byte) 0xFF, (byte) 0xFE, 0x00 });
        Thread.sleep(1200L);
        FramePacerProfiles.applyPending();
        check(PacingSettings.fpsLimit() == 70 || PacingSettings.fpsLimit() == 3, "bad encoding does not abort");
    }

    private static void symlink(Path dir) throws Exception {
        Files.createDirectories(dir);
        Path global = dir.resolve("global.properties");
        Path real = dir.resolve("real.properties");
        Path link = dir.resolve("instance.properties");
        write(global, "fps_limit=15\n");
        write(real, "fps_limit=200\n");
        try {
            Files.createSymbolicLink(link, real);
         } catch (UnsupportedOperationException ignored) {
             System.out.println("symlink skipped");
             return;
         } catch (IOException ignored) {
            System.out.println("symlink skipped");
            return;
        }
        seed(4, 1, true, false, true, 60, 2, 1, 160, 8, 150, true, true, true, false);
        FramePacerProfiles.configure(global.toString(), link.toString());
        Thread.sleep(400L);
        FramePacerProfiles.applyPending();
        check(PacingSettings.fpsLimit() != 200, "symlink instance is not followed");
    }

    private static void reconfigure(Path dir) throws Exception {
        Files.createDirectories(dir);
        Path a = dir.resolve("a.properties");
        Path b = dir.resolve("b.properties");
        write(a, "fps_limit=21\n");
        write(b, "fps_limit=22\n");
        seed(1, 1, true, false, true, 60, 2, 1, 160, 8, 150, true, true, true, false);
        FramePacerProfiles.configure(null, a.toString());
        FramePacerProfiles.configure(null, b.toString());
        FramePacerProfiles.close();
        Thread.sleep(200L);
        check(FramePacerProfiles.profilePath().length() == 0, "close clears profilePath");
        FramePacerProfiles.configure(null, b.toString());
        await(22, 1, true, false, true, 60, 2, 1, 160, 8, 150, true, true, true, false);
        FramePacerProfiles.close();
        check(FramePacerProfiles.profilePath().length() == 0 && FramePacerProfiles.status().length() == 0, "close clears status");
    }

    private static void superResolution(Path dir) throws Exception {
        Files.createDirectories(dir);
        Path global = dir.resolve("global.properties");
        Path instance = dir.resolve("instance.properties");
        write(global, "super_resolution=on\nrender_scale=77\nupscale_sharpness=15\n");
        write(instance, "render_scale=59\nupscale_sharpness=maybe\nsuper_resolution=nope\n");
        seed(6, 1, true, false, true, 60, 2, 1, 160, 8, 150, true, true, true, false);
        UpscaleSettings.apply(true, 80, 40);
        FramePacerProfiles.configure(global.toString(), instance.toString());
        await(6, 1, true, false, true, 60, 2, 1, 160, 8, 150, true, true, true, false);
        awaitUpscale(true, 59, 15);
        write(instance, "super_resolution=off\nrender_scale=49\nupscale_sharpness=101\n");
        awaitUpscale(false, 77, 15);
        UpscaleSettings.apply(true, 50, 0);
        FramePacerProfiles.saveInstance();
        awaitStatus("已保存");
        String saved = new String(Files.readAllBytes(instance), StandardCharsets.UTF_8);
        check(saved.indexOf("super_resolution=on") >= 0 && saved.indexOf("render_scale=50") >= 0
            && saved.indexOf("upscale_sharpness=0") >= 0, "save writes the SR group");
        FramePacerProfiles.resetInstance();
        awaitStatus("已重置");
        awaitUpscale(true, 77, 15);
        check(!Files.exists(instance), "SR reset removes only the instance file");
    }

    private static void awaitUpscale(boolean enabled, int scale, int sharpness) throws Exception {
        long deadline = System.currentTimeMillis() + 4000L;
        while (System.currentTimeMillis() < deadline) {
            FramePacerProfiles.applyPending();
            if (UpscaleSettings.enabled() == enabled && UpscaleSettings.scalePercent() == scale
                && UpscaleSettings.sharpnessPercent() == sharpness) return;
            Thread.sleep(40L);
        }
        FramePacerProfiles.applyPending();
        check(UpscaleSettings.enabled() == enabled && UpscaleSettings.scalePercent() == scale
            && UpscaleSettings.sharpnessPercent() == sharpness,
            "SR overlay expected " + enabled + "/" + scale + "/" + sharpness + " got "
                + UpscaleSettings.enabled() + "/" + UpscaleSettings.scalePercent() + "/" + UpscaleSettings.sharpnessPercent());
    }

    private static void seed(int fps, int mode, boolean telemetry, boolean hud, boolean generation, int output, int multiplier,
                             int quality, int flow, int gpu, int stall, boolean adaptive, boolean low, boolean nativeControl,
                             boolean diagnostics) {
        PacingSettings.apply(fps, mode, telemetry);
         FrameSettings.setShowFps(hud);
         FrameSettings.setGeneration(generation);
         FrameSettings.setOutputFps(output);
        FrameSettings.setMultiplier(multiplier);
        FrameSettings.setQuality(quality);
        FrameSettings.setFlowWidth(flow);
        FrameSettings.setGpuBudgetMs(gpu);
        FrameSettings.setStallThresholdMs(stall);
        FrameSettings.setAdaptivePacing(adaptive);
        FrameSettings.setLowFpsProtection(low);
        FrameSettings.setNativeFrameControl(nativeControl);
        FrameSettings.setDiagnostics(diagnostics);
        UpscaleSettings.apply(false, 67, 20);
    }

    private static void await(int fps, int mode, boolean telemetry, boolean hud, boolean generation, int output, int multiplier,
                              int quality, int flow, int gpu, int stall, boolean adaptive, boolean low, boolean nativeControl,
                              boolean diagnostics) throws Exception {
        long deadline = System.currentTimeMillis() + 4000L;
        while (System.currentTimeMillis() < deadline) {
            FramePacerProfiles.applyPending();
            if (matches(fps, mode, telemetry, hud, generation, output, multiplier, quality, flow, gpu, stall,
                adaptive, low, nativeControl, diagnostics)) return;
            Thread.sleep(40L);
        }
        FramePacerProfiles.applyPending();
        check(matches(fps, mode, telemetry, hud, generation, output, multiplier, quality, flow, gpu, stall,
            adaptive, low, nativeControl, diagnostics),
            "overlay did not apply expected settings fps=" + PacingSettings.fpsLimit()
                + " mode=" + PacingSettings.waitMode() + " out=" + FrameSettings.outputFps()
                + " status=" + FramePacerProfiles.status());
    }

    private static boolean matches(int fps, int mode, boolean telemetry, boolean hud, boolean generation, int output,
                                   int multiplier, int quality, int flow, int gpu, int stall, boolean adaptive,
                                   boolean low, boolean nativeControl, boolean diagnostics) {
        return PacingSettings.fpsLimit() == fps && PacingSettings.waitMode() == mode && PacingSettings.telemetry() == telemetry
            && FrameSettings.showFps() == hud && FrameSettings.generation() == generation
            && FrameSettings.outputFps() == output && FrameSettings.multiplier() == multiplier
            && FrameSettings.quality() == quality && FrameSettings.flowWidth() == flow
            && FrameSettings.gpuBudgetMs() == gpu && FrameSettings.stallThresholdMs() == stall
            && FrameSettings.adaptivePacing() == adaptive && FrameSettings.lowFpsProtection() == low
            && FrameSettings.nativeFrameControl() == nativeControl && FrameSettings.diagnostics() == diagnostics;
    }

    private static void awaitStatus(String expected) throws Exception {
        long deadline = System.currentTimeMillis() + 4000L;
        while (System.currentTimeMillis() < deadline) {
            if (expected.equals(FramePacerProfiles.status())) return;
            Thread.sleep(40L);
        }
        check(expected.equals(FramePacerProfiles.status()), "status " + FramePacerProfiles.status() + " != " + expected);
    }

    private static void write(Path path, String text) throws IOException {
        Files.write(path, text.getBytes(StandardCharsets.UTF_8));
    }

    private static void check(boolean ok, String message) {
        if (!ok) {
            failures++;
            System.err.println("FAIL " + message);
        }
    }
}
