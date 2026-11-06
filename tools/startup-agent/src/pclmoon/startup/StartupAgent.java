package pclmoon.startup;

import java.io.InputStream;
import java.lang.instrument.Instrumentation;
import java.net.URI;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.security.MessageDigest;
import java.util.Arrays;
import java.util.Collections;
import java.util.HexFormat;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.jar.JarFile;

public final class StartupAgent {
    public static void premain(String options, Instrumentation instrumentation) {
        if (Boolean.getBoolean("pclmoon.startup.disabled")) return;
        Class<?> cache = null;
        try {
            if ((Runtime.version().feature() != 17 && Runtime.version().feature() != 21)
                    || !instrumentation.isRetransformClassesSupported()) {
                System.err.println("[PCL-MOON Startup] unsupported JVM; using original lookup");
                return;
            }
            boolean forgeOnly = Runtime.version().feature() == 17;
            if (forgeOnly && (Boolean.getBoolean("pclmoon.startup.forgeCache.disabled")
                    || Boolean.getBoolean("pclmoon.startup.forgeFastRead.disabled"))) return;
            Path helper = extractHelper();
            instrumentation.appendToBootstrapClassLoaderSearch(new JarFile(helper.toFile()));
            // Java 17 仅使用 Forge 直接读取，不初始化缓存或开放 JDK 加载器内部包。
            if (!forgeOnly) cache = Class.forName("pclmoon.startup.NegativeCache", true, null);
            Module helperModule = forgeOnly
                ? Class.forName("pclmoon.startup.ForgeDirectRead", false, null).getModule() : cache.getModule();
            Module javaBase = Object.class.getModule();
            Map<String, Set<Module>> exports = new java.util.HashMap<>(Map.of(
                "jdk.internal.org.objectweb.asm", Set.of(StartupAgent.class.getModule()),
                "jdk.internal.org.objectweb.asm.tree", Set.of(StartupAgent.class.getModule())));
            if (!forgeOnly) exports.put("jdk.internal.loader", Set.of(helperModule));
            instrumentation.redefineModule(javaBase, Set.of(helperModule), exports,
                forgeOnly ? Collections.emptyMap() : Map.of("jdk.internal.loader", Set.of(helperModule)),
                Collections.emptySet(), Collections.emptyMap());
            long duration = Math.max(1000, Math.min(120000, Long.getLong("pclmoon.startup.durationMs", 60000L)));
            installForgeDirectRead(instrumentation, helperModule, duration);
            if (forgeOnly) {
                // Forge 主要读取命名模块，跳过无关的应用 JAR 索引和 JDK 类修改。
                System.err.println("[PCL-MOON Startup] Java 17 Forge-only path; JDK lookup caches not installed");
                return;
            }
            cache.getMethod("start", long.class).invoke(null, duration);
            LookupTransformer transformer = new LookupTransformer();
            // Java 17 still has JarIndex-driven implicit classpaths. Keep that whole-path
            // optimization off until its distinct semantics have their own coverage.
            ClassPathTransformer classpaths = Runtime.version().feature() != 21
                || Boolean.getBoolean("pclmoon.startup.classpathCache.disabled") ? null : new ClassPathTransformer();
            boolean installed = false;
            instrumentation.addTransformer(transformer, true);
            try {
                if (classpaths != null) instrumentation.addTransformer(classpaths, true);
                instrumentation.retransformClasses(Class.forName("jdk.internal.loader.URLClassPath$JarLoader"),
                    java.util.zip.ZipFile.class, Class.forName("jdk.internal.loader.URLClassPath"));
                if (transformer.lookups != 1 || transformer.closeHooks != 1) {
                    cache.getMethod("stop").invoke(null);
                    System.err.println("[PCL-MOON Startup] unknown JDK layout; cache disabled");
                    return;
                }
                if (classpaths != null) {
                    if (classpaths.searches == 2 && classpaths.mutations == 2 && classpaths.manifests == 1) {
                        cache.getMethod("activateClasspaths").invoke(null);
                        System.err.println("[PCL-MOON Startup] versioned classpath miss cache enabled");
                    } else System.err.println("[PCL-MOON Startup] unknown classpath layout; classpath cache disabled");
                }
                installed = true;
            } finally {
                // Keep hooks in subsequent normal retransforms requested by other agents.
                if (!installed) {
                    instrumentation.removeTransformer(transformer);
                    if (classpaths != null) instrumentation.removeTransformer(classpaths);
                }
            }
            System.err.println("[PCL-MOON Startup] bounded JAR miss cache enabled for " + duration + " ms");
        } catch (Throwable error) {
            try { if (cache != null) cache.getMethod("stop").invoke(null); }
            catch (Throwable ignored) { }
            // An optional optimization must never abort premain or expose command-line credentials.
            System.err.println("[PCL-MOON Startup] unavailable (" + error.getClass().getSimpleName() + "); using original lookup");
        }
    }

    /** Content-named helper beside the agent JAR: stable across launches, no per-run %TEMP% litter. */
    private static Path extractHelper() throws Exception {
        byte[] bytes;
        try (InputStream input = StartupAgent.class.getResourceAsStream("/bootstrap-helper.jar")) {
            if (input == null) throw new IllegalStateException("Missing bootstrap helper");
            bytes = input.readAllBytes();
        }
        URI source = StartupAgent.class.getProtectionDomain().getCodeSource().getLocation().toURI();
        if (!"file".equalsIgnoreCase(source.getScheme())) throw new IllegalStateException("Agent JAR is not a file");
        Path directory = Path.of(source).getParent();
        if (directory == null) throw new IllegalStateException("Agent JAR has no parent directory");
        String tag = HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(bytes)).substring(0, 16);
        Path target = directory.resolve("pcl-moon-startup-helper-" + tag + ".jar");
        if (Files.isRegularFile(target) && Arrays.equals(Files.readAllBytes(target), bytes)) return target;
        Path pending = target.resolveSibling(target.getFileName() + "." + UUID.randomUUID().toString().replace("-", "") + ".tmp");
        Files.write(pending, bytes);
        try {
            Files.move(pending, target, StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING);
        } catch (java.io.IOException moveFailure) {
            // Another JVM may hold the target open; a matching existing file is just as good.
            Files.deleteIfExists(pending);
            if (Files.isRegularFile(target) && Arrays.equals(Files.readAllBytes(target), bytes)) return target;
            throw moveFailure;
        }
        return target;
    }

    private static void installForgeDirectRead(Instrumentation instrumentation, Module helperModule, long duration) {
        if (Boolean.getBoolean("pclmoon.startup.forgeCache.disabled")
                || Boolean.getBoolean("pclmoon.startup.forgeFastRead.disabled")) return;
        if (Runtime.version().feature() == 17 && Boolean.getBoolean("pclmoon.startup.metrics")) {
            // metrics 开启时，窗口结束打印一次直接读取计数（Java 21 由 NegativeCache.stop 打印）。
            Thread reporter = new Thread(() -> {
                try { Thread.sleep(duration); } catch (InterruptedException ignored) { Thread.currentThread().interrupt(); }
                try {
                    System.err.println("[pcl-moon-startup] "
                        + Class.forName("pclmoon.startup.ForgeDirectRead", false, null).getMethod("summary").invoke(null));
                } catch (Throwable ignored) { }
            }, "pcl-moon-forge-direct-read-metrics");
            reporter.setDaemon(true);
            reporter.start();
        }
        ForgeReadTransformer transformer = null;
        try {
            Class.forName("pclmoon.startup.ForgeDirectRead", true, null);
            transformer = new ForgeReadTransformer(instrumentation, helperModule);
            instrumentation.addTransformer(transformer, true);
            // Do not eagerly resolve Forge classes: its module layers have not necessarily
            // been built yet. Future definitions are transformed by the registered callback.
            for (Class<?> loaded : instrumentation.getAllLoadedClasses()) {
                if (loaded.getName().equals("cpw.mods.cl.ModuleClassLoader")
                        && instrumentation.isModifiableClass(loaded)) instrumentation.retransformClasses(loaded);
            }
        } catch (Throwable unavailable) {
            if (transformer != null) instrumentation.removeTransformer(transformer);
            System.err.println("[PCL-MOON Startup] Forge direct read unavailable; original reads retained");
        }
    }
}
