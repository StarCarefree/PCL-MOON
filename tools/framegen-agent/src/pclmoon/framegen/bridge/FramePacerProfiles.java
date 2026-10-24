package pclmoon.framegen.bridge;

import java.io.BufferedReader;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.StringReader;
import java.nio.ByteBuffer;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.CharsetDecoder;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.LinkOption;
import java.nio.file.OpenOption;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.nio.file.StandardCopyOption;
import java.nio.file.StandardOpenOption;
import java.nio.file.attribute.BasicFileAttributes;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.atomic.AtomicLong;
import java.util.concurrent.atomic.AtomicReference;

/**
 * Global plus per-instance UTF-8 {@code .properties} overlay.
 * File IO stays on one daemon worker. {@link #applyPending()} only publishes an already parsed snapshot.
 */
public final class FramePacerProfiles {
    private static final long MAX_BYTES = 1024L * 1024L;
    private static final long POLL_NANOS = 1000000000L;
    private static final LinkOption NOFOLLOW = LinkOption.NOFOLLOW_LINKS;
    private static final OpenOption[] READ = new OpenOption[] {
        StandardOpenOption.READ, LinkOption.NOFOLLOW_LINKS
    };

    private static final Object GATE = new Object();
    private static final AtomicLong EPOCH = new AtomicLong();

    private static volatile String statusText = "";
    private static volatile String instanceDisplay = "";
    private static final AtomicReference<Snapshot> pending = new AtomicReference<Snapshot>();
    private static volatile Worker worker;

    private FramePacerProfiles() { }

    /** Capture the current session as the inherited default, then start (or retarget) the worker. */
    public static void configure(String globalPath, String instancePath) {
        final Defaults defaults = Defaults.capture();
        final long epoch = EPOCH.incrementAndGet();
        final String global = blank(globalPath) ? null : globalPath;
        final String instance = blank(instancePath) ? null : instancePath;
        Worker previous;
        synchronized (GATE) {
            previous = worker;
            worker = null;
            pending.set(null);
            if (instance == null) {
                instanceDisplay = "";
                statusText = "";
            } else {
                instanceDisplay = instance;
                statusText = "读取中";
            }
        }
        if (previous != null) previous.retire();
        if (instance == null && global == null) return;
        Worker next = new Worker(epoch, global, instance, defaults);
        synchronized (GATE) {
            if (EPOCH.get() != epoch) {
                next.retire();
                return;
            }
            worker = next;
        }
        next.start();
    }

    /** Render thread: apply one immutable snapshot. Does not touch the filesystem. */
    public static void applyPending() {
        synchronized (GATE) {
            Snapshot snap = pending.getAndSet(null);
            if (snap == null || snap.epoch != EPOCH.get()) return;
            try { snap.apply(); }
            catch (Throwable ignored) { statusText = "应用失败"; }
        }
    }

    /** Capture UI settings immediately, then queue an atomic UTF-8 replace of the instance file only. */
    public static void saveInstance() {
        final Defaults captured = Defaults.capture();
        Worker current = worker;
        if (current == null || current.instance == null) {
            statusText = instanceDisplay.length() == 0 ? "" : "未配置";
            return;
        }
        current.enqueue(new Save(captured));
    }

    /** Remove only the instance file and queue a global-or-default snapshot. Does not write the global file. */
    public static void resetInstance() {
        Worker current = worker;
        if (current == null || current.instance == null) {
            statusText = instanceDisplay.length() == 0 ? "" : "未配置";
            return;
        }
        current.enqueue(new Reset());
    }

    public static String status() {
        String value = statusText;
        return value == null ? "" : value;
    }

    public static String profilePath() {
        String value = instanceDisplay;
        return value == null ? "" : value;
    }

    public static void close() {
        EPOCH.incrementAndGet();
        Worker previous;
        synchronized (GATE) {
            previous = worker;
            worker = null;
            pending.set(null);
            instanceDisplay = "";
            statusText = "";
        }
        if (previous != null) previous.retire();
    }

    private static boolean blank(String value) {
        if (value == null) return true;
        for (int i = 0; i < value.length(); i++) {
            if (!Character.isWhitespace(value.charAt(i))) return false;
        }
        return true;
    }

    private static final class Worker extends Thread {
        final long epoch;
        final String global;
        final String instance;
        final Defaults defaults;
        private final Object inbox = new Object();
        private final List<Op> ops = new ArrayList<Op>();
        private volatile boolean retired;

        Worker(long epoch, String global, String instance, Defaults defaults) {
            super("pclmoon-framegen-profiles");
            setDaemon(true);
            this.epoch = epoch;
            this.global = global;
            this.instance = instance;
            this.defaults = defaults;
        }

        void retire() {
            retired = true;
            interrupt();
        }

        void enqueue(Op op) {
            if (retired || epoch != EPOCH.get()) return;
            synchronized (inbox) {
                if (retired) return;
                ops.add(op);
                inbox.notifyAll();
            }
        }

        public void run() {
            FileStamp globalStamp = FileStamp.missing();
            FileStamp instanceStamp = FileStamp.missing();
            Map<String, String> globalKeys = new LinkedHashMap<String, String>();
            Map<String, String> instanceKeys = new LinkedHashMap<String, String>();
            String appliedSemantic = null;
            boolean first = true;
            long nextPoll = 0L;
            while (!retired && epoch == EPOCH.get()) {
                List<Op> batch = drain();
                if (!batch.isEmpty()) {
                    for (int i = 0; i < batch.size(); i++) {
                        if (retired || epoch != EPOCH.get()) return;
                        Op op = batch.get(i);
                        try {
                            if (op instanceof Save) appliedSemantic = save((Save) op, globalKeys);
                            else appliedSemantic = reset(globalKeys);
                        } catch (Throwable failure) {
                            statusText = "保存失败";
                        }
                        instanceStamp = stamp(instance);
                        instanceKeys = readMap(instance);
                    }
                    first = false;
                    nextPoll = System.nanoTime() + POLL_NANOS;
                    continue;
                }
                long now = System.nanoTime();
                if (!first && now < nextPoll) {
                    long waitMs = Math.max(1L, (nextPoll - now) / 1000000L);
                    synchronized (inbox) {
                        if (ops.isEmpty() && !retired) {
                            try { inbox.wait(Math.min(waitMs, 1000L)); }
                            catch (InterruptedException ignored) { interrupt(); }
                        }
                    }
                    continue;
                }
                try {
                    FileStamp nextGlobal = stamp(global);
                    FileStamp nextInstance = stamp(instance);
                    boolean changed = first || !nextGlobal.same(globalStamp) || !nextInstance.same(instanceStamp);
                    if (changed) {
                        if (!nextGlobal.same(globalStamp) || first) globalKeys = readMap(global);
                        if (!nextInstance.same(instanceStamp) || first) instanceKeys = readMap(instance);
                        globalStamp = nextGlobal;
                        instanceStamp = nextInstance;
                        String semantic = semantic(globalKeys, instanceKeys, defaults);
                        if (first || appliedSemantic == null || !semantic.equals(appliedSemantic)) {
                            publish(parse(globalKeys, instanceKeys, defaults));
                            appliedSemantic = semantic;
                            statusText = nextInstance.exists && !nextInstance.rejected ? "已加载"
                                : (nextGlobal.exists && !nextGlobal.rejected ? "全局" : "默认");
                        }
                    }
                    first = false;
                } catch (Throwable failure) {
                    statusText = "读取失败";
                    first = false;
                }
                nextPoll = System.nanoTime() + POLL_NANOS;
            }
        }

        private List<Op> drain() {
            synchronized (inbox) {
                if (ops.isEmpty()) return java.util.Collections.emptyList();
                List<Op> batch = new ArrayList<Op>(ops);
                ops.clear();
                return batch;
            }
        }

        private String save(Save op, Map<String, String> globalKeys) throws IOException {
            if (instance == null) {
                statusText = "未配置";
                return semantic(globalKeys, new LinkedHashMap<String, String>(), defaults);
            }
            Path path = Paths.get(instance);
            Path parent = path.getParent();
            if (Files.exists(path, NOFOLLOW) && isUnsafe(path)) throw new IOException("Unsafe instance profile");
            if (parent != null && Files.isSymbolicLink(parent)) throw new IOException("Unsafe profile directory");
            if (parent != null) Files.createDirectories(parent);
            Map<String, String> written = op.captured.toInstanceMap();
            byte[] body = encode(written);
            Path dir = parent == null ? path.toAbsolutePath().getParent() : parent;
            Path temp = Files.createTempFile(dir, ".framegen-", ".tmp");
            try {
                Files.write(temp, body);
                try {
                    Files.move(temp, path, StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE);
                } catch (IOException atomic) {
                    Files.move(temp, path, StandardCopyOption.REPLACE_EXISTING);
                }
            } finally {
                Files.deleteIfExists(temp);
            }
            statusText = "已保存";
            return semantic(globalKeys, written, defaults);
        }

        private String reset(Map<String, String> globalKeys) {
            if (instance != null) {
                try {
                    Path path = Paths.get(instance);
                    if (Files.isSymbolicLink(path)) statusText = "已忽略";
                    else {
                        Files.deleteIfExists(path);
                        statusText = "已重置";
                    }
                } catch (Throwable failure) {
                    statusText = "重置失败";
                }
            }
            Map<String, String> empty = new LinkedHashMap<String, String>();
            publish(parse(globalKeys, empty, defaults));
            return semantic(globalKeys, empty, defaults);
        }

        private void publish(Snapshot snap) {
            synchronized (GATE) {
                if (retired || epoch != EPOCH.get()) return;
                pending.set(snap);
            }
        }

        private Map<String, String> readMap(String location) {
            return read(location).keys;
        }

        private FileStamp stamp(String location) {
            if (location == null) return FileStamp.missing();
            try {
                Path path = Paths.get(location);
                if (!Files.exists(path, NOFOLLOW)) return FileStamp.missing();
                BasicFileAttributes attr = Files.readAttributes(path, BasicFileAttributes.class, NOFOLLOW);
                if (!attr.isRegularFile() || attr.size() > MAX_BYTES) {
                    return FileStamp.rejected(attr.lastModifiedTime().toMillis(), attr.size());
                }
                return new FileStamp(true, false, attr.lastModifiedTime().toMillis(), attr.size());
            } catch (Throwable ignored) {
                return FileStamp.missing();
            }
        }
    }

    private static Parsed read(String location) {
        if (location == null) return Parsed.empty();
        Path path;
        try { path = Paths.get(location); }
        catch (Throwable ignored) { return Parsed.rejected(); }
        try {
            if (!Files.exists(path, NOFOLLOW)) return Parsed.empty();
            BasicFileAttributes attr = Files.readAttributes(path, BasicFileAttributes.class, NOFOLLOW);
            if (!attr.isRegularFile() || attr.isSymbolicLink() || attr.size() < 0 || attr.size() > MAX_BYTES) return Parsed.rejected();
            if (isUnsafe(path)) return Parsed.rejected();
            byte[] raw;
            try (InputStream in = Files.newInputStream(path, READ)) {
                raw = readBounded(in, MAX_BYTES);
            }
            if (raw == null) return Parsed.rejected();
            String text = utf8(raw);
            if (text == null) return Parsed.rejected();
            return new Parsed(parseProperties(text), false);
        } catch (Throwable ignored) {
            return Parsed.rejected();
        }
    }

    private static byte[] readBounded(InputStream in, long limit) throws IOException {
        byte[] buf = new byte[8192];
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        long total = 0;
        for (int n; (n = in.read(buf)) != -1;) {
            total += n;
            if (total > limit) return null;
            out.write(buf, 0, n);
        }
        return out.toByteArray();
    }

    private static String utf8(byte[] raw) {
        int offset = 0;
        if (raw.length >= 3 && (raw[0] & 0xFF) == 0xEF && (raw[1] & 0xFF) == 0xBB && (raw[2] & 0xFF) == 0xBF) offset = 3;
        CharsetDecoder decoder = StandardCharsets.UTF_8.newDecoder()
            .onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT);
        try {
            return decoder.decode(ByteBuffer.wrap(raw, offset, raw.length - offset)).toString();
        } catch (CharacterCodingException ignored) {
            return null;
        }
    }

    private static boolean isUnsafe(Path path) {
        try {
            if (Files.isSymbolicLink(path)) return true;
            BasicFileAttributes attr = Files.readAttributes(path, BasicFileAttributes.class, NOFOLLOW);
            if (attr.isSymbolicLink() || !attr.isRegularFile()) return true;
            Path parent = path.getParent();
            if (parent != null) {
                if (Files.isSymbolicLink(parent)) return true;
                if (!Files.isDirectory(parent, NOFOLLOW)) return true;
            }
            return false;
        } catch (Throwable ignored) {
            return true;
        }
    }

    private static Map<String, String> parseProperties(String text) throws IOException {
        java.util.Properties properties = new java.util.Properties();
        properties.load(new StringReader(text));
        Map<String, String> keys = new LinkedHashMap<String, String>();
        for (String name : properties.stringPropertyNames())
            keys.put(name.trim().toLowerCase(Locale.ROOT), properties.getProperty(name).trim());
        return keys;
    }

    private static String semantic(Map<String, String> globalKeys, Map<String, String> instanceKeys, Defaults defaults) {
        Snapshot s = parse(globalKeys, instanceKeys, defaults);
        return s.fpsLimit + "," + s.waitMode + "," + s.telemetry + "," + s.showFps + "," + s.generation
            + "," + s.outputFps + "," + s.multiplier + "," + s.quality + "," + s.flowWidth + "," + s.gpuBudgetMs
            + "," + s.stallThresholdMs + "," + s.adaptive + "," + s.lowFpsProtection + "," + s.nativeFrameControl
            + "," + s.diagnostics + "," + s.superResolution + "," + s.renderScale + "," + s.upscaleSharpness;
    }

    private static Snapshot parse(Map<String, String> globalKeys, Map<String, String> instanceKeys, Defaults defaults) {
        IntValue fps = IntValue.missing();
        IntValue mode = IntValue.missing();
        BoolValue telemetry = BoolValue.missing();
        BoolValue hud = BoolValue.missing();
        BoolValue generation = BoolValue.missing();
        IntValue output = IntValue.missing();
        IntValue multiplier = IntValue.missing();
        IntValue quality = IntValue.missing();
        IntValue flow = IntValue.missing();
        IntValue gpu = IntValue.missing();
        IntValue stall = IntValue.missing();
        BoolValue adaptive = BoolValue.missing();
        BoolValue low = BoolValue.missing();
        BoolValue nativeControl = BoolValue.missing();
        BoolValue diagnostics = BoolValue.missing();
        BoolValue superResolution = BoolValue.missing();
        IntValue renderScale = IntValue.missing();
        IntValue sharpness = IntValue.missing();
        overlayCap(globalKeys, true, fps);
        overlayCap(instanceKeys, false, fps);
        mode = overlayMode(globalKeys, mode);
        mode = overlayMode(instanceKeys, mode);
        telemetry = overlayBool(globalKeys, "telemetry", telemetry);
        telemetry = overlayBool(instanceKeys, "telemetry", telemetry);
        hud = overlayBool(globalKeys, "hud", hud);
        hud = overlayBool(instanceKeys, "hud", hud);
        generation = overlayBool(globalKeys, "generation", generation);
        generation = overlayBool(instanceKeys, "generation", generation);
        output = overlayInt(globalKeys, "output_fps", 30, 240, output);
        output = overlayInt(instanceKeys, "output_fps", 30, 240, output);
        multiplier = overlayInt(globalKeys, "multiplier", 2, 4, multiplier);
        multiplier = overlayInt(instanceKeys, "multiplier", 2, 4, multiplier);
        quality = overlayInt(globalKeys, "quality", 0, 2, quality);
        quality = overlayInt(instanceKeys, "quality", 0, 2, quality);
        flow = overlayInt(globalKeys, "flow_width", 80, 320, flow);
        flow = overlayInt(instanceKeys, "flow_width", 80, 320, flow);
        gpu = overlayInt(globalKeys, "gpu_budget_ms", 2, 20, gpu);
        gpu = overlayInt(instanceKeys, "gpu_budget_ms", 2, 20, gpu);
        stall = overlayInt(globalKeys, "stall_threshold_ms", 50, 500, stall);
        stall = overlayInt(instanceKeys, "stall_threshold_ms", 50, 500, stall);
        adaptive = overlayBool(globalKeys, "adaptive", adaptive);
        adaptive = overlayBool(instanceKeys, "adaptive", adaptive);
        low = overlayBool(globalKeys, "low_fps_protection", low);
        low = overlayBool(instanceKeys, "low_fps_protection", low);
        nativeControl = overlayBool(globalKeys, "native_frame_control", nativeControl);
        nativeControl = overlayBool(instanceKeys, "native_frame_control", nativeControl);
        diagnostics = overlayBool(globalKeys, "diagnostics", diagnostics);
        diagnostics = overlayBool(instanceKeys, "diagnostics", diagnostics);
        superResolution = overlayBool(globalKeys, "super_resolution", superResolution);
        superResolution = overlayBool(instanceKeys, "super_resolution", superResolution);
        renderScale = overlayInt(globalKeys, "render_scale", 50, 100, renderScale);
        renderScale = overlayInt(instanceKeys, "render_scale", 50, 100, renderScale);
        sharpness = overlayInt(globalKeys, "upscale_sharpness", 0, 100, sharpness);
        sharpness = overlayInt(instanceKeys, "upscale_sharpness", 0, 100, sharpness);
        return new Snapshot(EPOCH.get(),
            fps.present ? fps.value : defaults.fpsLimit,
            mode.present ? mode.value : defaults.waitMode,
            telemetry.present ? telemetry.value : defaults.telemetry,
            hud.present ? hud.value : defaults.showFps,
            generation.present ? generation.value : defaults.generation,
            output.present ? output.value : defaults.outputFps,
            multiplier.present ? multiplier.value : defaults.multiplier,
            quality.present ? quality.value : defaults.quality,
            flow.present ? flow.value : defaults.flowWidth,
            gpu.present ? gpu.value : defaults.gpuBudgetMs,
            stall.present ? stall.value : defaults.stallThresholdMs,
            adaptive.present ? adaptive.value : defaults.adaptive,
            low.present ? low.value : defaults.lowFpsProtection,
            nativeControl.present ? nativeControl.value : defaults.nativeFrameControl,
            diagnostics.present ? diagnostics.value : defaults.diagnostics,
            superResolution.present ? superResolution.value : defaults.superResolution,
            renderScale.present ? renderScale.value : defaults.renderScale,
            sharpness.present ? sharpness.value : defaults.upscaleSharpness);
    }

    private static void overlayCap(Map<String, String> keys, boolean global, IntValue target) {
        if (global && keys.containsKey("global_fps_limit")) applyCap(keys.get("global_fps_limit"), target);
        if (keys.containsKey("fps_limit")) applyCap(keys.get("fps_limit"), target);
    }

    /** Invalid cap fails open to 0. Valid values are off, 0, or 1..999. */
    private static void applyCap(String raw, IntValue target) {
        String text = raw == null ? "" : raw.trim().toLowerCase(Locale.ROOT);
        if ("off".equals(text)) { target.present = true; target.value = 0; return; }
        Integer n = integer(text);
        if (n != null && n.intValue() >= 0 && n.intValue() <= 999) {
            target.present = true;
            target.value = n.intValue();
            return;
        }
        target.present = true;
        target.value = 0;
    }

    private static IntValue overlayMode(Map<String, String> keys, IntValue inherited) {
        if (!keys.containsKey("pacing_mode")) return inherited;
        String text = keys.get("pacing_mode").trim().toLowerCase(Locale.ROOT);
        if ("efficient".equals(text)) return IntValue.of(0);
        if ("balanced".equals(text)) return IntValue.of(1);
        if ("precise".equals(text)) return IntValue.of(2);
        return inherited;
    }

    private static IntValue overlayInt(Map<String, String> keys, String name, int min, int max, IntValue inherited) {
        if (!keys.containsKey(name)) return inherited;
        Integer n = integer(keys.get(name).trim());
        if (n == null || n.intValue() < min || n.intValue() > max) return inherited;
        return IntValue.of(n.intValue());
    }

    private static BoolValue overlayBool(Map<String, String> keys, String name, BoolValue inherited) {
        if (!keys.containsKey(name)) return inherited;
        String text = keys.get(name).trim().toLowerCase(Locale.ROOT);
        if ("1".equals(text) || "on".equals(text) || "true".equals(text) || "yes".equals(text)) return BoolValue.of(true);
        if ("0".equals(text) || "off".equals(text) || "false".equals(text) || "no".equals(text)) return BoolValue.of(false);
        return inherited;
    }

    private static Integer integer(String text) {
        if (text == null || text.length() == 0) return null;
        int sign = 1;
        int i = 0;
        if (text.charAt(0) == '+') i++;
        else if (text.charAt(0) == '-') { sign = -1; i++; }
        if (i >= text.length()) return null;
        int value = 0;
        for (; i < text.length(); i++) {
            char c = text.charAt(i);
            if (c < '0' || c > '9') return null;
            value = value * 10 + (c - '0');
            if (value > 100000) return null;
        }
        return Integer.valueOf(sign * value);
    }

    private static byte[] encode(Map<String, String> keys) {
        StringBuilder out = new StringBuilder();
        out.append("# PCL FrameGen instance profile\n");
        String[] names = Defaults.INSTANCE_KEYS;
        for (int i = 0; i < names.length; i++) {
            if (!keys.containsKey(names[i])) continue;
            out.append(names[i]).append('=').append(escape(keys.get(names[i]))).append('\n');
        }
        return out.toString().getBytes(StandardCharsets.UTF_8);
    }

    private static String escape(String value) {
        StringBuilder out = new StringBuilder(value.length());
        for (int i = 0; i < value.length(); i++) {
            char c = value.charAt(i);
            if (c == '\\' || c == '=' || c == ':' || c == '#' || c == '!') out.append('\\').append(c);
            else if (c == '\n') out.append("\\n");
            else if (c == '\r') out.append("\\r");
            else out.append(c);
        }
        return out.toString();
    }

    static final class Snapshot {
        final long epoch;
        final int fpsLimit;
        final int waitMode;
        final boolean telemetry;
        final boolean showFps;
        final boolean generation;
        final int outputFps;
        final int multiplier;
        final int quality;
        final int flowWidth;
        final int gpuBudgetMs;
        final int stallThresholdMs;
        final boolean adaptive;
        final boolean lowFpsProtection;
        final boolean nativeFrameControl;
        final boolean diagnostics;
        final boolean superResolution;
        final int renderScale;
        final int upscaleSharpness;

        Snapshot(long epoch, int fpsLimit, int waitMode, boolean telemetry, boolean showFps, boolean generation,
                 int outputFps, int multiplier, int quality, int flowWidth, int gpuBudgetMs, int stallThresholdMs,
                 boolean adaptive, boolean lowFpsProtection, boolean nativeFrameControl, boolean diagnostics,
                 boolean superResolution, int renderScale, int upscaleSharpness) {
            this.epoch = epoch;
            this.fpsLimit = fpsLimit;
            this.waitMode = waitMode;
            this.telemetry = telemetry;
            this.showFps = showFps;
            this.generation = generation;
            this.outputFps = outputFps;
            this.multiplier = multiplier;
            this.quality = quality;
            this.flowWidth = flowWidth;
            this.gpuBudgetMs = gpuBudgetMs;
            this.stallThresholdMs = stallThresholdMs;
            this.adaptive = adaptive;
            this.lowFpsProtection = lowFpsProtection;
            this.nativeFrameControl = nativeFrameControl;
            this.diagnostics = diagnostics;
            this.superResolution = superResolution;
            this.renderScale = renderScale;
            this.upscaleSharpness = upscaleSharpness;
        }

        void apply() {
            PacingSettings.apply(fpsLimit, waitMode, telemetry);
            FrameSettings.setShowFps(showFps);
            FrameSettings.setGeneration(generation);
            FrameSettings.setOutputFps(outputFps);
            FrameSettings.setMultiplier(multiplier);
            FrameSettings.setQuality(quality);
            FrameSettings.setFlowWidth(flowWidth);
            FrameSettings.setGpuBudgetMs(gpuBudgetMs);
            FrameSettings.setStallThresholdMs(stallThresholdMs);
            FrameSettings.setAdaptivePacing(adaptive);
            FrameSettings.setLowFpsProtection(lowFpsProtection);
            FrameSettings.setNativeFrameControl(nativeFrameControl);
            FrameSettings.setDiagnostics(diagnostics);
            UpscaleSettings.apply(superResolution, renderScale, upscaleSharpness);
        }
    }

    private static final class Defaults {
        static final String[] KEYS = {
            "fps_limit", "global_fps_limit", "pacing_mode", "telemetry", "hud", "generation",
            "output_fps", "multiplier", "quality", "flow_width", "gpu_budget_ms", "stall_threshold_ms",
            "adaptive", "low_fps_protection", "native_frame_control", "diagnostics",
            "super_resolution", "render_scale", "upscale_sharpness"
        };
        static final String[] INSTANCE_KEYS = {
            "fps_limit", "pacing_mode", "telemetry", "hud", "generation",
            "output_fps", "multiplier", "quality", "flow_width", "gpu_budget_ms", "stall_threshold_ms",
            "adaptive", "low_fps_protection", "native_frame_control", "diagnostics",
            "super_resolution", "render_scale", "upscale_sharpness"
        };
        final int fpsLimit, waitMode, outputFps, multiplier, quality, flowWidth, gpuBudgetMs, stallThresholdMs;
        final boolean telemetry, showFps, generation, adaptive, lowFpsProtection, nativeFrameControl, diagnostics;
        final boolean superResolution;
        final int renderScale, upscaleSharpness;

        Defaults(int fpsLimit, int waitMode, boolean telemetry, boolean showFps, boolean generation,
                 int outputFps, int multiplier, int quality, int flowWidth, int gpuBudgetMs, int stallThresholdMs,
                 boolean adaptive, boolean lowFpsProtection, boolean nativeFrameControl, boolean diagnostics,
                 boolean superResolution, int renderScale, int upscaleSharpness) {
            this.fpsLimit = fpsLimit;
            this.waitMode = waitMode;
            this.telemetry = telemetry;
            this.showFps = showFps;
            this.generation = generation;
            this.outputFps = outputFps;
            this.multiplier = multiplier;
            this.quality = quality;
            this.flowWidth = flowWidth;
            this.gpuBudgetMs = gpuBudgetMs;
            this.stallThresholdMs = stallThresholdMs;
            this.adaptive = adaptive;
            this.lowFpsProtection = lowFpsProtection;
            this.nativeFrameControl = nativeFrameControl;
            this.diagnostics = diagnostics;
            this.superResolution = superResolution;
            this.renderScale = renderScale;
            this.upscaleSharpness = upscaleSharpness;
        }


        static Defaults capture() {
            UpscaleSettings.Values upscale = UpscaleSettings.snapshot();
            return new Defaults(PacingSettings.fpsLimit(), PacingSettings.waitMode(), PacingSettings.telemetry(),
                FrameSettings.showFps(), FrameSettings.generation(), FrameSettings.outputFps(), FrameSettings.multiplier(),
                FrameSettings.quality(), FrameSettings.flowWidth(), FrameSettings.gpuBudgetMs(), FrameSettings.stallThresholdMs(),
                FrameSettings.adaptivePacing(), FrameSettings.lowFpsProtection(), FrameSettings.nativeFrameControl(),
                FrameSettings.diagnostics(), upscale.enabled, upscale.scalePercent, upscale.sharpnessPercent);
        }

        Map<String, String> toInstanceMap() {
            Map<String, String> keys = new LinkedHashMap<String, String>();
            keys.put("fps_limit", fpsLimit == 0 ? "off" : Integer.toString(fpsLimit));
            keys.put("pacing_mode", waitMode == 0 ? "efficient" : waitMode == 2 ? "precise" : "balanced");
            keys.put("telemetry", telemetry ? "on" : "off");
            keys.put("hud", showFps ? "on" : "off");
            keys.put("generation", generation ? "on" : "off");
            keys.put("output_fps", Integer.toString(outputFps));
            keys.put("multiplier", Integer.toString(multiplier));
            keys.put("quality", Integer.toString(quality));
            keys.put("flow_width", Integer.toString(flowWidth));
            keys.put("gpu_budget_ms", Integer.toString(gpuBudgetMs));
            keys.put("stall_threshold_ms", Integer.toString(stallThresholdMs));
            keys.put("adaptive", adaptive ? "on" : "off");
            keys.put("low_fps_protection", lowFpsProtection ? "on" : "off");
            keys.put("native_frame_control", nativeFrameControl ? "on" : "off");
            keys.put("diagnostics", diagnostics ? "on" : "off");
            keys.put("super_resolution", superResolution ? "on" : "off");
            keys.put("render_scale", Integer.toString(renderScale));
            keys.put("upscale_sharpness", Integer.toString(upscaleSharpness));
            return keys;
        }

        Snapshot toSnapshot(long epoch) {
            return new Snapshot(epoch, fpsLimit, waitMode, telemetry, showFps, generation, outputFps, multiplier,
                quality, flowWidth, gpuBudgetMs, stallThresholdMs, adaptive, lowFpsProtection, nativeFrameControl,
                diagnostics, superResolution, renderScale, upscaleSharpness);
        }
    }

    private static final class IntValue {
        boolean present;
        int value;
        static IntValue missing() { return new IntValue(); }
        static IntValue of(int value) { IntValue v = new IntValue(); v.present = true; v.value = value; return v; }
    }

    private static final class BoolValue {
        boolean present;
        boolean value;
        static BoolValue missing() { return new BoolValue(); }
        static BoolValue of(boolean value) { BoolValue v = new BoolValue(); v.present = true; v.value = value; return v; }
    }

    private static final class FileStamp {
        final boolean exists;
        final boolean rejected;
        final long mtime;
        final long size;
        FileStamp(boolean exists, boolean rejected, long mtime, long size) {
            this.exists = exists; this.rejected = rejected; this.mtime = mtime; this.size = size;
        }
        static FileStamp missing() { return new FileStamp(false, false, 0L, 0L); }
        static FileStamp rejected(long mtime, long size) { return new FileStamp(true, true, mtime, size); }
        boolean same(FileStamp other) {
            return exists == other.exists && rejected == other.rejected && mtime == other.mtime && size == other.size;
        }
    }

    private static final class Parsed {
        final Map<String, String> keys;
        final boolean rejected;
        Parsed(Map<String, String> keys, boolean rejected) { this.keys = keys; this.rejected = rejected; }
        static Parsed empty() { return new Parsed(new LinkedHashMap<String, String>(), false); }
        static Parsed rejected() { return new Parsed(new LinkedHashMap<String, String>(), true); }
    }

    private interface Op { }
    private static final class Save implements Op { final Defaults captured; Save(Defaults captured) { this.captured = captured; } }
    private static final class Reset implements Op { }
}
