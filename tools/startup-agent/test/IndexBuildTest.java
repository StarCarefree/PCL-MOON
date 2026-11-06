import java.lang.reflect.Field;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.net.URL;
import java.net.URLClassLoader;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.LongAdder;
import java.util.jar.JarEntry;
import java.util.jar.JarFile;
import java.util.jar.JarOutputStream;

public final class IndexBuildTest {
    private static Class<?> cache;
    private static Method lookup;
    private static int checks;
    private static void check(boolean condition, String message) {
        checks++;
        if (!condition) throw new AssertionError(message);
    }
    private static Field field(Class<?> type, String name) throws Exception {
        Field field = type.getDeclaredField(name);
        field.setAccessible(true);
        return field;
    }
    private static Object value(String name) throws Exception { return field(cache, name).get(null); }
    private static long counter(String name) throws Exception { return ((LongAdder) value(name)).sum(); }
    private static Map<?, ?> indexes() throws Exception { return (Map<?, ?>) value("indexes"); }
    private static Object index(JarFile jar) throws Exception {
        Object state = indexes().get(jar);
        return state == null ? null : field(state.getClass(), "index").get(state);
    }
    private static Object get(JarFile jar, String name) throws Exception { return lookup.invoke(null, jar, name); }
    private static void miss(JarFile jar, int count) throws Exception {
        for (int i = 0; i < count; i++) check(get(jar, "absent/" + i + ".class") == null, "Missing entry appeared");
    }
    public static void main(String[] args) throws Exception {
        if (Runtime.version().feature() != 21) {
            System.out.println("PASS: JDK index checks apply only to Java 21");
            return;
        }
        cache = Class.forName("pclmoon.startup.NegativeCache", true, null);
        lookup = cache.getMethod("getJarEntry", JarFile.class, String.class);
        Path path = Path.of(args[0]).resolve("entries.jar");
        try (JarOutputStream out = new JarOutputStream(Files.newOutputStream(path))) {
            for (int i = 0; i < 64; i++) {
                out.putNextEntry(new JarEntry("present/Type" + i + ".class"));
                out.write(i);
                out.closeEntry();
            }
            out.putNextEntry(new JarEntry("META-INF/versions/21/versioned/Type.class"));
            out.write(1);
            out.closeEntry();
        }
        long before = counter("indexBuilds");
        int budgetBefore = (Integer) value("reservedHashSlots");
        JarFile jar = new JarFile(path.toFile());
        for (int i = 0; i < 64; i++) check(get(jar, "present/Type" + i + ".class") != null, "Hit lost");
        check(!indexes().containsKey(jar), "Successful queries must not register an index");
        miss(jar, 3);
        check(index(jar) == null, "Sparse misses must not enumerate the JAR");
        miss(jar, 1);
        check(index(jar) != null && counter("indexBuilds") == before + 1, "Fourth miss must build once");
        check((Integer) value("reservedHashSlots") == budgetBefore + jar.size() * 2, "Hash budget mismatch");
        jar.close();
        check(!indexes().containsKey(jar), "Closed JAR retained");
        check((Integer) value("reservedHashSlots") == budgetBefore, "Close did not release hash budget");
        try { get(jar, "absent.class"); throw new AssertionError("Closed JAR returned cached miss"); }
        catch (InvocationTargetException expected) {
            check(expected.getCause() instanceof IllegalStateException, "Closed exception changed");
        }

        // 逐 JAR 和聚合索引必须共享一次枚举，且保留 MR 别名。
        before = counter("indexBuilds");
        long entriesBefore = counter("indexedEntries");
        try (URLClassLoader loader = new URLClassLoader(new URL[] {path.toUri().toURL()}, null)) {
            for (int i = 0; i < 8; i++) check(loader.getResource("missing/Type" + i + ".class") == null, "Missing class appeared");
            check(counter("indexBuilds") == before + 1, "Aggregate rebuilt the JAR index");
            check(counter("indexedEntries") == entriesBefore + 65, "Entries enumerated more than once");
            long hits = (Long) cache.getMethod("classMissHits").invoke(null);
            check(loader.getResource("brand/new/Absent.class") == null, "Aggregate miss changed");
            check((Long) cache.getMethod("classMissHits").invoke(null) == hits + 1, "Aggregate did not skip new miss");
            check(loader.getResource("present/Type7.class") != null, "Aggregate hid a real resource");
        }

        // 模拟预算耗尽；原查找仍必须能找到资源，不能发布部分索引。
        Field budget = field(cache, "reservedHashSlots");
        int savedBudget = budget.getInt(null);
        budget.setInt(null, field(cache, "MAX_HASH_SLOTS").getInt(null));
        try (JarFile limited = new JarFile(path.toFile())) {
            miss(limited, 8);
            check(index(limited) == null, "Budget overflow built an index");
            check(get(limited, "present/Type1.class") != null, "Budget fallback lost a resource");
        } finally { budget.setInt(null, savedBudget); }

        before = counter("indexBuilds");
        try (JarFile shared = new JarFile(path.toFile())) {
            var workers = Executors.newFixedThreadPool(8);
            CountDownLatch start = new CountDownLatch(1);
            try {
                var tasks = new java.util.ArrayList<java.util.concurrent.Future<Boolean>>();
                for (int i = 0; i < 8; i++) tasks.add(workers.submit(() -> {
                    start.await();
                    for (int j = 0; j < 100; j++)
                        if (get(shared, "missing/" + j + ".class") != null || get(shared, "present/Type1.class") == null)
                            return false;
                    return true;
                }));
                start.countDown();
                for (var task : tasks) check(task.get(5, TimeUnit.SECONDS), "Concurrent lookup changed");
            } finally { workers.shutdownNow(); }
            check(counter("indexBuilds") == before + 1, "Concurrent queries enumerated the same JAR twice");
        }

        // 持有 JarFile 锁，让构建在开始枚举前等待；全局缓存锁必须仍可用。
        try (JarFile pending = new JarFile(path.toFile())) {
            miss(pending, 3);
            Object state = indexes().get(pending);
            var worker = Executors.newSingleThreadExecutor();
            try {
                java.util.concurrent.Future<?> task;
                synchronized (pending) {
                    task = worker.submit(() -> {
                        try {
                            Method build = cache.getDeclaredMethod("buildIndex", JarFile.class, state.getClass());
                            build.setAccessible(true);
                            build.invoke(null, pending, state);
                        } catch (Exception error) { throw new RuntimeException(error); }
                    });
                    AtomicBoolean building = (AtomicBoolean) field(state.getClass(), "building").get(state);
                    long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(3);
                    while (!building.get() && System.nanoTime() < deadline) Thread.onSpinWait();
                    check(building.get(), "Build did not start");
                    var stopper = Executors.newSingleThreadExecutor();
                    try { stopper.submit(() -> {
                        try { cache.getMethod("stop").invoke(null); }
                        catch (Exception error) { throw new RuntimeException(error); }
                    }).get(3, TimeUnit.SECONDS); }
                    finally { stopper.shutdownNow(); }
                }
                task.get(3, TimeUnit.SECONDS);
                check(indexes().isEmpty(), "In-flight build republished after stop");
                check((Integer) value("reservedHashSlots") == 0, "Stop leaked hash budget");
                check(get(pending, "present/Type0.class") != null, "Stop fallback lost a resource");
            } finally { worker.shutdownNow(); }
        }
        System.out.println("PASS: " + checks + " lazy-index, reuse, budget, concurrency, close and stop checks");
    }
}
