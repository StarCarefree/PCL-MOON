import java.net.URL;
import java.net.URLClassLoader;
import java.nio.file.Path;
import java.util.Locale;

public final class LookupBenchmark {
    private static final int QUERIES = 4000;

    public static void main(String[] args) throws Exception {
        if (args.length != 2) throw new IllegalArgumentException("Expected fixture directory and scenario");
        if (Runtime.version().feature() != 21) throw new IllegalStateException("Java 21 is required");
        Path directory = Path.of(args[0]);
        String scenario = args[1];
        if (!scenario.equals("positive") && !scenario.equals("misses") && !scenario.equals("mixed")) {
            throw new IllegalArgumentException("Unknown scenario: " + scenario);
        }
        URL[] urls = new URL[32];
        for (int i = 0; i < urls.length; i++) {
            urls[i] = directory.resolve(String.format(Locale.ROOT, "fixture-%02d.jar", i)).toUri().toURL();
        }
        // 在计时前生成查询，避免格式化与字符串分配掩盖资源查找耗时。
        String[] names = new String[QUERIES];
        boolean[] expected = new boolean[QUERIES];
        int expectedHits = 0;
        for (int i = 0; i < QUERIES; i++) {
            boolean hit = scenario.equals("positive") || (scenario.equals("mixed") && i % 2 == 0);
            expected[i] = hit;
            if (hit) {
                int hitIndex = scenario.equals("positive") ? i : i / 2;
                int jar = scenario.equals("positive") ? 0 : hitIndex % 32;
                int resource = scenario.equals("positive") ? hitIndex % 1000 : (hitIndex / 32) % 1000;
                names[i] = "fixture/j" + jar + "/C" + resource + ".class";
                expectedHits++;
            } else {
                names[i] = "missing/p" + i + "/Absent" + i + ".class";
            }
        }
        int hits = 0;
        int misses = 0;
        long firstNanos = 0;
        long totalNanos;
        // 不读取资源字节、不调用 loadClass，也不让父加载器参与查找。
        try (URLClassLoader loader = new URLClassLoader(urls, null)) {
            long start = System.nanoTime();
            for (int i = 0; i < QUERIES; i++) {
                URL found = loader.getResource(names[i]);
                if (i == 0) firstNanos = System.nanoTime() - start;
                boolean hit = found != null;
                if (hit != expected[i]) throw new AssertionError("Unexpected lookup result: " + names[i]);
                if (hit) hits++;
                else misses++;
            }
            totalNanos = System.nanoTime() - start;
        }
        if (hits != expectedHits || misses != QUERIES - expectedHits) {
            throw new AssertionError("Unexpected hit/miss counts");
        }
        System.out.printf(Locale.ROOT,
            "BENCHMARK {\"scenario\":\"%s\",\"queries\":%d,\"hits\":%d,\"misses\":%d,"
                + "\"first_query_ms\":%.6f,\"query_loop_ms\":%.6f}%n",
            scenario, QUERIES, hits, misses, firstNanos / 1_000_000.0, totalNanos / 1_000_000.0);
    }
}
