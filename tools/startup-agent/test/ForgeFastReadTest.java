import java.io.IOException;
import java.io.InputStream;
import java.lang.module.ModuleDescriptor;
import java.lang.module.ModuleReader;
import java.lang.module.ModuleReference;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.net.URI;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Optional;
import java.util.stream.Stream;

/** Observable read/open/close semantics of the hash-gated Forge 2.1.10 direct-read path. */
public final class ForgeFastReadTest {
    private static int checks;
    private static Method read;
    private static void check(boolean condition, String message) {
        checks++;
        if (!condition) throw new AssertionError(message);
    }
    private static int invokes;
    private static byte[] invoke(ModuleReader reader, ModuleReference ref, String name) throws Exception {
        invokes++;
        try { return (byte[]) read.invoke(null, reader, ref, name); }
        catch (InvocationTargetException error) {
            if (error.getCause() instanceof Exception cause) throw cause;
            if (error.getCause() instanceof Error cause) throw cause;
            throw error;
        }
    }
    private static final class Reader implements ModuleReader {
        byte[] content = {1, 2, 3};
        int opens, closes;
        String resource;
        IOException openError, readError, closeError;
        boolean invalidNullBytes;
        @Override public Optional<InputStream> open(String name) throws IOException {
            opens++; resource = name;
            if (openError != null) throw openError;
            if (content == null) return Optional.empty();
            return Optional.of(new InputStream() {
                @Override public int read() throws IOException { throw new AssertionError("readAllBytes should be used"); }
                @Override public byte[] readAllBytes() throws IOException {
                    if (readError != null) throw readError;
                    return invalidNullBytes ? null : content.clone();
                }
                @Override public void close() throws IOException {
                    closes++;
                    if (closeError != null) throw closeError;
                }
            });
        }
        @Override public Optional<URI> find(String name) { return Optional.empty(); }
        @Override public Stream<String> list() { throw new AssertionError("No resource enumeration"); }
        @Override public void close() { }
    }
    public static void main(String[] args) throws Exception {
        if (Runtime.version().feature() == 17
                && (Boolean.getBoolean("pclmoon.startup.forgeCache.disabled")
                    || Boolean.getBoolean("pclmoon.startup.forgeFastRead.disabled"))) {
            try {
                Class.forName("pclmoon.startup.ForgeDirectRead", false, null);
                throw new AssertionError("Disabled Java 17 Forge path must not install the bootstrap helper");
            } catch (ClassNotFoundException expected) { }
            System.out.println("PASS: disabled Java 17 Forge path installs no helper");
            return;
        }
        Class<?> direct = Class.forName("pclmoon.startup.ForgeDirectRead", true, null);
        read = direct.getMethod("read", ModuleReader.class, ModuleReference.class, String.class);
        Reader reader = new Reader();
        check(invoke(reader, null, "a.b.C")[0] == 1, "Successful byte read changed");
        check(reader.resource.equals("a/b/C.class"), "Binary name conversion changed");
        invoke(reader, null, "a.b.C");
        check(reader.opens == 2 && reader.closes == 2, "Every call reads and closes again; nothing cached");
        reader.content = null;
        check(invoke(reader, null, "absent").length == 0, "Missing class must return empty byte array");
        check(reader.closes == 2, "Missing stream must not be closed");
        reader.content = new byte[0];
        check(invoke(reader, null, "empty").length == 0 && reader.closes == 3, "Empty stream close changed");
        IOException open = new IOException("open failure"); reader.openError = open;
        try { invoke(reader, null, "fail"); throw new AssertionError("Open failure swallowed"); }
        catch (IOException expected) { check(expected == open, "Open exception identity changed"); }
        reader.openError = null;
        IOException body = new IOException("read failure"), close = new IOException("close failure");
        reader.readError = body; reader.closeError = close;
        try { invoke(reader, null, "fail"); throw new AssertionError("Read failure swallowed"); }
        catch (IOException expected) {
            check(expected == body, "Primary read exception changed");
            check(expected.getSuppressed().length == 1 && expected.getSuppressed()[0] == close,
                  "Close failure must be suppressed on original read failure");
        }
        reader.readError = null;
        Path file = Path.of(args[0]).resolve("source.jar"); Files.write(file, new byte[] {1});
        ModuleReference ref = new ModuleReference(ModuleDescriptor.newModule("fixture").build(), file.toUri()) {
            @Override public ModuleReader open() { throw new AssertionError("Do not reopen a reference"); }
        };
        try { invoke(reader, ref, "closeFailure"); throw new AssertionError("Close failure swallowed"); }
        catch (IOException expected) { check(expected == close, "Successful read must still propagate close failure"); }
        reader.closeError = null; reader.invalidNullBytes = true;
        int before = reader.closes;
        try { invoke(reader, null, "invalid"); throw new AssertionError("Invalid null byte result accepted"); }
        catch (NullPointerException expected) { check(reader.closes == before + 1, "Invalid read result must still close stream"); }
        try { invoke(reader, null, null); throw new AssertionError("Null name accepted"); }
        catch (NullPointerException expected) { checks++; }
        try { invoke(null, null, "name"); throw new AssertionError("Null reader accepted"); }
        catch (NullPointerException expected) { checks++; }
        long expectedCount = Boolean.getBoolean("pclmoon.startup.metrics") ? invokes : 0;
        check((Long) direct.getMethod("directReadCount").invoke(null) == expectedCount, "Direct read counter drifted");
        check(direct.getMethod("summary").invoke(null).equals("Forge direct reads=" + expectedCount),
              "Direct read summary drifted");
        System.out.println("PASS: " + checks + " Forge direct-read byte/open/close/exception checks");
    }
}
