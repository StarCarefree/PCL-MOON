package pclmoon.startup;

import java.util.Enumeration;
import java.util.Map;
import java.util.IdentityHashMap;
import java.util.Collections;
import java.util.WeakHashMap;
import java.net.URL;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;
import java.util.concurrent.atomic.LongAdder;
import java.util.jar.JarEntry;
import java.util.jar.JarFile;

/** Bootstrap-only, conservative JAR membership index. False positives use the original lookup. */
public final class NegativeCache {
    private static final int MAX_JARS = 256;
    private static final int INDEX_AFTER_MISSES = 4;
    private static final int MAX_HASH_SLOTS = 524288;
    private static final Object LOCK = new Object();
    private static volatile Map<JarFile, IndexState> indexes = Collections.emptyMap();
    private static int reservedHashSlots;
    private static final LongAdder indexBuilds = new LongAdder();
    private static final LongAdder indexedEntries = new LongAdder();
    private static final Map<Object, Boolean> closing = new WeakHashMap<>();
    private static final LongAdder hits = new LongAdder();
    private static volatile boolean enabled;
    private static final boolean METRICS = Boolean.getBoolean("pclmoon.startup.metrics");
    private static final boolean CLASS_LOOKUPS = Runtime.version().feature() == 21
        && !Boolean.getBoolean("pclmoon.startup.classpathCache.disabled");
    private static final Object CLASS_LOCK = new Object();
    private static final AtomicLong generation = new AtomicLong();
    private static final LongAdder classHits = new LongAdder();
    private static volatile Map<Object, ClassLookup> classpaths = Collections.emptyMap();
    private static volatile boolean classpathEnabled;
    private static volatile boolean manifestExpanded;
    private static final ThreadLocal<Boolean> registering = new ThreadLocal<>();

    private NegativeCache() { }

    public static void start(long milliseconds) {
        enabled = true;
        Thread cleanup = new Thread(() -> {
            try { Thread.sleep(milliseconds); }
            catch (InterruptedException ignored) { Thread.currentThread().interrupt(); }
            stop();
        }, "PCL-MOON startup cache cleanup");
        cleanup.setDaemon(true);
        cleanup.start();
    }

    public static JarEntry getJarEntry(JarFile jar, String name) {
        if (!enabled || name == null || jar.getClass() != JarFile.class) return jar.getJarEntry(name);
        IndexState state = indexes.get(jar);
        Index index = state == null ? null : state.index;
        if (index != null && !index.mightContain(name)) {
            if (METRICS) hits.increment();
            return null;
        }
        JarEntry result = jar.getJarEntry(name);
        // 成功查找不触发枚举；少量缺失先交给 JDK，避免一次性查询承担建表成本。
        if (result == null) {
            if (state == null) state = registerIndex(jar);
            if (state != null && !state.building.get() && state.misses.incrementAndGet() >= INDEX_AFTER_MISSES)
                buildIndex(jar, state);
        }
        return result;
    }

    private static IndexState registerIndex(JarFile jar) {
        synchronized (LOCK) {
            if (!enabled || closing.containsKey(jar)) return null;
            IndexState state = indexes.get(jar);
            if (state != null) return state;
            if (indexes.size() >= MAX_JARS) return null;
            state = new IndexState();
            Map<JarFile, IndexState> next = new IdentityHashMap<>(indexes);
            next.put(jar, state);
            indexes = next;
            return state;
        }
    }

    private static Index buildIndex(JarFile jar, IndexState state) {
        if (state.index != null) return state.index;
        // 其他线程正在构建时直接走原查找，不等待，也不重复枚举。
        if (!state.building.compareAndSet(false, true)) return null;
        try {
            int entries = jar.size();
            if (entries < 0 || entries > MAX_HASH_SLOTS / 2) return null;
            int slots = entries * 2;
            synchronized (LOCK) {
                if (!enabled || indexes.get(jar) != state || slots > MAX_HASH_SLOTS - reservedHashSlots) return null;
                reservedHashSlots += slots;
                state.hashSlots = slots;
            }
            // 全局锁只保护登记和预算，不包围 JAR 枚举。
            Index built = new Index(jar, entries, slots);
            synchronized (LOCK) {
                if (!enabled || indexes.get(jar) != state) return null;
                state.index = built;
                if (METRICS) {
                    indexBuilds.increment();
                    indexedEntries.add(entries);
                }
                return built;
            }
        } catch (RuntimeException unavailable) {
            // 可选枚举失败时保留原始查找结果，不发布不完整索引。
            return null;
        } finally {
            synchronized (LOCK) {
                if (state.index == null && indexes.get(jar) == state) {
                    reservedHashSlots -= state.hashSlots;
                    state.hashSlots = 0;
                }
            }
        }
    }

    private static final class IndexState {
        final AtomicInteger misses = new AtomicInteger();
        final AtomicBoolean building = new AtomicBoolean();
        volatile Index index;
        int hashSlots;
    }

    public static void forget(Object jar) {
        if (!enabled || jar == null || jar.getClass() != JarFile.class) return;
        synchronized (LOCK) {
            if (!enabled) return; // stop() may have cleared the maps while close waited for LOCK.
            closing.put(jar, Boolean.TRUE);
            Map<JarFile, IndexState> next = new IdentityHashMap<>(indexes);
            IndexState removed = next.remove(jar);
            if (removed != null) reservedHashSlots -= removed.hashSlots;
            indexes = next;
        }
    }

    public static void stop() {
        enabled = false;
        classpathEnabled = false;
        invalidateClasspaths();
        synchronized (LOCK) { indexes = Collections.emptyMap(); closing.clear(); reservedHashSlots = 0; }
        System.err.println("[PCL-MOON Startup] cache released" + (METRICS
            ? "; JAR misses avoided=" + hits.sum() + "; classpath scans avoided=" + classHits.sum()
                + "; JAR indexes built=" + indexBuilds.sum() + "; entries enumerated=" + indexedEntries.sum()
                + "; " + ForgeDirectRead.summary() : ""));
    }

    public static void activateClasspaths() { classpathEnabled = CLASS_LOOKUPS && !manifestExpanded; }

    /** Only called after all mutation hooks have been verified. */
    @SuppressWarnings("removal")
    public static ClassLookup beginClassLookup(Object owner, String name) {
        if (!enabled || !classpathEnabled || name == null || name.length() > 256 || !name.endsWith(".class")
                || System.getSecurityManager() != null) return null;
        ClassLookup lookup = classpaths.get(owner);
        if (lookup != null) return lookup;
        if (registering.get() != null || classpaths.size() >= 16) return null;
        registering.set(Boolean.TRUE);
        try {
            // Match the JDK's owner -> metadata lock order. Never hold CLASS_LOCK while
            // inspecting an owner/JarFile; a JDK getLoader call can hold owner and invalidate.
            synchronized (owner) {
                long version = generation.get();
                URL[] urls = (URL[]) owner.getClass().getMethod("getURLs").invoke(owner);
                var handler = owner.getClass().getDeclaredField("jarHandler");
                handler.setAccessible(true);
                boolean eligible = urls.length > 0 && handler.get(owner) == null;
                for (URL url : urls) {
                    if (!"file".equals(url.getProtocol()) || !url.getHost().isEmpty() || url.getQuery() != null
                            || url.getRef() != null || !url.getPath().endsWith(".jar")
                            || !Files.isRegularFile(Path.of(url.toURI()))) { eligible = false; break; }
                }
                // Already-open loaders may have expanded a manifest BEFORE this agent's hook
                // was installed. Check their actual JarFiles, not just getURLs()'s initial paths.
                var loaders = owner.getClass().getDeclaredField("loaders");
                loaders.setAccessible(true);
                for (Object loader : (java.util.List<?>) loaders.get(owner)) {
                    if (!eligible) break;
                    if (!loader.getClass().getName().equals("jdk.internal.loader.URLClassPath$JarLoader")) {
                        eligible = false; break;
                    }
                    var getJar = loader.getClass().getDeclaredMethod("getJarFile");
                    getJar.setAccessible(true);
                    JarFile jar = (JarFile) getJar.invoke(loader);
                    var manifest = jar.getManifest();
                    if (manifest != null && manifest.getMainAttributes().getValue("Class-Path") != null)
                        eligible = false;
                }
                synchronized (CLASS_LOCK) {
                    if (!enabled || !classpathEnabled || version != generation.get() || classpaths.size() >= 16) return null;
                    lookup = classpaths.get(owner);
                    if (lookup != null) return lookup;
                    lookup = new ClassLookup(owner, version, eligible);
                    Map<Object, ClassLookup> next = new IdentityHashMap<>(classpaths);
                    next.put(owner, lookup);
                    classpaths = next;
                    return lookup;
                }
            }
        } catch (ReflectiveOperationException | RuntimeException | java.io.IOException | java.net.URISyntaxException unavailable) {
            classpathEnabled = false;
            invalidateClasspaths();
            return null;
        } finally { registering.remove(); }
    }

    public static boolean isClassMissing(ClassLookup lookup, String name) {
        if (lookup == null || !enabled || !classpathEnabled || !lookup.eligible
                || lookup.version != generation.get()) return false;
        Index aggregate = lookup.aggregate;
        boolean missing = aggregate != null && !aggregate.mightContain(name) || lookup.misses.contains(name);
        if (missing && METRICS) classHits.increment();
        return missing;
    }

    public static void observeClassLookup(Object result, ClassLookup lookup, String name) {
        if (result != null || lookup == null || !lookup.eligible || !enabled || !classpathEnabled
                || lookup.version != generation.get()) return;
        if (lookup.observations.incrementAndGet() >= INDEX_AFTER_MISSES
                && lookup.building.compareAndSet(false, true)) buildClassIndex(lookup);
        if (lookup.size.incrementAndGet() > 4096) { lookup.size.decrementAndGet(); return; }
        if (!lookup.misses.add(name)) lookup.size.decrementAndGet();
    }

    private static void buildClassIndex(ClassLookup lookup) {
        try {
            synchronized (lookup.owner) {
                if (!enabled || !classpathEnabled || lookup.version != generation.get()) return;
                URL[] urls = (URL[]) lookup.owner.getClass().getMethod("getURLs").invoke(lookup.owner);
                var field = lookup.owner.getClass().getDeclaredField("loaders");
                field.setAccessible(true);
                List<?> loaders = (List<?>) field.get(lookup.owner);
                // A null original lookup normally opens the full path. Missing/corrupt/skipped
                // JARs or duplicate URLs are deliberately not summarized into an aggregate.
                if (loaders.size() != urls.length || loaders.size() > MAX_JARS) return;
                List<JarFile> jars = new ArrayList<>();
                long count = 0;
                for (Object loader : loaders) {
                    if (!loader.getClass().getName().equals("jdk.internal.loader.URLClassPath$JarLoader")) return;
                    var getJar = loader.getClass().getDeclaredMethod("getJarFile");
                    getJar.setAccessible(true);
                    JarFile jar = (JarFile) getJar.invoke(loader);
                    if (jar.getClass() != JarFile.class) return;
                    var manifest = jar.getManifest();
                    if (manifest != null && manifest.getMainAttributes().getValue("Class-Path") != null) return;
                    count += jar.size();
                    if (count > 262144) return;
                    jars.add(jar);
                }
                Index aggregate = new Index(count);
                for (JarFile jar : jars) {
                    if (!enabled || !classpathEnabled || lookup.version != generation.get()) return;
                    IndexState state = indexes.get(jar);
                    if (state == null) state = registerIndex(jar);
                    Index index = state == null ? null : buildIndex(jar, state);
                    // 并发构建或预算不足时保留逐名称缺失缓存，不为聚合重复枚举。
                    if (index == null) return;
                    for (int i = 0; i < index.hashCount; i++) aggregate.add(index.hashes[i]);
                }
                if (enabled && classpathEnabled && lookup.version == generation.get()) lookup.aggregate = aggregate;
            }
        } catch (ReflectiveOperationException | java.io.IOException | RuntimeException unavailable) {
            // A failed optional summary must never substitute for an original class lookup.
        }
    }

    /** Any addURL/close invalidates even in-flight observations, across all classpaths. */
    public static void invalidateClasspaths() {
        synchronized (CLASS_LOCK) {
            generation.incrementAndGet();
            classpaths = Collections.emptyMap();
        }
    }

    public static void manifestClassPath(URL[] urls) {
        if (urls != null && urls.length > 0) {
            manifestExpanded = true;
            classpathEnabled = false; // A manifest can introduce a live directory not visible in getURLs().
            invalidateClasspaths();
        }
    }

    public static long classMissHits() { return classHits.sum(); }

    public static final class ClassLookup {
        final Object owner;
        final AtomicBoolean building = new AtomicBoolean();
        final AtomicInteger observations = new AtomicInteger();
        volatile Index aggregate;
        final long version;
        final boolean eligible;
        final Set<String> misses = ConcurrentHashMap.newKeySet();
        final AtomicInteger size = new AtomicInteger();
        ClassLookup(Object owner, long version, boolean eligible) {
            this.owner = owner; this.version = version; this.eligible = eligible;
        }
    }

    private static final class Index {
        // 位图每份最多 256 KiB；单 JAR 哈希数组共享 2 MiB 总预算，供聚合索引复用。
        final long[] bits;
        final int mask;
        int[] hashes;
        int hashCount;
        Index(long count) {
            int bitCount = 1024;
            while (bitCount < Math.min(2097152L, Math.max(1024L, count * 16L))) bitCount <<= 1;
            bits = new long[bitCount >>> 6];
            mask = bitCount - 1;
        }
        Index(JarFile jar, int count, int slots) {
            this(count);
            hashes = new int[slots];
            Enumeration<JarEntry> entries = jar.entries();
            while (entries.hasMoreElements()) {
                String name = entries.nextElement().getName();
                remember(name.hashCode());
                // 保守纳入所有 MR 别名，实际版本选择和签名检查仍交给 JarFile。
                String prefix = "META-INF/versions/";
                if (name.startsWith(prefix)) {
                    int slash = name.indexOf('/', prefix.length());
                    if (slash > 0 && slash + 1 < name.length()) remember(name.substring(slash + 1).hashCode());
                }
            }
        }
        private void remember(int hash) {
            hashes[hashCount++] = hash;
            add(hash);
        }
        void add(int hash) {
            int bit = (hash ^ (hash >>> 16)) & mask;
            bits[bit >>> 6] |= 1L << bit;
        }
        boolean test(int hash) {
            int bit = (hash ^ (hash >>> 16)) & mask;
            return (bits[bit >>> 6] & (1L << bit)) != 0;
        }
        boolean mightContain(String name) {
            int hash = name.hashCode();
            return test(hash) || (!name.endsWith("/") && test(hash * 31 + '/'));
        }
    }
}
