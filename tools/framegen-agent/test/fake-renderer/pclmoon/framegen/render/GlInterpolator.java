package pclmoon.framegen.render;

import pclmoon.framegen.test.Probe;

/** Loaded through exactly the same isolated renderer-loader path as the real GL implementation. */
public final class GlInterpolator {
    private int frames;
    public GlInterpolator(long window) { }
    public boolean capture() { Probe.captures++; return ++frames > 1; }
    public void drawIntermediate() {
        Probe.intermediates++;
        if (Probe.failIntermediate) throw new IllegalStateException("synthetic GL failure");
    }
    public void drawCurrent() { Probe.currents++; }
    public void resetHistory() { Probe.resets++; frames = 0; }
    public boolean isOverBudget() { boolean result = Probe.budget; Probe.budget = false; return result; }
    public void close() { Probe.closes++; }
}
