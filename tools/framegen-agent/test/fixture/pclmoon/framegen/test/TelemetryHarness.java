package pclmoon.framegen.test;

import java.lang.reflect.Method;
import pclmoon.framegen.bridge.FrameTelemetry;

/** Standalone telemetry seams. Does not require a GPU or publish a production setter. */
public final class TelemetryHarness {
    private static int failures;

    public static void main(String[] args) throws Exception {
        csv();
        lifecycle();
        if (failures != 0) throw new AssertionError(failures + " telemetry checks failed");
        System.out.println("telemetry harness ok");
    }

    private static void csv() throws Exception {
        Method parse = FrameTelemetry.class.getDeclaredMethod("parseGpuCsv", String.class, String.class, Double.TYPE, Double.TYPE, Long.TYPE);
        parse.setAccessible(true);
        FrameTelemetry.Snapshot unique = (FrameTelemetry.Snapshot) parse.invoke(null,
            "NVIDIA GeForce RTX 3060, 37, 61\r\n", "NVIDIA GeForce RTX 3060/PCIe/SSE2", 12.5, 4.0, 9L);
        check(unique.gpuPercent == 37 && unique.gpuTempC == 61 && "NVIDIA GeForce RTX 3060".equals(unique.gpuName)
            && unique.cpuPercent == 12.5 && unique.processCpuPercent == 4.0 && Double.isNaN(unique.cpuTempC)
            && unique.sampleNanos == 9L, "unique GPU CSV match");

        FrameTelemetry.Snapshot many = (FrameTelemetry.Snapshot) parse.invoke(null,
            "NVIDIA GeForce RTX 3060, 10, 40\nNVIDIA GeForce RTX 3060, 20, 50\n",
            "NVIDIA GeForce RTX 3060/PCIe/SSE2", 1, 2, 3L);
        check(Double.isNaN(many.gpuPercent) && Double.isNaN(many.gpuTempC) && "".equals(many.gpuName)
            && many.cpuPercent == 1 && Double.isNaN(many.cpuTempC), "duplicate GPU names stay N/A");

        FrameTelemetry.Snapshot other = (FrameTelemetry.Snapshot) parse.invoke(null,
            "NVIDIA GeForce GTX 1650, 80, 70\nNVIDIA GeForce RTX 4090, 5, 42\n",
            "NVIDIA GeForce RTX 4090/PCIe/SSE2", 8, 9, 4L);
        check(other.gpuPercent == 5 && other.gpuTempC == 42 && other.gpuName.indexOf("4090") >= 0, "selects the named GPU only");

        FrameTelemetry.Snapshot bad = (FrameTelemetry.Snapshot) parse.invoke(null,
            "NVIDIA GeForce RTX 3060, nope, [N/A]\n", "NVIDIA GeForce RTX 3060/PCIe/SSE2", 3, 4, 5L);
        check(Double.isNaN(bad.gpuPercent) && Double.isNaN(bad.gpuTempC) && bad.gpuName.indexOf("3060") >= 0
            && Double.isNaN(bad.cpuTempC), "illegal utilization and temperature stay NaN");

        FrameTelemetry.Snapshot intel = (FrameTelemetry.Snapshot) parse.invoke(null,
            "NVIDIA GeForce RTX 3060, 10, 40\n", "Intel(R) UHD Graphics 630", 6, 7, 8L);
        check(Double.isNaN(intel.gpuPercent) && Double.isNaN(intel.gpuTempC) && "".equals(intel.gpuName)
            && Double.isNaN(intel.cpuTempC), "non-NVIDIA renderer does not claim an NVIDIA card");
    }

    private static void lifecycle() throws Exception {
        FrameTelemetry.stop();
        FrameTelemetry.Snapshot before = FrameTelemetry.snapshot();
        check(Double.isNaN(before.cpuPercent) && Double.isNaN(before.gpuPercent) && "".equals(before.gpuName), "snapshot before request is empty");
        String renderer = "Intel(R) UHD Graphics";
        int baseline = samples();
        FrameTelemetry.request(renderer);
        FrameTelemetry.request(renderer);
        FrameTelemetry.Snapshot during = FrameTelemetry.snapshot();
        check(during != null && Double.isNaN(during.cpuTempC) && Double.isNaN(during.gpuTempC), "unavailable sensors remain NaN");
        long timeout = System.nanoTime() + 3000000000L;
        while (samples() == baseline && System.nanoTime() < timeout) Thread.sleep(10L);
        int first = samples();
        check(first == baseline + 1, "first HUD request starts one sample");
        for (int i = 0; i < 4; i++) { FrameTelemetry.request(renderer); Thread.sleep(100L); }
        check(samples() == first, "requests inside one second do not resample");
        timeout = System.nanoTime() + 3000000000L;
        while (samples() == first && System.nanoTime() < timeout) { FrameTelemetry.request(renderer); Thread.sleep(25L); }
        check(samples() == first + 1, "a later HUD request samples again");
        FrameTelemetry.stop();
        FrameTelemetry.Snapshot stopped = FrameTelemetry.snapshot();
        check(Double.isNaN(stopped.gpuPercent) && "".equals(stopped.gpuName) && Double.isNaN(stopped.cpuTempC), "stop clears cache");
        int afterStop = samples();
        Thread.sleep(400L);
        check(samples() == afterStop, "stop prevents the old worker from sampling");
        FrameTelemetry.request(renderer);
        Thread.sleep(250L);
        check(samples() == afterStop + 1, "request after stop starts a new worker");
        FrameTelemetry.Snapshot restarted = FrameTelemetry.snapshot();
        check(restarted != null && Double.isNaN(restarted.cpuTempC) && Double.isNaN(restarted.gpuTempC), "restart stays callable without GPU data");
        int hidden = samples();
        Thread.sleep(2200L);
        check(samples() == hidden, "sampler stops within two seconds without HUD requests");
        FrameTelemetry.request(null);
        FrameTelemetry.request("");
        Thread.sleep(250L);
        FrameTelemetry.Snapshot blank = FrameTelemetry.snapshot();
        check(blank != null && Double.isNaN(blank.gpuPercent) && "".equals(blank.gpuName), "missing renderer does not fail the call");
        FrameTelemetry.stop();
    }

    private static int samples() throws Exception {
        Method method = FrameTelemetry.class.getDeclaredMethod("sampleCount");
        method.setAccessible(true);
        return ((Integer) method.invoke(null)).intValue();
    }
    private static void check(boolean ok, String message) {
        if (ok) return;
        failures++;
        System.err.println("FAIL " + message);
    }
}
