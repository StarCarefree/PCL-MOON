package pclmoon.framegen.bridge;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.InputStream;
import java.lang.management.ManagementFactory;
import java.lang.reflect.Method;
import java.util.Locale;
import java.util.concurrent.atomic.AtomicReference;

/**
 * Request-driven Windows HUD telemetry. Snapshot never starts work or touches the OS.
 * Missing sensors stay NaN; GPU temperature is never substituted for CPU temperature.
 */
public final class FrameTelemetry {
    static final long SAMPLE_INTERVAL_NANOS = 1000000000L;
    static final long IDLE_STOP_NANOS = 2000000000L;
    static final int OUTPUT_LIMIT = 64 * 1024;
    static final long QUERY_TIMEOUT_NANOS = 1000000000L;

    private static final Snapshot EMPTY = new Snapshot(Double.NaN, Double.NaN, Double.NaN, Double.NaN, Double.NaN, "", 0L);
    private static volatile int sampleCount;
    private static final AtomicReference<Worker> ACTIVE = new AtomicReference<Worker>();
    private static volatile Snapshot cache = EMPTY;

    private FrameTelemetry() { }

    /** HUD heartbeat. Starts the daemon sampler only on the first request. */
    public static void request(String rendererName) {
        try {
            long now = System.nanoTime();
            Worker worker = ACTIVE.get();
            if (worker == null || !worker.alive()) {
                Worker created = new Worker(now, rendererName);
                if (ACTIVE.compareAndSet(worker, created)) created.start();
                else {
                    worker = ACTIVE.get();
                    if (worker != null) worker.request(now, rendererName);
                }
            } else worker.request(now, rendererName);
        } catch (Throwable ignored) { }
    }

    /** Latest immutable cache. Does not start a thread and does not perform I/O. */
    public static Snapshot snapshot() {
        Snapshot value = cache;
        return value == null ? EMPTY : value;
    }

    /** Stops this sampler, kills an in-flight child, and rejects stale publishes. */
    public static void stop() {
        try {
            synchronized (ACTIVE) {
                Worker worker = ACTIVE.getAndSet(null);
                cache = EMPTY;
                if (worker != null) worker.halt();
            }
        } catch (Throwable ignored) { }
    }

    public static final class Snapshot {
        public final double cpuPercent, processCpuPercent, gpuPercent, cpuTempC, gpuTempC;
        public final String gpuName;
        public final long sampleNanos;

        Snapshot(double cpuPercent, double processCpuPercent, double gpuPercent, double cpuTempC, double gpuTempC,
                 String gpuName, long sampleNanos) {
            this.cpuPercent = cpuPercent;
            this.processCpuPercent = processCpuPercent;
            this.gpuPercent = gpuPercent;
            this.cpuTempC = cpuTempC;
            this.gpuTempC = gpuTempC;
            this.gpuName = gpuName == null ? "" : gpuName;
            this.sampleNanos = sampleNanos;
        }
    }

    /** Package seam: unique NVIDIA name match, otherwise unavailable. Not a production publisher. */
    static Snapshot parseGpuCsv(String csv, String rendererName, double cpuPercent, double processCpuPercent, long sampleNanos) {
        int matches = 0;
        String name = "";
        double util = Double.NaN;
        double temp = Double.NaN;
        if (csv != null && isNvidia(rendererName)) {
            String needle = normalize(rendererName);
            String[] lines = csv.split("\r?\n");
            for (int i = 0; i < lines.length; i++) {
                String line = lines[i].trim();
                if (line.length() == 0) continue;
                String[] fields = splitCsv(line);
                if (fields.length < 3) continue;
                String candidate = unquote(fields[0]).trim();
                if (candidate.length() == 0 || !namesMatch(needle, normalize(candidate))) continue;
                double parsedUtil = percent(fields[1]);
                double parsedTemp = celsius(fields[2]);
                if (!Double.isFinite(parsedUtil) && !Double.isFinite(parsedTemp) && candidate.length() == 0) continue;
                matches++;
                name = candidate;
                util = parsedUtil;
                temp = parsedTemp;
            }
        }
        if (matches != 1) return new Snapshot(cpuPercent, processCpuPercent, Double.NaN, Double.NaN, Double.NaN, "", sampleNanos);
        return new Snapshot(cpuPercent, processCpuPercent, util, Double.NaN, temp, name, sampleNanos);
    }

    static boolean isNvidia(String rendererName) {
        return rendererName != null && rendererName.toLowerCase(Locale.ROOT).indexOf("nvidia") >= 0;
    }

    private static final class Worker implements Runnable {
        private final long generation;
        private final Thread thread;
        private final Object lock = new Object();
        private volatile boolean stopped;
        private volatile Process child;
        private long lastRequest;
        private String renderer = "";
        private boolean pending;

        Worker(long now, String rendererName) {
            generation = now;
            lastRequest = now;
            renderer = rendererName == null ? "" : rendererName;
            pending = true;
            thread = new Thread(this, "pclmoon-frame-telemetry");
            thread.setDaemon(true);
        }

        void start() { thread.start(); }

        boolean alive() { return !stopped && (thread.getState() == Thread.State.NEW || thread.isAlive()); }

        private long lastSample;

        void request(long now, String rendererName) {
            synchronized (lock) {
                if (stopped) return;
                lastRequest = now;
                if (rendererName != null) renderer = rendererName;
                pending = true;
                lock.notifyAll();
            }
        }
        void halt() {
            Process running;
            synchronized (lock) {
                stopped = true;
                running = child;
                lock.notifyAll();
            }
            destroy(running);
            thread.interrupt();
        }

        boolean current() { return !stopped && ACTIVE.get() == this; }

        public void run() {
            try {
                while (current()) {
                    String rendererName;
                    long requestedAt;
                    synchronized (lock) {
                        while (current() && !pending) {
                            long remain = IDLE_STOP_NANOS - (System.nanoTime() - lastRequest);
                            if (remain <= 0) return;
                            lock.wait(Math.max(1L, Math.min(remain, 200000000L) / 1000000L));
                        }
                        if (!current()) return;
                        pending = false;
                        rendererName = renderer;
                        requestedAt = lastRequest;
                        long due = lastSample == 0 ? 0 : SAMPLE_INTERVAL_NANOS - (System.nanoTime() - lastSample);
                        if (due > 0) {
                            pending = true;
                            lock.wait(Math.max(1L, due / 1000000L));
                            continue;
                        }
                    }
                    long started = System.nanoTime();
                    if (started - requestedAt > IDLE_STOP_NANOS) continue;
                    Sample cpu = readCpu();
                    Gpu gpu = isNvidia(rendererName) ? queryGpu(rendererName) : Gpu.NONE;
                    if (!current()) return;
                    synchronized (ACTIVE) {
                        if (ACTIVE.get() != this) return;
                        lastSample = System.nanoTime();
                        sampleCount++;
                        cache = new Snapshot(cpu.system, cpu.process, gpu.percent, Double.NaN, gpu.tempC, gpu.name, lastSample);
                    }
                }
            } catch (InterruptedException ignored) {
                Thread.currentThread().interrupt();
            } catch (Throwable ignored) {
            } finally {
                ACTIVE.compareAndSet(this, null);
            }
        }

        private Sample readCpu() {
            try {
                Class<?> type = Class.forName("com.sun.management.OperatingSystemMXBean");
                Object bean = ManagementFactory.getOperatingSystemMXBean();
                if (!type.isInstance(bean)) return Sample.NONE;
                return new Sample(load(type, bean, "getSystemCpuLoad"), load(type, bean, "getProcessCpuLoad"));
            } catch (Throwable ignored) { return Sample.NONE; }
        }

        private double load(Class<?> type, Object bean, String name) {
            try {
                Method method = type.getMethod(name);
                Object value = method.invoke(bean);
                if (!(value instanceof Number)) return Double.NaN;
                double load = ((Number) value).doubleValue();
                if (!Double.isFinite(load) || load < 0) return Double.NaN;
                return load * 100.0;
            } catch (Throwable ignored) { return Double.NaN; }
        }

        private Gpu queryGpu(String rendererName) {
            File executable = nvidiaSmi();
            if (executable == null) return Gpu.NONE;
            Process process = null;
            Thread drainer = null;
            try {
                ProcessBuilder builder = new ProcessBuilder(executable.getPath(),
                    "--query-gpu=name,utilization.gpu,temperature.gpu", "--format=csv,noheader,nounits");
                builder.redirectErrorStream(false);
                process = builder.start();
                synchronized (lock) { if (stopped) { destroy(process); return Gpu.NONE; } child = process; }
                ByteArrayOutputStream stdout = new ByteArrayOutputStream();
                ByteArrayOutputStream stderr = new ByteArrayOutputStream();
                drainer = pump(process.getErrorStream(), stderr);
                long deadline = System.nanoTime() + QUERY_TIMEOUT_NANOS;
                byte[] buffer = new byte[4096];
                InputStream input = process.getInputStream();
                while (current() && System.nanoTime() < deadline) {
                    int available = input.available();
                    if (available > 0) {
                        int count = input.read(buffer, 0, Math.min(buffer.length, available));
                        if (count < 0) break;
                        if (stdout.size() < OUTPUT_LIMIT) stdout.write(buffer, 0, Math.min(count, OUTPUT_LIMIT - stdout.size()));
                    } else if (!process.isAlive()) break;
                    else Thread.sleep(15L);
                }
                drainAvailable(input, stdout);
                if (process.isAlive() || stdout.size() >= OUTPUT_LIMIT) { destroy(process); return Gpu.NONE; }
                if (process.exitValue() != 0) return Gpu.NONE;
                String text = new String(stdout.toByteArray(), "UTF-8");
                Snapshot parsed = parseGpuCsv(text, rendererName, Double.NaN, Double.NaN, 0L);
                return new Gpu(parsed.gpuName, parsed.gpuPercent, parsed.gpuTempC);
            } catch (Throwable ignored) {
                return Gpu.NONE;
            } finally {
                synchronized (lock) { if (child == process) child = null; }
                destroy(process);
                if (drainer != null) drainer.interrupt();
            }
        }
    }

    private static Thread pump(final InputStream input, final ByteArrayOutputStream sink) {
        Thread thread = new Thread(new Runnable() {
            public void run() { drainAvailable(input, sink); try { while (input.read() >= 0) { } } catch (Throwable ignored) { } }
        }, "pclmoon-frame-telemetry-stderr");
        thread.setDaemon(true);
        thread.start();
        return thread;
    }

    private static void drainAvailable(InputStream input, ByteArrayOutputStream sink) {
        try {
            byte[] buffer = new byte[1024];
            while (input.available() > 0 && sink.size() < OUTPUT_LIMIT) {
                int count = input.read(buffer, 0, Math.min(buffer.length, input.available()));
                if (count < 0) return;
                sink.write(buffer, 0, Math.min(count, OUTPUT_LIMIT - sink.size()));
            }
        } catch (Throwable ignored) { }
    }

    private static void destroy(Process process) {
        if (process == null) return;
        try { process.destroy(); } catch (Throwable ignored) { }
        try {
            Method force = Process.class.getMethod("destroyForcibly");
            force.invoke(process);
        } catch (Throwable ignored) { }
    }

    static File nvidiaSmi() {
        String root = System.getenv("SystemRoot");
        if (root != null) {
            File system = new File(root + "\\System32\\nvidia-smi.exe");
            if (system.isFile()) return system;
        }
        String programs = System.getenv("ProgramFiles");
        if (programs != null) {
            File official = new File(programs + "\\NVIDIA Corporation\\NVSMI\\nvidia-smi.exe");
            if (official.isFile()) return official;
        }
        return null;
    }

    private static String[] splitCsv(String line) {
        int commas = 0;
        for (int i = 0; i < line.length(); i++) if (line.charAt(i) == ',') commas++;
        String[] fields = new String[commas + 1];
        int start = 0, index = 0;
        for (int i = 0; i <= line.length(); i++) {
            if (i == line.length() || line.charAt(i) == ',') {
                fields[index++] = line.substring(start, i);
                start = i + 1;
            }
        }
        return fields;
    }

    private static String unquote(String value) {
        String text = value == null ? "" : value.trim();
        if (text.length() >= 2 && text.charAt(0) == '"' && text.charAt(text.length() - 1) == '"')
            return text.substring(1, text.length() - 1).replace("\"\"", "\"");
        return text;
    }

    private static String normalize(String value) {
        String text = value == null ? "" : value.toLowerCase(Locale.ROOT);
        StringBuilder out = new StringBuilder(text.length());
        for (int i = 0; i < text.length(); i++) {
            char c = text.charAt(i);
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) out.append(c);
        }
        return out.toString();
    }

    private static boolean namesMatch(String renderer, String gpu) {
        return renderer.length() > 0 && gpu.length() > 0 && (renderer.indexOf(gpu) >= 0 || gpu.indexOf(renderer) >= 0);
    }

    private static double percent(String text) {
        double value = number(text);
        return Double.isFinite(value) && value >= 0 && value <= 100 ? value : Double.NaN;
    }

    private static double celsius(String text) {
        double value = number(text);
        return Double.isFinite(value) && value > -100 && value < 200 ? value : Double.NaN;
    }

    private static double number(String text) {
        if (text == null) return Double.NaN;
        String trimmed = text.trim();
        if (trimmed.length() == 0 || "n/a".equalsIgnoreCase(trimmed) || "[n/a]".equalsIgnoreCase(trimmed)) return Double.NaN;
        try { return Double.parseDouble(trimmed); } catch (NumberFormatException ignored) { return Double.NaN; }
    }

    /** Package seam for the harness. Counts completed sample attempts, not a publisher. */
    static int sampleCount() { return sampleCount; }

    private static final class Sample {
        static final Sample NONE = new Sample(Double.NaN, Double.NaN);
        final double system, process;
        Sample(double system, double process) { this.system = system; this.process = process; }
    }

    private static final class Gpu {
        static final Gpu NONE = new Gpu("", Double.NaN, Double.NaN);
        final String name;
        final double percent, tempC;
        Gpu(String name, double percent, double tempC) {
            this.name = name == null ? "" : name;
            this.percent = percent;
            this.tempC = tempC;
        }
    }
}
