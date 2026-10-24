package pclmoon.framegen.render;

import java.lang.reflect.Method;
import org.lwjgl.glfw.GLFW;
import org.lwjgl.opengl.*;

/** Test-only GPU/CPU timing; all glFinish calls stay in this benchmark, never in the game renderer. */
public final class RendererBenchmark {
    public static void main(String[] args) throws Exception {
        int width = Integer.getInteger("bench.w", 1920), height = Integer.getInteger("bench.h", 1080);
        int iterations = Integer.getInteger("bench.frames", 40), warmup = 10;
        int generated = Integer.getInteger("bench.generated", 1);
        if (!GLFW.glfwInit()) throw new AssertionError("GLFW unavailable");
        long window = 0; GlInterpolator renderer = null;
        try {
            GLFW.glfwWindowHint(GLFW.GLFW_VISIBLE, GLFW.GLFW_FALSE);
            GLFW.glfwWindowHint(GLFW.GLFW_CONTEXT_VERSION_MAJOR, 3);
            GLFW.glfwWindowHint(GLFW.GLFW_CONTEXT_VERSION_MINOR, 3);
            GLFW.glfwWindowHint(GLFW.GLFW_OPENGL_PROFILE, GLFW.GLFW_OPENGL_CORE_PROFILE);
            window = GLFW.glfwCreateWindow(width, height, "framegen-benchmark", 0, 0);
            if (window == 0) throw new AssertionError("benchmark window");
            GLFW.glfwMakeContextCurrent(window); GL.createCapabilities();
            int program = program(), vao = GL30.glGenVertexArrays();
            int location = GL20.glGetUniformLocation(program, "phase");
            renderer = new GlInterpolator(window);
            Method configure = optional("configure", int.class, int.class, float.class, float.class);
            Method interpolate = optional("drawInterpolated", float.class), timer = optional("lastGpuMillis");
            if (configure != null) configure.invoke(renderer, 160, Integer.getInteger("bench.quality", 1), 0.22f, 8f);
            double cpu = 0, completed = 0, gpu = 0; int samples = 0;
            for (int i = 0; i < warmup + iterations; i++) {
                GL30.glBindFramebuffer(GL30.GL_FRAMEBUFFER, 0); GL11.glViewport(0, 0, width, height);
                GL20.glUseProgram(program); GL30.glBindVertexArray(vao);
                GL20.glUniform1f(location, i * 0.03f); GL11.glDrawArrays(GL11.GL_TRIANGLES, 0, 3);
                GL11.glFinish(); // Exclude the synthetic input draw from frame-generation cost.
                long begin = System.nanoTime();
                boolean ready = renderer.capture();
                if (ready) for (int j = 0; j < generated; j++) {
                    if (interpolate != null) interpolate.invoke(renderer, (j + 1) / (float) (generated + 1));
                    else renderer.drawIntermediate();
                }
                long submitted = System.nanoTime();
                GL11.glFinish(); // Test-only completion timing, not an async-query claim.
                long finished = System.nanoTime();
                if (i >= warmup && ready) {
                    cpu += (submitted - begin) / 1e6; completed += (finished - begin) / 1e6;
                    if (timer != null) gpu += ((Number) timer.invoke(renderer)).doubleValue();
                    samples++;
                }
            }
            if (samples == 0) throw new AssertionError("no measured frames");
            System.out.println("BENCH " + GL11.glGetString(GL11.GL_RENDERER) + " " + width + "x" + height
                + " generated=" + generated + " n=" + samples + " cpuSubmitMs=" + cpu / samples
                + " gpuCompletedWallMs=" + completed / samples + " asyncGpuMs=" + (timer == null ? "unavailable" : gpu / samples));
            GL20.glDeleteProgram(program); GL30.glDeleteVertexArrays(vao);
        } finally {
            if (renderer != null) renderer.close();
            if (window != 0) GLFW.glfwDestroyWindow(window);
            GLFW.glfwTerminate();
        }
    }
    private static Method optional(String name, Class<?>... types) {
        try { return GlInterpolator.class.getMethod(name, types); } catch (NoSuchMethodException absent) { return null; }
    }
    private static int program() {
        int vertex = shader(GL20.GL_VERTEX_SHADER, "#version 150\nout vec2 uv;void main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);uv=p;gl_Position=vec4(p*2-1,0,1);}");
        int fragment = shader(GL20.GL_FRAGMENT_SHADER, "#version 150\nin vec2 uv;uniform float phase;out vec4 color;void main(){vec2 p=uv-vec2(phase,0);color=vec4(.5+.22*sin(30*p.x+11*p.y),.5+.22*sin(19*p.x-17*p.y),.5+.22*cos(23*p.x+7*p.y),1);}");
        int p = GL20.glCreateProgram(); GL20.glAttachShader(p, vertex); GL20.glAttachShader(p, fragment); GL20.glLinkProgram(p);
        if (GL20.glGetProgrami(p, GL20.GL_LINK_STATUS) == 0) throw new AssertionError(GL20.glGetProgramInfoLog(p));
        GL20.glDeleteShader(vertex); GL20.glDeleteShader(fragment); return p;
    }
    private static int shader(int type, String source) {
        int shader = GL20.glCreateShader(type); GL20.glShaderSource(shader, source); GL20.glCompileShader(shader);
        if (GL20.glGetShaderi(shader, GL20.GL_COMPILE_STATUS) == 0) throw new AssertionError(GL20.glGetShaderInfoLog(shader));
        return shader;
    }
}
