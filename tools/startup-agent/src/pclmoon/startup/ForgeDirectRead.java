package pclmoon.startup;

import java.lang.module.ModuleReader;
import java.lang.module.ModuleReference;
import java.util.Objects;
import java.util.concurrent.atomic.LongAdder;

/** Hash-gated direct read of raw class bytes: no caching, no cross-launch state. */
public final class ForgeDirectRead {
    private static final LongAdder directReads = Boolean.getBoolean("pclmoon.startup.metrics") ? new LongAdder() : null;

    private ForgeDirectRead() {}

    /** Equivalent to securejarhandler 2.1.10's Optional/Stream read pipeline, without its
     * stream/lambda wrappers. The caller still runs the original transformation and signers. */
    public static byte[] read(ModuleReader reader, ModuleReference ref, String name)
            throws java.io.IOException {
        if (directReads != null) directReads.increment();
        java.io.InputStream input = reader.open(name.replace('.', '/') + ".class").orElse(null);
        if (input == null) return new byte[0];
        try (input) {
            return Objects.requireNonNull(input.readAllBytes());
        }
    }

    public static long directReadCount() { return directReads == null ? 0 : directReads.sum(); }
    public static String summary() { return "Forge direct reads=" + directReadCount(); }
}
