package pclmoon.framegen;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.lang.instrument.Instrumentation;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.security.MessageDigest;
import java.util.Arrays;
import java.util.jar.JarFile;

/** Optional agent: failure must never abort Minecraft's premain. */
public final class FrameGenAgent {
    public static void premain(String options, Instrumentation instrumentation) {
        if (Boolean.getBoolean("pclmoon.framegen.disabled")) return;
        try {
            for (Class<?> loaded : instrumentation.getAllLoadedClasses()) {
                if (loaded.getName().equals("org.lwjgl.glfw.GLFW")) {
                    System.err.println("[PCL FrameGen] GLFW already loaded; original presentation retained");
                    return;
                }
            }
            Path own = java.nio.file.Paths.get(FrameGenAgent.class.getProtectionDomain().getCodeSource().getLocation().toURI());
            byte[] helper;
            try (InputStream in = FrameGenAgent.class.getResourceAsStream("/bootstrap-helper.jar")) {
                if (in == null) throw new IllegalStateException("missing bootstrap helper");
                ByteArrayOutputStream out = new ByteArrayOutputStream();
                byte[] buffer = new byte[8192];
                for (int n; (n = in.read(buffer)) != -1;) out.write(buffer, 0, n);
                helper = out.toByteArray();
            }
            StringBuilder hash = new StringBuilder();
            for (byte b : MessageDigest.getInstance("SHA-256").digest(helper)) hash.append(String.format("%02x", b & 255));
            Path target = own.resolveSibling("pcl-framegen-helper-" + hash + ".jar");
            if (!Files.isRegularFile(target) || !Arrays.equals(Files.readAllBytes(target), helper)) {
                Path pending = Files.createTempFile(own.getParent(), ".framegen-", ".tmp");
                try {
                    Files.write(pending, helper);
                    try { Files.move(pending, target, StandardCopyOption.REPLACE_EXISTING); }
                    catch (java.io.IOException race) {
                        if (!Files.isRegularFile(target) || !Arrays.equals(Files.readAllBytes(target), helper)) throw race;
                    }
                } finally { Files.deleteIfExists(pending); }
            }
            instrumentation.appendToBootstrapClassLoaderSearch(new JarFile(target.toFile()));
            Class<?> hook = Class.forName("pclmoon.framegen.bridge.SwapHook", true, null);
            hook.getMethod("configure", String.class).invoke(null, own.toUri().toString());
            instrumentation.addTransformer(new GlfwTransformer());
            System.err.println("[PCL FrameGen] optional GLFW presentation hook installed; OpenGL support is checked on first frame");
        } catch (Throwable unavailable) {
            System.err.println("[PCL FrameGen] agent unavailable (" + unavailable.getClass().getSimpleName() + "); original presentation retained");
        }
    }
}
