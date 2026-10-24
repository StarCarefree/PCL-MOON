package pclmoon.framegen.render;

import pclmoon.framegen.test.Probe;

/** Loaded through exactly the same isolated renderer-loader path as the real GL implementation. */
public final class GlInterpolator {
    private int frames, width = 160, quality = 1;
    public GlInterpolator(long window) { }
    public boolean capture() { Probe.captures++; return ++frames > 1; }
    public void configure(int width, int quality, float threshold, float budget) {
        if (this.width != width || this.quality != quality) frames = 0;
        this.width = width; this.quality = quality;
    }
    public void drawIntermediate() { drawInterpolated(0.5f); }
    public void drawInterpolated(float alpha) {
        Probe.alphas[Probe.intermediates % Probe.alphas.length] = alpha;
        Probe.intermediates++;
        if (Probe.failIntermediate) throw new IllegalStateException("synthetic GL failure");
    }
    public boolean ready() { return frames >= 2; }
    public boolean hasPendingWork() { return Probe.gpuPending; }
    public double lastGpuMillis() { return 1; }
    public void drawCurrent() { Probe.currents++; }
    public void resetHistory() { Probe.resets++; frames = 0; }
    public boolean isOverBudget() { boolean result = Probe.budget; Probe.budget = false; return result; }
    public void close() { Probe.closes++; }
}
