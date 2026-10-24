package pclmoon.framegen.render;

import org.lwjgl.opengl.GL;
import org.lwjgl.opengl.GL11;
import org.lwjgl.opengl.GL12;
import org.lwjgl.opengl.GL13;
import org.lwjgl.opengl.GL14;
import org.lwjgl.opengl.GL15;
import org.lwjgl.opengl.GL20;
import org.lwjgl.opengl.GL21;
import org.lwjgl.opengl.GL30;
import org.lwjgl.opengl.GL33;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.util.HashMap;
import java.util.Map;

/**
 * AMD FidelityFX FSR 1.0.2 spatial upscaler: official EASU then optional RCAS.
 * Requires an OpenGL 3.3 core context. The rest of the agent may stay on 3.2.
 * No CPU readback and no GPU sync in the production path.
 */
public final class GlSuperResolutionRenderer {
    private static final String ROOT = "/pclmoon/framegen/render/fsr1/";
    private static final String[] REQUIRED = {
        "ffx_a.h", "ffx_fsr1.h", "easu.frag", "rcas.frag", "fullscreen.vert", "linear.frag", "LICENSE.txt"
    };
    /** Used only when the caller passes NaN. 0 bypasses RCAS; modest default is about one stop. */
    private static final float DEFAULT_SHARPNESS = 0.2f;
    private static final int DEPTH_INTERNAL = GL14.GL_DEPTH_COMPONENT24;

    private boolean prepared;
    private boolean supported;
    private int outputWidth;
    private int outputHeight;
    private int scratch;
    private int sampler;
    private int scratchFbo;
    private int readFbo;
    private int drawFbo;
    private int vao;
    private int easuProgram;
    private int rcasProgram;
    private int linearProgram;
    private int easuInput;
    private int easuSize;
    private int easuCon0;
    private int easuCon1;
    private int easuCon2;
    private int easuCon3;
    private int rcasInput;
    private int rcasSize;
    private int rcasCon;
    private int linearInput;
    private int linearInv;

    public GlSuperResolutionRenderer() {
    }

    /**
     * Validates GL 3.3, official shader resources and a one-time output allocation.
     * Call on the render thread before the world scope. False means unsupported.
     */
    public boolean prepare(int outputWidth, int outputHeight) {
        if (outputWidth < 1 || outputHeight < 1) return fail();
        if (prepared && this.outputWidth == outputWidth && this.outputHeight == outputHeight) return supported;
        close();
        this.outputWidth = outputWidth;
        this.outputHeight = outputHeight;
        prepared = true;
        if (!gl33() || !resourcesPresent()) return fail();
        GlState state = GlState.capture();
        try {
            String vert = source("fullscreen.vert");
            easuProgram = program(vert, source("easu.frag"));
            rcasProgram = program(vert, source("rcas.frag"));
            linearProgram = program(vert, source("linear.frag"));
            easuInput = uniform(easuProgram, "uInput");
            easuSize = uniform(easuProgram, "uInputSize");
            easuCon0 = uniform(easuProgram, "uCon0");
            easuCon1 = uniform(easuProgram, "uCon1");
            easuCon2 = uniform(easuProgram, "uCon2");
            easuCon3 = uniform(easuProgram, "uCon3");
            rcasInput = uniform(rcasProgram, "uInput");
            rcasSize = uniform(rcasProgram, "uSize");
            rcasCon = uniform(rcasProgram, "uCon");
            linearInput = uniform(linearProgram, "uInput");
            linearInv = uniform(linearProgram, "uInvOutput");
            scratch = GL11.glGenTextures();
            GL11.glBindTexture(GL11.GL_TEXTURE_2D, scratch);
            GL11.glTexParameteri(GL11.GL_TEXTURE_2D, GL11.GL_TEXTURE_MIN_FILTER, GL11.GL_NEAREST);
            GL11.glTexParameteri(GL11.GL_TEXTURE_2D, GL11.GL_TEXTURE_MAG_FILTER, GL11.GL_NEAREST);
            GL11.glTexParameteri(GL11.GL_TEXTURE_2D, GL11.GL_TEXTURE_WRAP_S, GL12.GL_CLAMP_TO_EDGE);
            GL11.glTexParameteri(GL11.GL_TEXTURE_2D, GL11.GL_TEXTURE_WRAP_T, GL12.GL_CLAMP_TO_EDGE);
            GL11.glTexImage2D(GL11.GL_TEXTURE_2D, 0, GL11.GL_RGBA8, outputWidth, outputHeight, 0,
                GL11.GL_RGBA, GL11.GL_UNSIGNED_BYTE, 0L);
            sampler = GL33.glGenSamplers();
            GL33.glSamplerParameteri(sampler, GL11.GL_TEXTURE_MIN_FILTER, GL11.GL_NEAREST);
            GL33.glSamplerParameteri(sampler, GL11.GL_TEXTURE_MAG_FILTER, GL11.GL_NEAREST);
            GL33.glSamplerParameteri(sampler, GL11.GL_TEXTURE_WRAP_S, GL12.GL_CLAMP_TO_EDGE);
            GL33.glSamplerParameteri(sampler, GL11.GL_TEXTURE_WRAP_T, GL12.GL_CLAMP_TO_EDGE);
            scratchFbo = framebuffer(scratch);
            readFbo = GL30.glGenFramebuffers();
            drawFbo = GL30.glGenFramebuffers();
            vao = GL30.glGenVertexArrays();
            supported = drainErrors();
            return supported;
        } catch (RuntimeException unsupported) {
            return fail();
        } finally {
            state.restore();
        }
    }

    /**
     * Upscales {@code inputColor} into Minecraft's original color (and optional depth) textures.
     * {@code sharpness} is 0..1 strength; 0 bypasses RCAS. True only after EASU/RCAS succeeds.
     * False is returned only after a scaled GPU blit has copied the current image to the output.
     */
    public boolean upscale(int inputColor, int inputDepth, int outputColor, int outputDepth,
                           int srcW, int srcH, int dstW, int dstH, float sharpness) {
        if (!prepared || !supported || dstW != outputWidth || dstH != outputHeight
            || srcW < 1 || srcH < 1 || inputColor == 0 || outputColor == 0) {
            return fallback(inputColor, outputColor, srcW, srcH, dstW, dstH);
        }
        float strength = sharpness < 0f ? 0f : Math.min(sharpness, 1f);
        if (Float.isNaN(strength)) strength = DEFAULT_SHARPNESS;
        GlState state = GlState.capture();
        boolean ok = false;
        try {
            boolean depthRequested = inputDepth != 0 && outputDepth != 0;
            boolean depthOk = !depthRequested || transferDepth(inputDepth, outputDepth, srcW, srcH, dstW, dstH);
            bindSampler(GL11.GL_NEAREST);
            GL30.glBindVertexArray(vao);
            GL13.glActiveTexture(GL13.GL_TEXTURE0);
            GL11.glBindTexture(GL11.GL_TEXTURE_2D, inputColor);
            GL20.glUseProgram(easuProgram);
            GL20.glUniform1i(easuInput, 0);
            GL20.glUniform2f(easuSize, srcW, srcH);
            float[] con0 = new float[4], con1 = new float[4], con2 = new float[4], con3 = new float[4];
            easuConstants(con0, con1, con2, con3, srcW, srcH, dstW, dstH);
            GL20.glUniform4fv(easuCon0, con0);
            GL20.glUniform4fv(easuCon1, con1);
            GL20.glUniform4fv(easuCon2, con2);
            GL20.glUniform4fv(easuCon3, con3);
            int easuTarget = strength == 0f ? outputColor : scratch;
            int easuFbo = strength == 0f ? drawFbo : scratchFbo;
            if (!drawColor(easuFbo, easuTarget, dstW, dstH)) return false;
            if (strength > 0f) {
                GL11.glBindTexture(GL11.GL_TEXTURE_2D, scratch);
                GL20.glUseProgram(rcasProgram);
                GL20.glUniform1i(rcasInput, 0);
                GL20.glUniform2f(rcasSize, dstW, dstH);
                GL20.glUniform4f(rcasCon, strength, 0f, 0f, 0f);
                if (!drawColor(drawFbo, outputColor, dstW, dstH)) return false;
            }
            ok = drainErrors() && depthOk;
            return ok;
        } catch (RuntimeException failed) {
            return false;
        } finally {
            try { if (!ok) blitColor(inputColor, outputColor, srcW, srcH, dstW, dstH); }
            finally { state.restore(); }
        }
    }

    /** Render-thread / context owner. Idempotent. */
    public void close() {
        if (!prepared && scratch == 0 && easuProgram == 0) return;
        GlState state = gl33() ? GlState.capture() : null;
        try {
            deleteProgram(easuProgram);
            deleteProgram(rcasProgram);
            deleteProgram(linearProgram);
            if (vao != 0) GL30.glDeleteVertexArrays(vao);
            if (scratchFbo != 0) GL30.glDeleteFramebuffers(scratchFbo);
            if (readFbo != 0) GL30.glDeleteFramebuffers(readFbo);
            if (drawFbo != 0) GL30.glDeleteFramebuffers(drawFbo);
            if (scratch != 0) GL11.glDeleteTextures(scratch);
            if (sampler != 0) GL33.glDeleteSamplers(sampler);
            drainErrors();
        } finally {
            easuProgram = rcasProgram = linearProgram = 0;
            vao = scratchFbo = readFbo = drawFbo = scratch = sampler = 0;
            prepared = supported = false;
            outputWidth = outputHeight = 0;
            if (state != null) state.restore();
        }
    }

    private boolean fallback(int inputColor, int outputColor, int srcW, int srcH, int dstW, int dstH) {
        if (inputColor == 0 || outputColor == 0 || srcW < 1 || srcH < 1 || dstW < 1 || dstH < 1 || !gl33()) return false;
        GlState state = GlState.capture();
        try {
            blitColor(inputColor, outputColor, srcW, srcH, dstW, dstH);
            return false;
        } finally {
            state.restore();
        }
    }

    private void blitColor(int inputColor, int outputColor, int srcW, int srcH, int dstW, int dstH) {
        if (linearProgram == 0 || drawFbo == 0 || vao == 0 || sampler == 0) {
            scaledBlit(inputColor, outputColor, srcW, srcH, dstW, dstH, GL11.GL_COLOR_BUFFER_BIT, GL11.GL_LINEAR);
            return;
        }
        bindSampler(GL11.GL_LINEAR);
        GL30.glBindVertexArray(vao);
        GL13.glActiveTexture(GL13.GL_TEXTURE0);
        GL11.glBindTexture(GL11.GL_TEXTURE_2D, inputColor);
        GL20.glUseProgram(linearProgram);
        GL20.glUniform1i(linearInput, 0);
        GL20.glUniform2f(linearInv, 1f / dstW, 1f / dstH);
        drawColor(drawFbo, outputColor, dstW, dstH);
    }

    private void bindSampler(int filter) {
        GL33.glBindSampler(0, sampler);
        GL33.glSamplerParameteri(sampler, GL11.GL_TEXTURE_MIN_FILTER, filter);
        GL33.glSamplerParameteri(sampler, GL11.GL_TEXTURE_MAG_FILTER, filter);
    }

    private boolean transferDepth(int inputDepth, int outputDepth, int srcW, int srcH, int dstW, int dstH) {
        if (inputDepth == 0 || outputDepth == 0) return false;
        if (!depthCompatible(inputDepth) || !depthCompatible(outputDepth)) return false;
        return scaledBlit(inputDepth, outputDepth, srcW, srcH, dstW, dstH, GL11.GL_DEPTH_BUFFER_BIT, GL11.GL_NEAREST);
    }

    private boolean depthCompatible(int texture) {
        GL33.glBindSampler(0, 0);
        GL11.glBindTexture(GL11.GL_TEXTURE_2D, texture);
        int internal = GL11.glGetTexLevelParameteri(GL11.GL_TEXTURE_2D, 0, GL11.GL_TEXTURE_INTERNAL_FORMAT);
        int width = GL11.glGetTexLevelParameteri(GL11.GL_TEXTURE_2D, 0, GL11.GL_TEXTURE_WIDTH);
        int height = GL11.glGetTexLevelParameteri(GL11.GL_TEXTURE_2D, 0, GL11.GL_TEXTURE_HEIGHT);
        boolean ok = width > 0 && height > 0 && (internal == DEPTH_INTERNAL || internal == GL11.GL_DEPTH_COMPONENT
            || internal == GL14.GL_DEPTH_COMPONENT16 || internal == GL14.GL_DEPTH_COMPONENT32 || internal == GL30.GL_DEPTH_COMPONENT32F);
        return ok;
    }

    private boolean scaledBlit(int source, int target, int srcW, int srcH, int dstW, int dstH, int mask, int filter) {
        if (readFbo == 0 || drawFbo == 0) return false;
        GL33.glBindSampler(0, 0);
        int attachment = mask == GL11.GL_DEPTH_BUFFER_BIT ? GL30.GL_DEPTH_ATTACHMENT : GL30.GL_COLOR_ATTACHMENT0;
        GL30.glBindFramebuffer(GL30.GL_READ_FRAMEBUFFER, readFbo);
        GL30.glFramebufferTexture2D(GL30.GL_READ_FRAMEBUFFER, attachment, GL11.GL_TEXTURE_2D, source, 0);
        GL30.glBindFramebuffer(GL30.GL_DRAW_FRAMEBUFFER, drawFbo);
        GL30.glFramebufferTexture2D(GL30.GL_DRAW_FRAMEBUFFER, attachment, GL11.GL_TEXTURE_2D, target, 0);
        if (mask == GL11.GL_COLOR_BUFFER_BIT) {
            GL11.glReadBuffer(GL30.GL_COLOR_ATTACHMENT0);
            GL11.glDrawBuffer(GL30.GL_COLOR_ATTACHMENT0);
        } else {
            GL11.glReadBuffer(GL11.GL_NONE);
            GL11.glDrawBuffer(GL11.GL_NONE);
        }
        int readStatus = GL30.glCheckFramebufferStatus(GL30.GL_READ_FRAMEBUFFER);
        int drawStatus = GL30.glCheckFramebufferStatus(GL30.GL_DRAW_FRAMEBUFFER);
        boolean complete = readStatus == GL30.GL_FRAMEBUFFER_COMPLETE && drawStatus == GL30.GL_FRAMEBUFFER_COMPLETE;
        drainErrors();
        if (complete) GL30.glBlitFramebuffer(0, 0, srcW, srcH, 0, 0, dstW, dstH, mask, filter);
        int error = GL11.glGetError();
        GL30.glFramebufferTexture2D(GL30.GL_READ_FRAMEBUFFER, attachment, GL11.GL_TEXTURE_2D, 0, 0);
        GL30.glFramebufferTexture2D(GL30.GL_DRAW_FRAMEBUFFER, attachment, GL11.GL_TEXTURE_2D, 0, 0);
        drainErrors();
        return complete && error == GL11.GL_NO_ERROR;
    }

    private boolean drawColor(int fbo, int target, int width, int height) {
        int binding = GL11.glGetInteger(GL11.GL_TEXTURE_BINDING_2D);
        GL11.glBindTexture(GL11.GL_TEXTURE_2D, target);
        int format = GL11.glGetTexLevelParameteri(GL11.GL_TEXTURE_2D, 0, GL11.GL_TEXTURE_INTERNAL_FORMAT);
        GL11.glBindTexture(GL11.GL_TEXTURE_2D, binding);
        if (format == GL21.GL_SRGB8_ALPHA8 || format == GL21.GL_SRGB8) GL11.glEnable(GL30.GL_FRAMEBUFFER_SRGB);
        else GL11.glDisable(GL30.GL_FRAMEBUFFER_SRGB);
        GL30.glBindFramebuffer(GL30.GL_DRAW_FRAMEBUFFER, fbo);
        if (fbo != scratchFbo) {
            GL30.glFramebufferTexture2D(GL30.GL_DRAW_FRAMEBUFFER, GL30.GL_COLOR_ATTACHMENT0, GL11.GL_TEXTURE_2D, target, 0);
        }
        GL11.glDrawBuffer(GL30.GL_COLOR_ATTACHMENT0);
        if (GL30.glCheckFramebufferStatus(GL30.GL_DRAW_FRAMEBUFFER) != GL30.GL_FRAMEBUFFER_COMPLETE) return false;
        GL11.glViewport(0, 0, width, height);
        GL11.glDrawArrays(GL11.GL_TRIANGLES, 0, 3);
        if (fbo != scratchFbo) {
            GL30.glFramebufferTexture2D(GL30.GL_DRAW_FRAMEBUFFER, GL30.GL_COLOR_ATTACHMENT0, GL11.GL_TEXTURE_2D, 0, 0);
        }
        return true;
    }


    /** Official FsrEasuCon, evaluated on the CPU so the shader receives the same bit pattern. */
    static void easuConstants(float[] con0, float[] con1, float[] con2, float[] con3,
                              int srcW, int srcH, int dstW, int dstH) {
        float vx = srcW, vy = srcH, ix = srcW, iy = srcH, ox = dstW, oy = dstH;
        con0[0] = vx / ox;
        con0[1] = vy / oy;
        con0[2] = 0.5f * vx / ox - 0.5f;
        con0[3] = 0.5f * vy / oy - 0.5f;
        con1[0] = 1f / ix;
        con1[1] = 1f / iy;
        con1[2] = 1f / ix;
        con1[3] = -1f / iy;
        con2[0] = -1f / ix;
        con2[1] = 2f / iy;
        con2[2] = 1f / ix;
        con2[3] = 2f / iy;
        con3[0] = 0f;
        con3[1] = 4f / iy;
        con3[2] = 0f;
        con3[3] = 0f;
    }


    private static int framebuffer(int color) {
        int fbo = GL30.glGenFramebuffers();
        GL30.glBindFramebuffer(GL30.GL_DRAW_FRAMEBUFFER, fbo);
        GL30.glFramebufferTexture2D(GL30.GL_DRAW_FRAMEBUFFER, GL30.GL_COLOR_ATTACHMENT0, GL11.GL_TEXTURE_2D, color, 0);
        GL11.glDrawBuffer(GL30.GL_COLOR_ATTACHMENT0);
        if (GL30.glCheckFramebufferStatus(GL30.GL_DRAW_FRAMEBUFFER) != GL30.GL_FRAMEBUFFER_COMPLETE) {
            GL30.glDeleteFramebuffers(fbo);
            throw new IllegalStateException("FSR scratch framebuffer incomplete");
        }
        return fbo;
    }

    private static int program(String vertex, String fragment) {
        int program = GL20.glCreateProgram(), vs = 0, fs = 0;
        boolean linked = false;
        try {
            vs = shader(GL20.GL_VERTEX_SHADER, vertex);
            fs = shader(GL20.GL_FRAGMENT_SHADER, fragment);
            GL20.glAttachShader(program, vs); GL20.glAttachShader(program, fs);
            GL20.glLinkProgram(program);
            if (GL20.glGetProgrami(program, GL20.GL_LINK_STATUS) == GL11.GL_FALSE)
                throw new IllegalStateException("FSR link failed: " + GL20.glGetProgramInfoLog(program));
            GL20.glDetachShader(program, vs); GL20.glDetachShader(program, fs);
            linked = true; return program;
        } finally {
            if (vs != 0) GL20.glDeleteShader(vs);
            if (fs != 0) GL20.glDeleteShader(fs);
            if (!linked) GL20.glDeleteProgram(program);
        }
    }

    private static int shader(int type, String source) {
        int shader = GL20.glCreateShader(type);
        GL20.glShaderSource(shader, source);
        GL20.glCompileShader(shader);
        if (GL20.glGetShaderi(shader, GL20.GL_COMPILE_STATUS) == GL11.GL_FALSE) {
            String log = GL20.glGetShaderInfoLog(shader);
            GL20.glDeleteShader(shader);
            throw new IllegalStateException("FSR compile failed: " + log);
        }
        return shader;
    }

    private static int uniform(int program, String name) {
        int location = GL20.glGetUniformLocation(program, name);
        if (location < 0) throw new IllegalStateException("missing FSR uniform " + name);
        return location;
    }

    private static String source(String name) {
        Map<String, String> files = new HashMap<String, String>();
        for (String required : new String[] {"ffx_a.h", "ffx_fsr1.h", name}) files.put(required, text(ROOT + required));
        return preprocess(files.get(name), files);
    }

    private static String preprocess(String text, Map<String, String> files) {
        StringBuilder out = new StringBuilder(text.length() + 256);
        for (String line : text.split("\n", -1)) {
            String trimmed = line.trim();
            if (trimmed.startsWith("#include")) {
                int quote = trimmed.indexOf('"');
                int end = trimmed.lastIndexOf('"');
                if (quote < 0 || end <= quote) throw new IllegalStateException("bad FSR include: " + line);
                String included = files.get(trimmed.substring(quote + 1, end));
                if (included == null) throw new IllegalStateException("missing FSR include: " + line);
                out.append(preprocess(included, files)).append('\n');
            } else {
                if (trimmed.startsWith("con") && (trimmed.endsWith("=0;") || trimmed.endsWith("=0}"))) {
                    line = line.replace("=0;", "=0u;").replace("=0}", "=0u}");
                }
                out.append(line).append('\n');
            }
        }
        return out.toString();
    }

    private static boolean resourcesPresent() {
        for (String name : REQUIRED) if (GlSuperResolutionRenderer.class.getResource(ROOT + name) == null) return false;
        return true;
    }

    private static String text(String path) {
        InputStream in = GlSuperResolutionRenderer.class.getResourceAsStream(path);
        if (in == null) throw new IllegalStateException("missing FSR resource " + path);
        try {
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            byte[] buffer = new byte[8192];
            int n;
            while ((n = in.read(buffer)) >= 0) out.write(buffer, 0, n);
            return new String(out.toByteArray(), StandardCharsets.UTF_8).replace("\r\n", "\n");
        } catch (IOException failure) {
            throw new IllegalStateException("unreadable FSR resource " + path, failure);
        } finally {
            try { in.close(); } catch (IOException ignored) { }
        }
    }

    private static boolean gl33() {
        try {
            return GL.getCapabilities().OpenGL33
                && GL11.glGetInteger(GL30.GL_MAJOR_VERSION) >= 3
                && (GL11.glGetInteger(GL30.GL_MAJOR_VERSION) > 3 || GL11.glGetInteger(GL30.GL_MINOR_VERSION) >= 3);
        } catch (RuntimeException unavailable) {
            return false;
        }
    }

    private boolean fail() {
        supported = false;
        return false;
    }

    private static boolean drainErrors() {
        boolean clean = true;
        int error;
        while ((error = GL11.glGetError()) != GL11.GL_NO_ERROR) clean = false;
        return clean;
    }

    private static void deleteProgram(int program) {
        if (program != 0) GL20.glDeleteProgram(program);
    }

    /** Saves and restores every binding this pass touches, without ending an external query. */
    private static final class GlState {
        private final int readFbo;
        private final int drawFbo;
        private final int[] viewport = new int[4];
        private final int[] scissorBox = new int[4];
        private final int program;
        private final int vao;
        private final int active;
        private final int[] bound = new int[4];
        private final int[] sampler = new int[4];
        private final boolean[] blend = new boolean[2];
        private final boolean depth;
        private final boolean depthMask;
        private final boolean cull;
        private final boolean scissor;
        private final boolean stencil;
        private final boolean srgb;
        private final boolean discard;
        private final boolean dither;
        private final int[] mask = new int[8];
        private final int[] polygon = new int[2];
        private final int unpack;
        private final int pack;
        private final int unpackAlignment;
        private final int packAlignment;

        private GlState() {
            readFbo = GL11.glGetInteger(GL30.GL_READ_FRAMEBUFFER_BINDING);
            drawFbo = GL11.glGetInteger(GL30.GL_DRAW_FRAMEBUFFER_BINDING);
            GL11.glGetIntegerv(GL11.GL_VIEWPORT, viewport);
            GL11.glGetIntegerv(GL11.GL_SCISSOR_BOX, scissorBox);
            program = GL11.glGetInteger(GL20.GL_CURRENT_PROGRAM);
            vao = GL11.glGetInteger(GL30.GL_VERTEX_ARRAY_BINDING);
            active = GL11.glGetInteger(GL13.GL_ACTIVE_TEXTURE);
            for (int i = 0; i < 4; i++) sampler[i] = GL33.glGetIntegeri(GL33.GL_SAMPLER_BINDING, i);
            for (int i = 0; i < 4; i++) GL33.glBindSampler(i, 0);
            for (int i = 0; i < 4; i++) {
                GL13.glActiveTexture(GL13.GL_TEXTURE0 + i);
                bound[i] = GL11.glGetInteger(GL11.GL_TEXTURE_BINDING_2D);
            }
            for (int i = 0; i < blend.length; i++) blend[i] = GL30.glIsEnabledi(GL11.GL_BLEND, i);
            depth = GL11.glIsEnabled(GL11.GL_DEPTH_TEST);
            depthMask = GL11.glGetBoolean(GL11.GL_DEPTH_WRITEMASK);
            cull = GL11.glIsEnabled(GL11.GL_CULL_FACE);
            scissor = GL11.glIsEnabled(GL11.GL_SCISSOR_TEST);
            stencil = GL11.glIsEnabled(GL11.GL_STENCIL_TEST);
            srgb = GL11.glIsEnabled(GL30.GL_FRAMEBUFFER_SRGB);
            discard = GL11.glIsEnabled(GL30.GL_RASTERIZER_DISCARD);
            dither = GL11.glIsEnabled(GL11.GL_DITHER);
            ByteBuffer masks = ByteBuffer.allocateDirect(4);
            for (int i = 0; i < 2; i++) {
                masks.clear();
                GL30.glGetBooleani_v(GL11.GL_COLOR_WRITEMASK, i, masks);
                for (int j = 0; j < 4; j++) mask[i * 4 + j] = masks.get(j) != 0 ? 1 : 0;
            }
            GL11.glGetIntegerv(GL11.GL_POLYGON_MODE, polygon);
            unpack = GL11.glGetInteger(GL21.GL_PIXEL_UNPACK_BUFFER_BINDING);
            pack = GL11.glGetInteger(GL21.GL_PIXEL_PACK_BUFFER_BINDING);
            unpackAlignment = GL11.glGetInteger(GL11.GL_UNPACK_ALIGNMENT);
            packAlignment = GL11.glGetInteger(GL11.GL_PACK_ALIGNMENT);
        }

        static GlState capture() {
            GlState state = new GlState();
            for (int i = 0; i < state.blend.length; i++) GL30.glDisablei(GL11.GL_BLEND, i);
            GL11.glDisable(GL11.GL_DEPTH_TEST);
            GL11.glDepthMask(true);
            GL11.glDisable(GL11.GL_CULL_FACE);
            GL11.glDisable(GL11.GL_SCISSOR_TEST);
            GL11.glDisable(GL11.GL_STENCIL_TEST);
            GL11.glDisable(GL30.GL_FRAMEBUFFER_SRGB);
            GL11.glDisable(GL30.GL_RASTERIZER_DISCARD);
            GL11.glDisable(GL11.GL_DITHER);
            GL30.glColorMaski(0, true, true, true, true);
            GL30.glColorMaski(1, true, true, true, true);
            GL11.glPolygonMode(GL11.GL_FRONT_AND_BACK, GL11.GL_FILL);
            GL15.glBindBuffer(GL21.GL_PIXEL_UNPACK_BUFFER, 0);
            GL15.glBindBuffer(GL21.GL_PIXEL_PACK_BUFFER, 0);
            GL11.glPixelStorei(GL11.GL_UNPACK_ALIGNMENT, 4);
            GL11.glPixelStorei(GL11.GL_PACK_ALIGNMENT, 4);
            for (int i = 0; i < 4; i++) GL33.glBindSampler(i, 0);
            return state;
        }

        void restore() {
            GL11.glPixelStorei(GL11.GL_PACK_ALIGNMENT, packAlignment);
            GL11.glPixelStorei(GL11.GL_UNPACK_ALIGNMENT, unpackAlignment);
            GL15.glBindBuffer(GL21.GL_PIXEL_PACK_BUFFER, pack);
            GL15.glBindBuffer(GL21.GL_PIXEL_UNPACK_BUFFER, unpack);
            if ((GL11.glGetInteger(0x9126) & 1) != 0 || polygon[0] == polygon[1]) {
                GL11.glPolygonMode(GL11.GL_FRONT_AND_BACK, polygon[0]);
            } else {
                GL11.glPolygonMode(GL11.GL_FRONT, polygon[0]);
                GL11.glPolygonMode(GL11.GL_BACK, polygon[1]);
            }
            GL30.glColorMaski(0, mask[0] != 0, mask[1] != 0, mask[2] != 0, mask[3] != 0);
            GL30.glColorMaski(1, mask[4] != 0, mask[5] != 0, mask[6] != 0, mask[7] != 0);
            cap(GL11.GL_DITHER, dither);
            cap(GL30.GL_RASTERIZER_DISCARD, discard);
            cap(GL30.GL_FRAMEBUFFER_SRGB, srgb);
            cap(GL11.GL_STENCIL_TEST, stencil);
            cap(GL11.GL_SCISSOR_TEST, scissor);
            cap(GL11.GL_CULL_FACE, cull);
            GL11.glDepthMask(depthMask);
            cap(GL11.GL_DEPTH_TEST, depth);
            for (int i = 0; i < blend.length; i++) if (blend[i]) GL30.glEnablei(GL11.GL_BLEND, i); else GL30.glDisablei(GL11.GL_BLEND, i);
            for (int i = 0; i < 4; i++) {
                GL13.glActiveTexture(GL13.GL_TEXTURE0 + i);
                GL33.glBindSampler(i, sampler[i]);
                GL11.glBindTexture(GL11.GL_TEXTURE_2D, bound[i]);
            }
            GL13.glActiveTexture(active);
            GL30.glBindVertexArray(vao);
            GL20.glUseProgram(program);
            GL11.glViewport(viewport[0], viewport[1], viewport[2], viewport[3]);
            GL11.glScissor(scissorBox[0], scissorBox[1], scissorBox[2], scissorBox[3]);
            GL30.glBindFramebuffer(GL30.GL_READ_FRAMEBUFFER, readFbo);
            GL30.glBindFramebuffer(GL30.GL_DRAW_FRAMEBUFFER, drawFbo);
        }

        private static void cap(int name, boolean enabled) {
            if (enabled) GL11.glEnable(name);
            else GL11.glDisable(name);
        }
    }
}
