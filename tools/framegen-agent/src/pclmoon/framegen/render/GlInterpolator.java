package pclmoon.framegen.render;

import org.lwjgl.glfw.GLFW;
import org.lwjgl.opengl.GL11;
import org.lwjgl.opengl.GL12;
import org.lwjgl.opengl.GL13;
import org.lwjgl.opengl.GL15;
import org.lwjgl.opengl.GL20;
import org.lwjgl.opengl.GL21;
import org.lwjgl.opengl.GL30;
import org.lwjgl.opengl.GL33;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.IntBuffer;

/**
 * Independent bidirectional block-matching frame interpolator.
 *
 * Captures the default framebuffer back buffer into a pair of textures and
 * composites a t=0.5 frame from prev-to-current and current-to-prev motion.
 * Requires an OpenGL 3.2 context current on the calling thread. Does not bind
 * to a Minecraft version. GPU timer queries run only when GL 3.3 is present;
 * isOverBudget never stalls on a query.
 */
public final class GlInterpolator {
    public static final float BUDGET_MS = 4.0f;
    private static final int FLOW_MAX_WIDTH = 160;
    private static final int GL_TEXTURE_COMPARE_MODE = 0x884C;

    private static final String VERT =
            "#version 150\n" +
            "out vec2 vUv;\n" +
            "void main() {\n" +
            "    vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));\n" +
            "    vUv = p;\n" +
            "    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);\n" +
            "}\n";

    private static final String FLOW_FRAG =
            "#version 150\n" +
            "uniform sampler2D uSrc;\n" +
            "uniform sampler2D uDst;\n" +
            "uniform vec2 uFullTexel;\n" +
            "uniform vec2 uCoarse;\n" +
            "uniform vec2 uFine;\n" +
            "in vec2 vUv;\n" +
            "out vec4 fragColor;\n" +
            "float luma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }\n" +
            "float patchCost(vec2 uv, vec2 disp) {\n" +
            "    float acc = 0.0;\n" +
            "    for (int y = -1; y <= 1; y++) {\n" +
            "        for (int x = -1; x <= 1; x++) {\n" +
            "            vec2 o = vec2(float(x), float(y)) * uFullTexel;\n" +
            "            vec3 a = texture(uSrc, clamp(uv + o, vec2(0.0), vec2(1.0))).rgb;\n" +
            "            vec3 b = texture(uDst, clamp(uv + disp + o, vec2(0.0), vec2(1.0))).rgb;\n" +
            "            vec3 d = abs(a - b);\n" +
            "            acc += luma(d);\n" +
            "        }\n" +
            "    }\n" +
            "    return acc;\n" +
            "}\n" +
            "void consider(inout vec2 best, inout float bestCost, vec2 cand) {\n" +
            "    float c = patchCost(vUv, cand);\n" +
            "    if (c < bestCost) { bestCost = c; best = cand; }\n" +
            "}\n" +
            "void main() {\n" +
            "    vec2 best = vec2(0.0);\n" +
            "    float bestCost = patchCost(vUv, best);\n" +
            "    for (int y = -2; y <= 2; y++) {\n" +
            "        for (int x = -2; x <= 2; x++) {\n" +
            "            consider(best, bestCost, vec2(float(x), float(y)) * uCoarse);\n" +
            "        }\n" +
            "    }\n" +
            "    vec2 stepSize = uFine;\n" +
            "    for (int level = 0; level < 8; level++) {\n" +
            "        vec2 coarse = best;\n" +
            "        for (int y = -1; y <= 1; y++) {\n" +
            "            for (int x = -1; x <= 1; x++) {\n" +
            "                consider(best, bestCost, coarse + vec2(float(x), float(y)) * stepSize);\n" +
            "            }\n" +
            "        }\n" +
            "        if (all(lessThanEqual(stepSize, uFullTexel))) break;\n" +
            "        stepSize = max(stepSize * 0.5, uFullTexel);\n" +
            "    }\n" +
            "    fragColor = vec4(best * 0.5 + 0.5, clamp(bestCost / 4.5, 0.0, 1.0), 1.0);\n" +
            "}\n";

    private static final String COMPOSE_FRAG =
            "#version 150\n" +
            "uniform sampler2D uPrev;\n" +
            "uniform sampler2D uCur;\n" +
            "uniform sampler2D uFwd;\n" +
            "uniform sampler2D uBwd;\n" +
            "in vec2 vUv;\n" +
            "out vec4 fragColor;\n" +
            "float luma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }\n" +
            "void main() {\n" +
            "    vec4 fwd = texture(uFwd, vUv);\n" +
            "    vec4 bwd = texture(uBwd, vUv);\n" +
            "    vec2 dFwd = (fwd.xy - 0.5) * 2.0;\n" +
            "    vec2 dBwd = (bwd.xy - 0.5) * 2.0;\n" +
            "    vec2 prevUv = vUv - dFwd * 0.5;\n" +
            "    vec2 curUv = vUv - dBwd * 0.5;\n" +
            "    bool inside = prevUv.x > 0.0 && prevUv.y > 0.0 && prevUv.x < 1.0 && prevUv.y < 1.0\n" +
            "        && curUv.x > 0.0 && curUv.y > 0.0 && curUv.x < 1.0 && curUv.y < 1.0;\n" +
            "    vec3 fromPrev = texture(uPrev, prevUv).rgb;\n" +
            "    vec3 fromCur = texture(uCur, curUv).rgb;\n" +
            "    vec3 cur = texture(uCur, vUv).rgb;\n" +
            "    float photo = max(max(abs(fromPrev.r-fromCur.r), abs(fromPrev.g-fromCur.g)), abs(fromPrev.b-fromCur.b));\n" +
            "    float agree = length(dFwd + dBwd);\n" +
            "    bool huge = length(dFwd) > 0.5 || length(dBwd) > 0.5;\n" +
            "    bool reject = !inside || huge || photo > 0.22 || agree > 0.08 || fwd.z > 0.45 || bwd.z > 0.45;\n" +
            "    fragColor = vec4(reject ? cur : 0.5 * (fromPrev + fromCur), 1.0);\n" +
            "}\n";

    private static final String BLIT_FRAG =
            "#version 150\n" +
            "uniform sampler2D uTex;\n" +
            "in vec2 vUv;\n" +
            "out vec4 fragColor;\n" +
            "void main() { fragColor = vec4(texture(uTex, vUv).rgb, 1.0); }\n";

    private final long window;
    private boolean probed;
    private boolean supported;
    private boolean failed;
    private boolean closed;
    private int width;
    private int height;
    private int flowW;
    private int flowH;
    private int framesCaptured;
    private int writeSlot;
    private boolean gl33;

    private int vao;
    private int flowProgram;
    private int composeProgram;
    private int blitProgram;
    private int flowFbo;
    private final int[] frameTex = new int[2];
    private final int[] flowTex = new int[2];
    private final int[] queries = new int[2];
    private int querySlot;
    private boolean queryBusy;
    private boolean queryRunning;
    private boolean overBudget;

    private int locSrc;
    private int locDst;
    private int locFullTexel;
    private int locCoarse;
    private int locFine;
    private int locPrev;
    private int locCur;
    private int locFwd;
    private int locBwd;
    private int locBlit;

    private final IntBuffer widthBuf = ByteBuffer.allocateDirect(4).order(ByteOrder.nativeOrder()).asIntBuffer();
    private final IntBuffer heightBuf = ByteBuffer.allocateDirect(4).order(ByteOrder.nativeOrder()).asIntBuffer();

    public GlInterpolator(long window) {
        if (window == 0L) {
            throw new IllegalArgumentException("window");
        }
        this.window = window;
    }

    public boolean capture() {
        if (closed || failed) {
            return false;
        }
        if (!prepare()) return false;
        GlState state = GlState.capture(gl33);
        try {
            readSize();
            int w = widthBuf.get(0);
            int h = heightBuf.get(0);
            if (w <= 0 || h <= 0) {
                return false;
            }
            if (w != width || h != height || vao == 0) {
                rebuild(w, h);
                // Capture this first real frame after allocation, but do not synthesize it yet.
            }
            GL30.glBindFramebuffer(GL30.GL_READ_FRAMEBUFFER, 0);
            GL11.glReadBuffer(GL11.GL_BACK);
            GL13.glActiveTexture(GL13.GL_TEXTURE0);
            GL11.glBindTexture(GL11.GL_TEXTURE_2D, frameTex[writeSlot]);
            GL15.glBindBuffer(GL21.GL_PIXEL_UNPACK_BUFFER, 0);
            GL11.glCopyTexSubImage2D(GL11.GL_TEXTURE_2D, 0, 0, 0, 0, 0, width, height);
            writeSlot ^= 1;
            if (framesCaptured < 2) {
                framesCaptured++;
            }
            pollBudget();
            return framesCaptured >= 2;
        } catch (RuntimeException ex) {
            fuse(ex);
            return false;
        } finally {
            state.restore();
        }
    }

    public void drawIntermediate() {
        if (closed || failed || framesCaptured < 2 || vao == 0) {
            return;
        }
        GlState state = GlState.capture(gl33);
        try {
            int prev = writeSlot;
            int cur = writeSlot ^ 1;
            beginBudgetQuery();
            dispatchFlow(flowTex[0], frameTex[prev], frameTex[cur]);
            dispatchFlow(flowTex[1], frameTex[cur], frameTex[prev]);
            endBudgetQuery();
            GL30.glBindFramebuffer(GL30.GL_DRAW_FRAMEBUFFER, 0);
            GL11.glDrawBuffer(GL11.GL_BACK);
            GL11.glViewport(0, 0, width, height);
            GL20.glUseProgram(composeProgram);
            bindUnit(0, frameTex[prev], locPrev);
            bindUnit(1, frameTex[cur], locCur);
            bindUnit(2, flowTex[0], locFwd);
            bindUnit(3, flowTex[1], locBwd);
            GL30.glBindVertexArray(vao);
            GL11.glDrawArrays(GL11.GL_TRIANGLES, 0, 3);
            pollBudget();
        } catch (RuntimeException ex) {
            if (queryRunning) endBudgetQuery();
            throw ex;
        } finally {
            state.restore();
        }
    }

    public void drawCurrent() {
        if (closed || failed || framesCaptured < 1 || vao == 0) {
            return;
        }
        GlState state = GlState.capture(gl33);
        try {
            GL30.glBindFramebuffer(GL30.GL_DRAW_FRAMEBUFFER, 0);
            GL11.glDrawBuffer(GL11.GL_BACK);
            GL11.glViewport(0, 0, width, height);
            GL20.glUseProgram(blitProgram);
            bindUnit(0, frameTex[writeSlot ^ 1], locBlit);
            GL30.glBindVertexArray(vao);
            GL11.glDrawArrays(GL11.GL_TRIANGLES, 0, 3);
        } catch (RuntimeException ex) {
            throw ex;
        } finally {
            state.restore();
        }
    }

    public void resetHistory() {
        framesCaptured = 0;
        writeSlot = 0;
        overBudget = false;
        queryBusy = false;
    }

    public boolean isOverBudget() {
        return gl33 && overBudget;
    }

    public void close() {
        if (closed) {
            return;
        }
        closed = true;
        release();
        resetHistory();
    }

    private boolean prepare() {
        if (GLFW.glfwGetCurrentContext() != window) {
            return false;
        }
        if (!probed) {
            probed = true;
            int major = GL11.glGetInteger(GL30.GL_MAJOR_VERSION);
            int minor = GL11.glGetInteger(GL30.GL_MINOR_VERSION);
            supported = major > 3 || (major == 3 && minor >= 2);
            gl33 = major > 3 || (major == 3 && minor >= 3);
            if (supported) {
                int draw = GL11.glGetInteger(GL30.GL_DRAW_FRAMEBUFFER_BINDING);
                try {
                    GL30.glBindFramebuffer(GL30.GL_DRAW_FRAMEBUFFER, 0);
                    supported = GL11.glGetBoolean(GL11.GL_DOUBLEBUFFER);
                } finally { GL30.glBindFramebuffer(GL30.GL_DRAW_FRAMEBUFFER, draw); }
            }
        }
        return supported;
    }

    private void readSize() {
        widthBuf.clear();
        heightBuf.clear();
        GLFW.glfwGetFramebufferSize(window, widthBuf, heightBuf);
    }

    private void rebuild(int w, int h) {
        release();
        resetHistory();
        width = w;
        height = h;
        flowW = Math.min(FLOW_MAX_WIDTH, w);
        flowH = Math.max(1, (int) Math.round(h * (flowW / (double) w)));
        try {
            vao = GL30.glGenVertexArrays();
            if (vao == 0) {
                throw new IllegalStateException("vao");
            }
            GL30.glBindVertexArray(vao);
            flowProgram = link(VERT, FLOW_FRAG);
            composeProgram = link(VERT, COMPOSE_FRAG);
            blitProgram = link(VERT, BLIT_FRAG);
            locSrc = GL20.glGetUniformLocation(flowProgram, "uSrc");
            locDst = GL20.glGetUniformLocation(flowProgram, "uDst");
            locFullTexel = GL20.glGetUniformLocation(flowProgram, "uFullTexel");
            locCoarse = GL20.glGetUniformLocation(flowProgram, "uCoarse");
            locFine = GL20.glGetUniformLocation(flowProgram, "uFine");
            locPrev = GL20.glGetUniformLocation(composeProgram, "uPrev");
            locCur = GL20.glGetUniformLocation(composeProgram, "uCur");
            locFwd = GL20.glGetUniformLocation(composeProgram, "uFwd");
            locBwd = GL20.glGetUniformLocation(composeProgram, "uBwd");
            locBlit = GL20.glGetUniformLocation(blitProgram, "uTex");
            frameTex[0] = alloc(width, height, GL11.GL_RGBA8, GL11.GL_UNSIGNED_BYTE);
            frameTex[1] = alloc(width, height, GL11.GL_RGBA8, GL11.GL_UNSIGNED_BYTE);
            flowTex[0] = alloc(flowW, flowH, GL30.GL_RGBA16F, GL11.GL_FLOAT);
            flowTex[1] = alloc(flowW, flowH, GL30.GL_RGBA16F, GL11.GL_FLOAT);
            flowFbo = GL30.glGenFramebuffers();
            if (flowFbo == 0) {
                throw new IllegalStateException("fbo");
            }
            if (gl33) {
                GL33.glGenQueries(queries);
                if (queries[0] == 0 || queries[1] == 0) {
                    throw new IllegalStateException("query");
                }
            }
            GL30.glBindVertexArray(0);
        } catch (RuntimeException ex) {
            release();
            throw ex;
        }
    }

    private void dispatchFlow(int target, int src, int dst) {
        GL30.glBindFramebuffer(GL30.GL_DRAW_FRAMEBUFFER, flowFbo);
        GL30.glFramebufferTexture2D(GL30.GL_DRAW_FRAMEBUFFER, GL30.GL_COLOR_ATTACHMENT0,
                GL11.GL_TEXTURE_2D, target, 0);
        if (GL30.glCheckFramebufferStatus(GL30.GL_DRAW_FRAMEBUFFER) != GL30.GL_FRAMEBUFFER_COMPLETE) {
            throw new IllegalStateException("flow fbo");
        }
        GL11.glDrawBuffer(GL30.GL_COLOR_ATTACHMENT0);
        GL11.glViewport(0, 0, flowW, flowH);
        GL20.glUseProgram(flowProgram);
        bindUnit(0, src, locSrc);
        bindUnit(1, dst, locDst);
        float coarse = 4.0f * width / flowW;
        GL20.glUniform2f(locFullTexel, 1.0f / width, 1.0f / height);
        GL20.glUniform2f(locCoarse, coarse / width, coarse / height);
        GL20.glUniform2f(locFine, Math.max(1.0f, coarse * 0.5f) / width, Math.max(1.0f, coarse * 0.5f) / height);
        GL30.glBindVertexArray(vao);
        GL11.glDrawArrays(GL11.GL_TRIANGLES, 0, 3);
    }

    private void bindUnit(int unit, int texture, int location) {
        GL13.glActiveTexture(GL13.GL_TEXTURE0 + unit);
        GL11.glBindTexture(GL11.GL_TEXTURE_2D, texture);
        if (location >= 0) {
            GL20.glUniform1i(location, unit);
        }
    }

    private static int alloc(int w, int h, int internalFormat, int type) {
        int tex = GL11.glGenTextures();
        if (tex == 0) {
            throw new IllegalStateException("texture");
        }
        GL15.glBindBuffer(GL21.GL_PIXEL_UNPACK_BUFFER, 0);
        GL11.glBindTexture(GL11.GL_TEXTURE_2D, tex);
        GL11.glTexParameteri(GL11.GL_TEXTURE_2D, GL11.GL_TEXTURE_MIN_FILTER, GL11.GL_LINEAR);
        GL11.glTexParameteri(GL11.GL_TEXTURE_2D, GL11.GL_TEXTURE_MAG_FILTER, GL11.GL_LINEAR);
        GL11.glTexParameteri(GL11.GL_TEXTURE_2D, GL11.GL_TEXTURE_WRAP_S, GL12.GL_CLAMP_TO_EDGE);
        GL11.glTexParameteri(GL11.GL_TEXTURE_2D, GL11.GL_TEXTURE_WRAP_T, GL12.GL_CLAMP_TO_EDGE);
        GL11.glTexParameteri(GL11.GL_TEXTURE_2D, GL_TEXTURE_COMPARE_MODE, GL11.GL_NONE);
        GL11.glTexImage2D(GL11.GL_TEXTURE_2D, 0, internalFormat, w, h, 0, GL11.GL_RGBA, type, 0L);
        return tex;
    }

    private static int link(String vertSrc, String fragSrc) {
        int vs = compile(GL20.GL_VERTEX_SHADER, vertSrc);
        int fs;
        try { fs = compile(GL20.GL_FRAGMENT_SHADER, fragSrc); }
        catch (RuntimeException failure) { GL20.glDeleteShader(vs); throw failure; }
        int program = GL20.glCreateProgram();
        if (program == 0) {
            GL20.glDeleteShader(vs);
            GL20.glDeleteShader(fs);
            throw new IllegalStateException("program");
        }
        GL20.glAttachShader(program, vs);
        GL20.glAttachShader(program, fs);
        GL20.glLinkProgram(program);
        GL20.glDetachShader(program, vs);
        GL20.glDetachShader(program, fs);
        GL20.glDeleteShader(vs);
        GL20.glDeleteShader(fs);
        if (GL20.glGetProgrami(program, GL20.GL_LINK_STATUS) == GL11.GL_FALSE) {
            String log = GL20.glGetProgramInfoLog(program);
            GL20.glDeleteProgram(program);
            throw new IllegalStateException(log);
        }
        return program;
    }

    private static int compile(int type, String source) {
        int shader = GL20.glCreateShader(type);
        if (shader == 0) {
            throw new IllegalStateException("shader");
        }
        GL20.glShaderSource(shader, source);
        GL20.glCompileShader(shader);
        if (GL20.glGetShaderi(shader, GL20.GL_COMPILE_STATUS) == GL11.GL_FALSE) {
            String log = GL20.glGetShaderInfoLog(shader);
            GL20.glDeleteShader(shader);
            throw new IllegalStateException(log);
        }
        return shader;
    }

    private void beginBudgetQuery() {
        if (!gl33 || queryBusy || queries[querySlot] == 0
                || GL15.glGetQueryi(GL33.GL_TIME_ELAPSED, GL15.GL_CURRENT_QUERY) != 0) {
            return;
        }
        GL33.glBeginQuery(GL33.GL_TIME_ELAPSED, queries[querySlot]);
        queryBusy = true;
        queryRunning = true;
    }

    private void endBudgetQuery() {
        if (!gl33 || !queryRunning) {
            return;
        }
        GL33.glEndQuery(GL33.GL_TIME_ELAPSED);
        queryRunning = false;
    }

    private void pollBudget() {
        if (!gl33 || !queryBusy || queryRunning) {
            return;
        }
        int id = queries[querySlot];
        if (GL33.glGetQueryObjectui(id, GL15.GL_QUERY_RESULT_AVAILABLE) == 0) {
            return;
        }
        long nanos = GL33.glGetQueryObjectui64(id, GL15.GL_QUERY_RESULT);
        overBudget = nanos > (long) (BUDGET_MS * 1000000.0f);
        queryBusy = false;
        querySlot ^= 1;
    }

    private void fuse(RuntimeException ex) {
        failed = true;
        try {
            release();
        } catch (RuntimeException ignored) {
            // Keep the original failure for the outer fuse.
        }
        resetHistory();
        throw ex;
    }

    private void release() {
        if (vao != 0) {
            GL30.glDeleteVertexArrays(vao);
            vao = 0;
        }
        deleteProgram(flowProgram);
        flowProgram = 0;
        deleteProgram(composeProgram);
        composeProgram = 0;
        deleteProgram(blitProgram);
        blitProgram = 0;
        if (flowFbo != 0) {
            GL30.glDeleteFramebuffers(flowFbo);
            flowFbo = 0;
        }
        for (int i = 0; i < frameTex.length; i++) {
            if (frameTex[i] != 0) {
                GL11.glDeleteTextures(frameTex[i]);
                frameTex[i] = 0;
            }
        }
        for (int i = 0; i < flowTex.length; i++) {
            if (flowTex[i] != 0) {
                GL11.glDeleteTextures(flowTex[i]);
                flowTex[i] = 0;
            }
        }
        if (queries[0] != 0 || queries[1] != 0) {
            GL33.glDeleteQueries(queries);
            queries[0] = 0;
            queries[1] = 0;
        }
        width = 0;
        height = 0;
        flowW = 0;
        flowH = 0;
    }

    private static void deleteProgram(int program) {
        if (program != 0) {
            GL20.glDeleteProgram(program);
        }
    }

    /**
     * Saves and restores every GL binding this renderer touches. Sampler
     * objects are saved only when the context is GL 3.3 or newer.
     */
    private static final class GlState {
        private final boolean samplers;
        private final int readFbo;
        private final int drawFbo;
        private final int readBuffer;
        private final int drawBuffer;
        private final int defaultReadBuffer;
        private final int defaultDrawBuffer;
        private final int[] viewport = new int[4];
        private final int program;
        private final int vao;
        private final int active;
        private final int[] bound = new int[4];
        private final int[] sampler = new int[4];
        private final boolean blend;
        private final boolean depth;
        private final boolean cull;
        private final boolean scissor;
        private final boolean stencil;
        private final boolean srgb;
        private final boolean discard;
        private final boolean dither;
        private final int[] mask = new int[4];
        private final int[] polygon = new int[2];
        private final int unpack;

        private GlState(boolean samplers) {
            this.samplers = samplers;
            readFbo = GL11.glGetInteger(GL30.GL_READ_FRAMEBUFFER_BINDING);
            drawFbo = GL11.glGetInteger(GL30.GL_DRAW_FRAMEBUFFER_BINDING);
            readBuffer = GL11.glGetInteger(GL11.GL_READ_BUFFER);
            drawBuffer = GL11.glGetInteger(GL11.GL_DRAW_BUFFER);
            GL30.glBindFramebuffer(GL30.GL_READ_FRAMEBUFFER, 0);
            defaultReadBuffer = GL11.glGetInteger(GL11.GL_READ_BUFFER);
            GL30.glBindFramebuffer(GL30.GL_READ_FRAMEBUFFER, readFbo);
            GL30.glBindFramebuffer(GL30.GL_DRAW_FRAMEBUFFER, 0);
            defaultDrawBuffer = GL11.glGetInteger(GL11.GL_DRAW_BUFFER);
            GL30.glBindFramebuffer(GL30.GL_DRAW_FRAMEBUFFER, drawFbo);
            GL11.glGetIntegerv(GL11.GL_VIEWPORT, viewport);
            program = GL11.glGetInteger(GL20.GL_CURRENT_PROGRAM);
            vao = GL11.glGetInteger(GL30.GL_VERTEX_ARRAY_BINDING);
            active = GL11.glGetInteger(GL13.GL_ACTIVE_TEXTURE);
            for (int i = 0; i < 4; i++) {
                GL13.glActiveTexture(GL13.GL_TEXTURE0 + i);
                bound[i] = GL11.glGetInteger(GL11.GL_TEXTURE_BINDING_2D);
                if (samplers) {
                    sampler[i] = GL11.glGetInteger(GL33.GL_SAMPLER_BINDING);
                }
            }
            blend = GL30.glIsEnabledi(GL11.GL_BLEND, 0);
            depth = GL11.glIsEnabled(GL11.GL_DEPTH_TEST);
            cull = GL11.glIsEnabled(GL11.GL_CULL_FACE);
            scissor = GL11.glIsEnabled(GL11.GL_SCISSOR_TEST);
            stencil = GL11.glIsEnabled(GL11.GL_STENCIL_TEST);
            srgb = GL11.glIsEnabled(GL30.GL_FRAMEBUFFER_SRGB);
            discard = GL11.glIsEnabled(GL30.GL_RASTERIZER_DISCARD);
            dither = GL11.glIsEnabled(GL11.GL_DITHER);
            GL11.glGetIntegerv(GL11.GL_COLOR_WRITEMASK, mask);
            GL11.glGetIntegerv(GL11.GL_POLYGON_MODE, polygon);
            unpack = GL11.glGetInteger(GL21.GL_PIXEL_UNPACK_BUFFER_BINDING);
        }

        static GlState capture(boolean samplers) {
            GlState state = new GlState(samplers);
            GL30.glDisablei(GL11.GL_BLEND, 0);
            GL11.glDisable(GL11.GL_DEPTH_TEST);
            GL11.glDisable(GL11.GL_CULL_FACE);
            GL11.glDisable(GL11.GL_SCISSOR_TEST);
            GL11.glDisable(GL11.GL_STENCIL_TEST);
            GL11.glDisable(GL30.GL_FRAMEBUFFER_SRGB);
            GL11.glDisable(GL30.GL_RASTERIZER_DISCARD);
            GL11.glDisable(GL11.GL_DITHER);
            GL30.glColorMaski(0, true, true, true, true);
            GL11.glPolygonMode(GL11.GL_FRONT_AND_BACK, GL11.GL_FILL);
            GL15.glBindBuffer(GL21.GL_PIXEL_UNPACK_BUFFER, 0);
            if (samplers) {
                for (int i = 0; i < 4; i++) {
                    GL33.glBindSampler(i, 0);
                }
            }
            return state;
        }

        void restore() {
            GL15.glBindBuffer(GL21.GL_PIXEL_UNPACK_BUFFER, unpack);
            if ((GL11.glGetInteger(0x9126) & 1) != 0 || polygon[0] == polygon[1])
                GL11.glPolygonMode(GL11.GL_FRONT_AND_BACK, polygon[0]); // GL_CONTEXT_CORE_PROFILE_BIT
            else { GL11.glPolygonMode(GL11.GL_FRONT, polygon[0]); GL11.glPolygonMode(GL11.GL_BACK, polygon[1]); }
            GL30.glColorMaski(0, mask[0] != 0, mask[1] != 0, mask[2] != 0, mask[3] != 0);
            cap(GL11.GL_DITHER, dither);
            cap(GL30.GL_RASTERIZER_DISCARD, discard);
            cap(GL30.GL_FRAMEBUFFER_SRGB, srgb);
            cap(GL11.GL_STENCIL_TEST, stencil);
            cap(GL11.GL_SCISSOR_TEST, scissor);
            cap(GL11.GL_CULL_FACE, cull);
            cap(GL11.GL_DEPTH_TEST, depth);
            if (blend) GL30.glEnablei(GL11.GL_BLEND, 0); else GL30.glDisablei(GL11.GL_BLEND, 0);
            for (int i = 0; i < 4; i++) {
                GL13.glActiveTexture(GL13.GL_TEXTURE0 + i);
                if (samplers) {
                    GL33.glBindSampler(i, sampler[i]);
                }
                GL11.glBindTexture(GL11.GL_TEXTURE_2D, bound[i]);
            }
            GL13.glActiveTexture(active);
            GL30.glBindVertexArray(vao);
            GL20.glUseProgram(program);
            GL11.glViewport(viewport[0], viewport[1], viewport[2], viewport[3]);
            GL30.glBindFramebuffer(GL30.GL_READ_FRAMEBUFFER, 0);
            GL11.glReadBuffer(defaultReadBuffer);
            GL30.glBindFramebuffer(GL30.GL_READ_FRAMEBUFFER, readFbo);
            GL30.glBindFramebuffer(GL30.GL_DRAW_FRAMEBUFFER, 0);
            GL11.glDrawBuffer(defaultDrawBuffer);
            GL30.glBindFramebuffer(GL30.GL_DRAW_FRAMEBUFFER, drawFbo);
        }

        private static void cap(int name, boolean enabled) {
            if (enabled) {
                GL11.glEnable(name);
            } else {
                GL11.glDisable(name);
            }
        }
    }
}
