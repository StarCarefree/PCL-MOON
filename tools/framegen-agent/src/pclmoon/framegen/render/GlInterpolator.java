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
import org.lwjgl.opengl.GL32;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.IntBuffer;

/**
 * Independent coarse-to-fine block-matching interpolator.
 *
 * Captures the default framebuffer back buffer and composites a frame at a
 * caller-supplied t in (0,1). Motion is a bounded image-space pyramid: a
 * cheap wide search at the coarsest level, then a 3x3 refinement up the
 * pyramid. Low-confidence and cut pixels fall back to the current real frame.
 * Requires an OpenGL 3.2 context current on the calling thread. GPU timer
 * queries run only on GL 3.3 and are never stalled on the normal path.
 */
public final class GlInterpolator {
    /** Historical default. The live budget is the value passed to {@link #configure}. */
    public static final float BUDGET_MS = 8.0f;
    private static final int FLOW_MIN = 80;
    private static final int FLOW_MAX = 320;
    private static final int GL_TEXTURE_COMPARE_MODE = 0x884C;

    private static final String VERT =
            "#version 150\n" +
            "out vec2 vUv;\n" +
            "void main() {\n" +
            "    vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));\n" +
            "    vUv = p;\n" +
            "    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);\n" +
            "}\n";

    /** 4x4 box over one destination texel. uSrcTexel is 1/srcSize; uScale is src/dst. */
    private static final String DOWN_FRAG =
            "#version 150\n" +
            "uniform sampler2D uSrc;\n" +
            "uniform vec2 uSrcTexel;\n" +
            "uniform vec2 uScale;\n" +
            "in vec2 vUv;\n" +
            "out vec4 fragColor;\n" +
            "void main() {\n" +
            "    vec2 footprint = uSrcTexel * uScale;\n" +
            "    vec3 acc = vec3(0.0);\n" +
            "    for (int y = 0; y < 4; y++) {\n" +
            "        for (int x = 0; x < 4; x++) {\n" +
            "            vec2 o = (vec2(float(x), float(y)) - 1.5) * footprint / 3.0;\n" +
            "            acc += texture(uSrc, clamp(vUv + o, vec2(0.0), vec2(1.0))).rgb;\n" +
            "        }\n" +
            "    }\n" +
            "    fragColor = vec4(acc * 0.0625, 1.0);\n" +
            "}\n";

    /** Coarse-to-fine UV flow; parent flow is refined at each finer image level. */
    private static final String FLOW_FRAG =
            "#version 150\n" +
            "uniform sampler2D uSrc, uDst, uParent;\n" +
            "uniform vec2 uStep; uniform float uExtent; uniform int uCoarse,uSmooth;\n" +
            "in vec2 vUv; out vec4 fragColor;\n" +
            "float costAt(vec2 d) {\n" +
            "  vec2 q=vUv+d; if(any(lessThan(q,vec2(0))) || any(greaterThan(q,vec2(1)))) return 10.0;\n" +
            "  float acc=0.0;\n" +
            "  for(int y=-1;y<=1;y++) for(int x=-1;x<=1;x++) {\n" +
            "    vec2 o=vec2(x,y)*uStep;\n" +
            "    vec3 a=textureLod(uSrc,clamp(vUv+o,vec2(0),vec2(1)),0.0).rgb;\n" +
            "    vec3 b=textureLod(uDst,clamp(q+o,vec2(0),vec2(1)),0.0).rgb;\n" +
            "    acc+=dot(abs(a-b),vec3(0.333333));\n" +
            "  } return acc/9.0;\n" +
            "}\n" +
            "void main() {\n" +
            "  vec2 base=vec2(0); if(uCoarse==0) base=texture(uParent,vUv).xy;\n" +
            "  vec2 best=base; float bestCost=costAt(best);\n" +
            "  for(int y=-8;y<=8;y++) for(int x=-8;x<=8;x++) {\n" +
            "    if(abs(float(x))>uExtent || abs(float(y))>uExtent) continue;\n" +
            "    vec2 d=base+vec2(x,y)*uStep; float c=costAt(d);\n" +
            "    if(c+length(vec2(x,y))*0.0001<bestCost) { best=d; bestCost=c; }\n" +
            "  }\n" +
            "  if(uSmooth!=0) for(int y=-4;y<=4;y++) for(int x=-4;x<=4;x++) {\n" +
            "    vec2 d=vec2(x,y)*uStep*8.0; float c=costAt(d);\n" +
            "    if(c<bestCost) { best=d; bestCost=c; }\n" +
            "  } fragColor=vec4(best,bestCost,1);\n" +
            "}\n";

    private static final String COMPOSE_FRAG =
            "#version 150\n" +
            "uniform sampler2D uPrev,uCur,uFwd,uBwd; uniform float uAlpha,uReject;\n" +
            "uniform int uRefine;\n" +
            "in vec2 vUv; out vec4 fragColor;\n" +
            "void main() {\n" +
            "  float t=clamp(uAlpha,0.0,1.0);\n" +
            "  if(t<=0.0) { fragColor=texture(uPrev,vUv); return; }\n" +
            "  if(t>=1.0) { fragColor=texture(uCur,vUv); return; }\n" +
            "  vec3 current=texture(uCur,vUv).rgb; vec3 stationary=abs(current-texture(uPrev,vUv).rgb);\n" +
            "  if(max(max(stationary.r,stationary.g),stationary.b)<0.001) { fragColor=vec4(current,1); return; }\n" +
            "  vec2 pu=vUv,cu=vUv;\n" +
            "  for(int i=0;i<3;i++) { if(i>=uRefine) break; pu=vUv-texture(uFwd,pu).xy*t; cu=vUv-texture(uBwd,cu).xy*(1.0-t); }\n" +
            "  vec4 f=texture(uFwd,pu),b=texture(uBwd,cu);\n" +
            "  vec3 a=texture(uPrev,pu).rgb,c=texture(uCur,cu).rgb,cur=current;\n" +
            "  vec3 diff=abs(a-c); float photo=max(max(diff.r,diff.g),diff.b);\n" +
            "  vec2 px=1.0/vec2(textureSize(uFwd,0));\n" +
            "  bool inside=all(greaterThan(pu,vec2(0))) && all(lessThan(pu,vec2(1)))\n" +
            "    && all(greaterThan(cu,vec2(0))) && all(lessThan(cu,vec2(1)));\n" +
            "  bool reject=!inside || photo>min(uReject,0.12) || length(f.xy+b.xy)>max(0.003,length(px))\n" +
            "    || max(f.z,b.z)>min(uReject,0.12);\n" +
            "  fragColor=vec4(reject?cur:c,1);\n" +
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
    private int levels;
    private int framesCaptured;
    private int writeSlot;
    private long completionFence;
    private boolean gl33;
    private boolean flowPrepared;

    private int cfgFlow = 160;
    private int cfgQuality = 1;
    private float cfgReject = 0.22f;
    private float cfgBudgetMs = BUDGET_MS;
    private int appliedFlow = -1;
    private int appliedQuality = -1;
    private boolean configDirty;

    private int vao;
    private int flowProgram;
    private int downProgram;
    private int composeProgram;
    private int blitProgram;
    private int flowFbo;
    private final int[] frameTex = new int[2];
    private final int[] flowTex = new int[2];
    /** [level][0=fwd pyramid color, 1=bwd]. Level 0 is full flow resolution. */
    private final int[][] pyrColor = new int[4][2];
    private final int[][] pyrFlow = new int[4][2];
    private final int[] pyrW = new int[4];
    private final int[] pyrH = new int[4];
    private final int[] queries = new int[2];
    private int querySlot;
    private boolean queryBusy;
    private boolean queryRunning;
    private boolean overBudget;
    private double lastGpuMillis;
    private int locSrc;
    private int locDst;
    private int locParent;
    private int locTexel;
    private int locRadius;
    private int locCoarse;
    private int locSmooth;
    private int locDownSrc;
    private int locDownTexel;
    private int locDownScale;
    private int locPrev;
    private int locCur;
    private int locFwd;
    private int locBwd;
    private int locAlpha;
    private int locReject;
    private int locRefine;
    private int locBlit;

    private final IntBuffer widthBuf = ByteBuffer.allocateDirect(4).order(ByteOrder.nativeOrder()).asIntBuffer();
    private final IntBuffer heightBuf = ByteBuffer.allocateDirect(4).order(ByteOrder.nativeOrder()).asIntBuffer();

    public GlInterpolator(long window) {
        if (window == 0L) {
            throw new IllegalArgumentException("window");
        }
        this.window = window;
    }

    /**
     * @param flowWidth 80..320, finest motion-field width
     * @param quality 0 fast, 1 balanced, 2 quality
     * @param rejectionThreshold photometric reject, typically 0.12..0.40
     * @param gpuBudgetMs async timer budget; never waited on
     */
    public void configure(int flowWidth, int quality, float rejectionThreshold, float gpuBudgetMs) {
        int widthClamped = Math.max(FLOW_MIN, Math.min(FLOW_MAX, flowWidth));
        int qualityClamped = quality < 0 ? 0 : Math.min(2, quality);
        float reject = rejectionThreshold < 0.05f ? 0.05f : Math.min(0.80f, rejectionThreshold);
        float budget = gpuBudgetMs < 0.5f ? 0.5f : Math.min(33.0f, gpuBudgetMs);
        if (widthClamped != cfgFlow || qualityClamped != cfgQuality) {
            configDirty = true;
        }
        cfgFlow = widthClamped;
        cfgQuality = qualityClamped;
        cfgReject = reject;
        cfgBudgetMs = budget;
    }

    public boolean ready() {
        return !closed && !failed && supported && framesCaptured >= 2 && vao != 0;
    }

    /** Last completed GPU timer sample in milliseconds. 0 until a query resolves. Non-blocking. */
    public double lastGpuMillis() {
        return lastGpuMillis;
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
            if (w != width || h != height || vao == 0 || configDirty) {
                rebuild(w, h);
            }
            GL30.glBindFramebuffer(GL30.GL_READ_FRAMEBUFFER, 0);
            GL11.glReadBuffer(GL11.GL_BACK);
            GL13.glActiveTexture(GL13.GL_TEXTURE0);
            GL11.glBindTexture(GL11.GL_TEXTURE_2D, frameTex[writeSlot]);
            GL15.glBindBuffer(GL21.GL_PIXEL_UNPACK_BUFFER, 0);
            GL11.glCopyTexSubImage2D(GL11.GL_TEXTURE_2D, 0, 0, 0, 0, 0, width, height);
            writeSlot ^= 1;
            flowPrepared = false;
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
        drawInterpolated(0.5f);
    }

    public void drawInterpolated(float alpha) {
        if (closed || failed || framesCaptured < 2 || vao == 0) {
            return;
        }
        float t = alpha < 0f ? 0f : Math.min(1f, alpha);
        GlState state = GlState.capture(gl33);
        try {
            int prev = writeSlot;
            int cur = writeSlot ^ 1;
            if (!flowPrepared) {
                beginBudgetQuery();
                buildPyramids(prev, cur);
                estimateFlow(0, prev, cur);
                estimateFlow(1, cur, prev);
                flowPrepared = true;
            }
            GL30.glBindFramebuffer(GL30.GL_DRAW_FRAMEBUFFER, 0);
            GL11.glDrawBuffer(GL11.GL_BACK);
            GL11.glViewport(0, 0, width, height);
            GL20.glUseProgram(composeProgram);
            bindUnit(0, frameTex[prev], locPrev);
            bindUnit(1, frameTex[cur], locCur);
            bindUnit(2, flowTex[0], locFwd);
            bindUnit(3, flowTex[1], locBwd);
            if (locAlpha >= 0) GL20.glUniform1f(locAlpha, t);
            if (locReject >= 0) GL20.glUniform1f(locReject, cfgReject);
            if (locRefine >= 0) GL20.glUniform1i(locRefine, cfgQuality == 2 ? 3 : 1);
            GL30.glBindVertexArray(vao);
            GL11.glDrawArrays(GL11.GL_TRIANGLES, 0, 3);
            if (completionFence != 0) GL32.glDeleteSync(completionFence);
            completionFence = GL32.glFenceSync(GL32.GL_SYNC_GPU_COMMANDS_COMPLETE, 0);
            if (queryRunning) endBudgetQuery();
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
        flowPrepared = false;
    }

    /** Zero-timeout GPU backpressure; never waits for a busy GPU on the game thread. */
    public boolean hasPendingWork() {
        if (completionFence == 0 || closed || failed) return false;
        int state = GL32.glClientWaitSync(completionFence, 0, 0);
        if (state == GL32.GL_WAIT_FAILED) throw new IllegalStateException("frame-generation GPU fence failed");
        if (state == GL32.GL_TIMEOUT_EXPIRED) return true;
        GL32.glDeleteSync(completionFence); completionFence = 0;
        return false;
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
        flowW = Math.min(cfgFlow, w);
        flowH = Math.max(1, (int) Math.round(h * (flowW / (double) w)));
        levels = cfgQuality == 0 ? 2 : 3; // Quality widens coarse search instead of destroying detail in a fourth image level.
        int coarseW = flowW;
        for (int i = 1; i < levels; i++) coarseW = Math.max(1, coarseW / 2);
        while (levels > 2 && coarseW < 20) {
            levels--;
            coarseW = flowW;
            for (int i = 1; i < levels; i++) coarseW = Math.max(1, coarseW / 2);
        }
        appliedFlow = cfgFlow;
        appliedQuality = cfgQuality;
        configDirty = false;
        try {
            vao = GL30.glGenVertexArrays();
            if (vao == 0) {
                throw new IllegalStateException("vao");
            }
            GL30.glBindVertexArray(vao);
            flowProgram = link(VERT, FLOW_FRAG);
            downProgram = link(VERT, DOWN_FRAG);
            composeProgram = link(VERT, COMPOSE_FRAG);
            blitProgram = link(VERT, BLIT_FRAG);
            locSrc = GL20.glGetUniformLocation(flowProgram, "uSrc");
            locDst = GL20.glGetUniformLocation(flowProgram, "uDst");
            locParent = GL20.glGetUniformLocation(flowProgram, "uParent");
            locTexel = GL20.glGetUniformLocation(flowProgram, "uStep");
            locRadius = GL20.glGetUniformLocation(flowProgram, "uExtent");
            locCoarse = GL20.glGetUniformLocation(flowProgram, "uCoarse");
            locSmooth = GL20.glGetUniformLocation(flowProgram, "uSmooth");
            locDownSrc = GL20.glGetUniformLocation(downProgram, "uSrc");
            locDownTexel = GL20.glGetUniformLocation(downProgram, "uSrcTexel");
            locDownScale = GL20.glGetUniformLocation(downProgram, "uScale");
            locPrev = GL20.glGetUniformLocation(composeProgram, "uPrev");
            locCur = GL20.glGetUniformLocation(composeProgram, "uCur");
            locFwd = GL20.glGetUniformLocation(composeProgram, "uFwd");
            locBwd = GL20.glGetUniformLocation(composeProgram, "uBwd");
            locAlpha = GL20.glGetUniformLocation(composeProgram, "uAlpha");
            locReject = GL20.glGetUniformLocation(composeProgram, "uReject");
            locRefine = GL20.glGetUniformLocation(composeProgram, "uRefine");
            locBlit = GL20.glGetUniformLocation(blitProgram, "uTex");
            frameTex[0] = alloc(width, height, GL11.GL_RGBA8, GL11.GL_UNSIGNED_BYTE, true);
            frameTex[1] = alloc(width, height, GL11.GL_RGBA8, GL11.GL_UNSIGNED_BYTE, true);
            flowTex[0] = alloc(flowW, flowH, GL30.GL_RGBA16F, GL11.GL_FLOAT, false);
            flowTex[1] = alloc(flowW, flowH, GL30.GL_RGBA16F, GL11.GL_FLOAT, false);
            int pw = flowW;
            int ph = flowH;
            for (int level = 0; level < levels; level++) {
                pyrW[level] = pw;
                pyrH[level] = ph;
                for (int dir = 0; dir < 2; dir++) {
                    pyrColor[level][dir] = alloc(pw, ph, GL11.GL_RGBA8, GL11.GL_UNSIGNED_BYTE, true);
                    pyrFlow[level][dir] = alloc(pw, ph, GL30.GL_RGBA16F, GL11.GL_FLOAT, false);
                }
                pw = Math.max(1, pw / 2);
                ph = Math.max(1, ph / 2);
            }
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

    private void buildPyramids(int prev, int cur) {
        int[] src = {frameTex[prev], frameTex[cur]};
        int[] srcW = {width, width};
        int[] srcH = {height, height};
        for (int level = 0; level < levels; level++) {
            for (int dir = 0; dir < 2; dir++) {
                drawTo(pyrColor[level][dir], pyrW[level], pyrH[level]);
                GL20.glUseProgram(downProgram);
                bindUnit(0, src[dir], locDownSrc);
                if (locDownTexel >= 0) GL20.glUniform2f(locDownTexel, 1.0f / srcW[dir], 1.0f / srcH[dir]);
                if (locDownScale >= 0) GL20.glUniform2f(locDownScale, (float) srcW[dir] / pyrW[level], (float) srcH[dir] / pyrH[level]);
                GL30.glBindVertexArray(vao);
                GL11.glDrawArrays(GL11.GL_TRIANGLES, 0, 3);
                src[dir] = pyrColor[level][dir];
                srcW[dir] = pyrW[level];
                srcH[dir] = pyrH[level];
            }
        }
    }

    /** Estimate a bounded motion field once per true-frame pair, reusable for every alpha. */
    private void estimateFlow(int dir, int srcFrame, int dstFrame) {
        for (int level = levels - 1; level >= 0; level--) {
            int target = level == 0 ? flowTex[dir] : pyrFlow[level][dir];
            drawTo(target, pyrW[level], pyrH[level]);
            GL20.glUseProgram(flowProgram);
            bindUnit(0, pyrColor[level][dir], locSrc);
            bindUnit(1, pyrColor[level][dir ^ 1], locDst);
            bindUnit(2, level + 1 < levels ? pyrFlow[level + 1][dir] : 0, locParent);
            GL20.glUniform2f(locTexel, 1.0f / pyrW[level], 1.0f / pyrH[level]);
            GL20.glUniform1f(locRadius, level == levels - 1 ? (cfgQuality == 2 ? 8 : 4) : (cfgQuality == 2 ? 3 : 2));
            GL20.glUniform1i(locCoarse, level == levels - 1 ? 1 : 0);
            GL20.glUniform1i(locSmooth, cfgQuality == 2 && level == 0 ? 1 : 0);
            GL30.glBindVertexArray(vao);
            GL11.glDrawArrays(GL11.GL_TRIANGLES, 0, 3);
        }
    }


    private void drawTo(int target, int w, int h) {
        GL30.glBindFramebuffer(GL30.GL_DRAW_FRAMEBUFFER, flowFbo);
        GL30.glFramebufferTexture2D(GL30.GL_DRAW_FRAMEBUFFER, GL30.GL_COLOR_ATTACHMENT0,
                GL11.GL_TEXTURE_2D, target, 0);
        if (GL30.glCheckFramebufferStatus(GL30.GL_DRAW_FRAMEBUFFER) != GL30.GL_FRAMEBUFFER_COMPLETE)
            throw new IllegalStateException("motion framebuffer incomplete");
        GL11.glDrawBuffer(GL30.GL_COLOR_ATTACHMENT0);
        GL11.glViewport(0, 0, w, h);
        GL11.glDisable(GL11.GL_SCISSOR_TEST);
    }

    private void bindUnit(int unit, int texture, int location) {
        GL13.glActiveTexture(GL13.GL_TEXTURE0 + unit);
        GL11.glBindTexture(GL11.GL_TEXTURE_2D, texture);
        if (location >= 0) {
            GL20.glUniform1i(location, unit);
        }
    }

    private static int alloc(int w, int h, int internalFormat, int type, boolean mips) {
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
        GL11.glTexParameteri(GL11.GL_TEXTURE_2D, GL12.GL_TEXTURE_BASE_LEVEL, 0);
        GL11.glTexParameteri(GL11.GL_TEXTURE_2D, GL12.GL_TEXTURE_MAX_LEVEL, 0);
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
        lastGpuMillis = nanos / 1_000_000.0;
        overBudget = nanos > (long) (cfgBudgetMs * 1_000_000.0f);
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
        if (completionFence != 0) { GL32.glDeleteSync(completionFence); completionFence = 0; }
        queryBusy = queryRunning = false;
        if (vao != 0) {
            GL30.glDeleteVertexArrays(vao);
            vao = 0;
        }
        deleteProgram(flowProgram);
        flowProgram = 0;
        deleteProgram(downProgram);
        downProgram = 0;
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
        for (int level = 0; level < pyrColor.length; level++) {
            for (int dir = 0; dir < 2; dir++) {
                if (pyrColor[level][dir] != 0) {
                    GL11.glDeleteTextures(pyrColor[level][dir]);
                    pyrColor[level][dir] = 0;
                }
                if (pyrFlow[level][dir] != 0) {
                    GL11.glDeleteTextures(pyrFlow[level][dir]);
                    pyrFlow[level][dir] = 0;
                }
            }
            pyrW[level] = 0;
            pyrH[level] = 0;
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
        levels = 0;
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
