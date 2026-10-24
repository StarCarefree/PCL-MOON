package pclmoon.framegen.test;

import java.awt.Component;
import java.awt.Rectangle;
import java.awt.Window;
import java.lang.reflect.Method;
import javax.swing.AbstractButton;
import javax.swing.JComboBox;
import javax.swing.JFrame;
import javax.swing.JSpinner;
import javax.swing.SwingUtilities;
 import pclmoon.framegen.bridge.FrameMetrics;
 import pclmoon.framegen.bridge.FramePacerProfiles;
 import pclmoon.framegen.bridge.FrameSettings;
 import pclmoon.framegen.bridge.GameSettingsOverlay;
 import pclmoon.framegen.bridge.PacingSettings;

/** Real (non-headless) Swing smoke for the game settings chrome, run with test.py --overlay. */
public final class OverlayHarness {
    private static void check(boolean ok, String message) {
        if (!ok) throw new AssertionError(message);
    }

    public static void main(String[] args) throws Exception {
        if (Boolean.getBoolean("java.awt.headless")) throw new IllegalStateException("OverlayHarness requires a display");
        check("false".equals(System.getProperty("pclmoon.framegen.overlay", "false")), "overlay is only constructed explicitly");

        final JFrame game = new JFrame("overlay-harness-game");
        game.setName("pcl-framegen-fake-game");
        SwingUtilities.invokeAndWait(new Runnable() {
            public void run() {
                game.setDefaultCloseOperation(JFrame.DO_NOTHING_ON_CLOSE);
                game.setSize(640, 420);
                game.setLocation(80, 60);
                game.setVisible(true);
            }
        });
        GameSettingsOverlay overlay = new GameSettingsOverlay();
        FrameSettings.setOutputFps(100); // Not a preset: opening the panel must not round it to 90.
        FrameSettings.setShowFps(false);
        FrameMetrics.reset();
        FrameMetrics.skip("gpu-budget"); // No presentations: every FPS readout must remain zero regardless of target/multiplier.
        try {
            Rectangle bounds = game.getBounds();
            overlay.update(bounds.x, bounds.y, bounds.width, bounds.height, true);
            overlay.update(bounds.x + 3, bounds.y + 2, bounds.width, bounds.height, true);
            Window gear = waitWindow("pcl-framegen-gear-window", true);
            check(!gear.isFocusableWindow(), "gear must not take game focus");
            AbstractButton gearButton = button(gear, "pcl-framegen-gear");
            AbstractButton fpsToggle = button(gear, "pcl-framegen-fps-toggle");
            check(gearButton != null && fpsToggle != null && gear.isVisible(), "gear and FPS button visible while game is foreground");
            check(!fpsToggle.isFocusable(), "FPS button must not take game focus");
            click(gearButton);
            Window panel = waitWindow("pcl-framegen-panel", true);
            check(panel.isVisible() && panel.isFocusableWindow(), "utility panel opens beside gear");
            check(panel.getX() >= game.getX() - 8 && panel.getY() >= game.getY() - 8
                && panel.getX() + panel.getWidth() <= game.getX() + game.getWidth() + 8
                && panel.getY() + panel.getHeight() <= game.getY() + game.getHeight() + 8,
                "panel remains inside the 640x420 game bounds");
             check(find(panel, "pcl-framegen-multiplier") != null && find(panel, "pcl-framegen-quality") != null
                 && find(panel, "pcl-framegen-flow") != null && find(panel, "pcl-framegen-gpu-budget") != null
                 && find(panel, "pcl-framegen-stall") != null && find(panel, "pcl-framegen-show-fps") != null
                 && find(panel, "pcl-framegen-fps-limit") != null && find(panel, "pcl-framegen-wait-mode") != null
                 && find(panel, "pcl-framegen-telemetry") != null && find(panel, "pcl-framegen-save-instance") != null
                 && find(panel, "pcl-framegen-reset-instance") != null && find(panel, "pcl-framegen-profile-status") != null
                 && find(panel, "pcl-framegen-scroll") != null, "expanded controls are present");
            String body = text(panel, "pcl-framegen-body");
            check(body.contains("无法消除") && text(panel, "pcl-framegen-status").contains("本次")
                && body.contains("options.txt") && body.contains("未接入") && body.contains("Esc"),
                "panel explains latency/HUD/GC, session-only scope, native state, and Esc");
            check(FrameSettings.outputFps() == 100, "opening panel must preserve a custom FPS value");
            final Window previewPanel = panel;
            final String previewPath = System.getProperty("pclmoon.framegen.test.preview");
            if (previewPath != null) SwingUtilities.invokeAndWait(new Runnable() {
                public void run() {
                    java.awt.image.BufferedImage image = new java.awt.image.BufferedImage(previewPanel.getWidth(), previewPanel.getHeight(), java.awt.image.BufferedImage.TYPE_INT_ARGB);
                    java.awt.Graphics2D graphics = image.createGraphics();
                    try {
                        previewPanel.printAll(graphics);
                        javax.imageio.ImageIO.write(image, "png", new java.io.File(previewPath));
                        javax.swing.JScrollPane scroll = (javax.swing.JScrollPane) find(previewPanel, "pcl-framegen-scroll");
                        scroll.getVerticalScrollBar().setValue(scroll.getVerticalScrollBar().getMaximum());
                        Component bottom = find(previewPanel, "pcl-framegen-reset-instance");
                        Rectangle visible = SwingUtilities.convertRectangle(bottom.getParent(), bottom.getBounds(), scroll.getViewport());
                        check(visible.y >= 0 && visible.y + visible.height <= scroll.getViewport().getHeight(),
                            "instance controls scroll fully into view");
                        previewPanel.printAll(graphics);
                        javax.imageio.ImageIO.write(image, "png", new java.io.File(previewPath + ".bottom.png"));
                        scroll.getVerticalScrollBar().setValue(0);
                    } catch (java.io.IOException failure) { throw new AssertionError(failure); }
                    finally { graphics.dispose(); }
                }
            });
            final Rectangle gearBeforeMove = gear.getBounds();
            SwingUtilities.invokeAndWait(new Runnable() {
                public void run() { game.setLocation(game.getX() + 100, game.getY() + 70); }
            });
            bounds = game.getBounds();
            overlay.update(bounds.x, bounds.y, bounds.width, bounds.height, true);
            SwingUtilities.invokeAndWait(new Runnable() { public void run() { } });
            check(gear.getX() != gearBeforeMove.x && gear.getY() != gearBeforeMove.y, "gear follows the moved game");

            AbstractButton generation = button(panel, "pcl-framegen-generation");
            AbstractButton diagnostics = button(panel, "pcl-framegen-diagnostics");
            AbstractButton close = button(panel, "pcl-framegen-close");
            check(generation != null && diagnostics != null && close != null && "X".equals(close.getText()), "named controls");
            Rectangle closeBounds = SwingUtilities.convertRectangle(close.getParent(), close.getBounds(), panel);
            check(closeBounds.x >= 0 && closeBounds.y >= 0 && closeBounds.x + closeBounds.width <= panel.getWidth()
                && closeBounds.y + closeBounds.height <= panel.getHeight(), "close button is visible inside the fixed header");
            boolean generationWas = generation.isSelected();
            click(generation);
            click(diagnostics);
            JComboBox<?> fps = combo(panel, "pcl-framegen-fps");
            SwingUtilities.invokeAndWait(select(fps, Integer.valueOf(144)));
            JComboBox<?> multiplier = combo(panel, "pcl-framegen-multiplier");
            SwingUtilities.invokeAndWait(select(multiplier, Integer.valueOf(3)));
            JComboBox<?> quality = combo(panel, "pcl-framegen-quality");
            SwingUtilities.invokeAndWait(select(quality, "画质"));
            JComboBox<?> flow = combo(panel, "pcl-framegen-flow");
            SwingUtilities.invokeAndWait(select(flow, Integer.valueOf(240)));
            JSpinner gpu = spinner(panel, "pcl-framegen-gpu-budget");
            JSpinner stall = spinner(panel, "pcl-framegen-stall");
             SwingUtilities.invokeAndWait(spin(gpu, Integer.valueOf(12)));
             SwingUtilities.invokeAndWait(spin(stall, Integer.valueOf(200)));
             JSpinner fpsLimit = spinner(panel, "pcl-framegen-fps-limit");
             SwingUtilities.invokeAndWait(spin(fpsLimit, Integer.valueOf(90)));
             JComboBox<?> waitMode = combo(panel, "pcl-framegen-wait-mode");
             SwingUtilities.invokeAndWait(select(waitMode, "精确"));
             AbstractButton telemetry = button(panel, "pcl-framegen-telemetry");
             boolean telemetryWas = telemetry.isSelected();
             click(telemetry);
             AbstractButton saveProfile = button(panel, "pcl-framegen-save-instance");
             AbstractButton resetProfile = button(panel, "pcl-framegen-reset-instance");
             check(saveProfile != null && resetProfile != null, "profile buttons exist");
             String profilePath = FramePacerProfiles.profilePath();
             boolean profileConfigured = profilePath != null && profilePath.length() > 0;
             check(saveProfile.isEnabled() == profileConfigured && resetProfile.isEnabled() == profileConfigured,
                 "profile buttons stay disabled until a path is configured");
             if (!profileConfigured) {
                 check(text(panel, "pcl-framegen-profile-status").contains("未配置"), "missing profile path is shown as unconfigured");
                 check(!saveProfile.isEnabled(), "save must not claim success without a path");
             }
             waitPacing(90, 2, !telemetryWas);
            AbstractButton adaptive = button(panel, "pcl-framegen-adaptive");
            AbstractButton protection = button(panel, "pcl-framegen-protection");
            AbstractButton nativeControl = button(panel, "pcl-framegen-native-control");
            boolean adaptiveWas = adaptive.isSelected();
            boolean protectionWas = protection.isSelected();
            boolean nativeWas = nativeControl.isSelected();
            click(adaptive);
            click(protection);
            click(nativeControl);
            waitSettings(!generationWas, true, 144, 3, 2, 240, 12, 200, !adaptiveWas, !protectionWas, !nativeWas);
            check(find("pcl-framegen-hud") == null || !find("pcl-framegen-hud").isVisible(),
                "HUD stays hidden until showFps is enabled");

            click(fpsToggle);
            Window hud = waitWindow("pcl-framegen-hud", true);
            check(!hud.isFocusableWindow(), "FPS HUD must not take game focus");
            check(hud.getX() >= game.getX() - 4 && hud.getY() >= game.getY() - 4, "HUD follows game bounds");
            String presented = text(hud, "pcl-framegen-hud-presented");
            String render = text(hud, "pcl-framegen-hud-render");
            String generated = text(hud, "pcl-framegen-hud-generated");
            check(presented.contains("0") && render.contains("0") && generated.contains("0"),
                "HUD stays at zero without any submitted frames, regardless of target*multiplier");
            check(!presented.contains("288") && !presented.contains("432") && !render.contains("144"),
                "HUD must not echo the target FPS or target*multiplier");
            check(text(hud, "pcl-framegen-hud-caption").contains("呈现提交"), "HUD labels presented submits");
            check(text(hud, "pcl-framegen-hud-reason").contains("gpu-budget"), "HUD shows the skip reason");
            check(text(hud, "pcl-framegen-hud-native").contains("未接入"), "HUD does not pretend the native hook is attached");
            check(FrameSettings.showFps(), "FPS button enables showFps");
            click(fpsToggle);
            waitHidden(hud);
            check(!FrameSettings.showFps(), "FPS button toggles showFps off");

            overlay.update(bounds.x, bounds.y, 0, 0, false);
            waitHidden(gear);
            waitHidden(panel);
            overlay.update(bounds.x, bounds.y, bounds.width, bounds.height, true);
            waitWindow("pcl-framegen-gear-window", true);
            click(gearButton);
            waitWindow("pcl-framegen-panel", true);

            click(gearButton);
            waitHidden(panel);
            click(gearButton);
            waitWindow("pcl-framegen-panel", true);
            click(close);
            waitHidden(panel);

            final JFrame other = new JFrame("overlay-harness-other");
            SwingUtilities.invokeAndWait(new Runnable() {
                public void run() {
                    other.setSize(200, 120);
                    other.setLocation(900, 80);
                    other.setVisible(true);
                    other.toFront();
                    other.requestFocus();
                }
            });
            try {
                bounds = game.getBounds();
                FrameSettings.setShowFps(true);
                overlay.update(bounds.x, bounds.y, bounds.width, bounds.height, false);
                waitHidden(gear);
                Window hiddenHud = find("pcl-framegen-hud");
                check(hiddenHud == null || !hiddenHud.isVisible(), "HUD hides when the game is unfocused");
                check(FrameSettings.outputFps() == 144, "settings survive hide");
                check(FrameSettings.multiplier() == 3 && FrameSettings.quality() == 2, "multiplier and quality survive hide");
            } finally {
                dispose(other);
            }

            overlay.close();
            overlay.update(bounds.x, bounds.y, bounds.width, bounds.height, true);
            Thread.sleep(300);
            check(find("pcl-framegen-gear-window") == null && find("pcl-framegen-panel") == null
                && find("pcl-framegen-hud") == null, "close cancels a queued show and releases the HUD");
        } finally {
            overlay.close();
            dispose(game);
        }
        System.out.println("PASS overlay");
    }


    private static void waitSettings(final boolean generation, final boolean diagnostics, final int fps,
            final int multiplier, final int quality, final int flow, final int gpu, final int stall,
            final boolean adaptive, final boolean protection, final boolean nativeControl) throws Exception {
        long deadline = System.currentTimeMillis() + 2000;
        while (System.currentTimeMillis() < deadline) {
            if (FrameSettings.generation() == generation && FrameSettings.diagnostics() == diagnostics && FrameSettings.outputFps() == fps
                && FrameSettings.multiplier() == multiplier && FrameSettings.quality() == quality && FrameSettings.flowWidth() == flow
                && FrameSettings.gpuBudgetMs() == gpu && FrameSettings.stallThresholdMs() == stall
                && FrameSettings.adaptivePacing() == adaptive && FrameSettings.lowFpsProtection() == protection
                && FrameSettings.nativeFrameControl() == nativeControl) return;
            Thread.sleep(20);
        }
         throw new AssertionError("FrameSettings did not follow the panel");
     }

     private static void waitPacing(final int fpsLimit, final int waitMode, final boolean telemetry) throws Exception {
         long deadline = System.currentTimeMillis() + 2000;
         while (System.currentTimeMillis() < deadline) {
             if (PacingSettings.fpsLimit() == fpsLimit && PacingSettings.waitMode() == waitMode
                 && PacingSettings.telemetry() == telemetry) return;
             Thread.sleep(20);
         }
         throw new AssertionError("PacingSettings did not follow the panel");
     }

    private static Window waitWindow(String name, boolean visible) throws Exception {
        long deadline = System.currentTimeMillis() + 3000;
        while (System.currentTimeMillis() < deadline) {
            Window window = find(name);
            if (window != null && window.isVisible() == visible) return window;
            Thread.sleep(20);
        }
        throw new AssertionError("missing window " + name);
    }

    private static void waitHidden(final Window window) throws Exception {
        long deadline = System.currentTimeMillis() + 2000;
        while (System.currentTimeMillis() < deadline) {
            if (window == null || !window.isDisplayable() || !window.isVisible()) return;
            Thread.sleep(20);
        }
        throw new AssertionError("window stayed visible: " + window.getName());
    }

    private static Window find(String name) {
        Window[] windows = Window.getWindows();
        for (int i = 0; i < windows.length; i++) {
            if (name.equals(windows[i].getName()) && windows[i].isDisplayable()) return windows[i];
        }
        return null;
    }

    private static AbstractButton button(Window root, String name) {
        Component found = find(root, name);
        return found instanceof AbstractButton ? (AbstractButton) found : null;
    }

    private static JComboBox<?> combo(Window root, String name) {
        Component found = find(root, name);
        check(found instanceof JComboBox, "missing combo " + name);
        return (JComboBox<?>) found;
    }

    private static JSpinner spinner(Window root, String name) {
        Component found = find(root, name);
        check(found instanceof JSpinner, "missing spinner " + name);
        return (JSpinner) found;
    }

    private static String text(Window root, String name) throws Exception {
        final Component found = find(root, name);
        check(found != null, "missing " + name);
        final Method method = found.getClass().getMethod("getText");
        final String[] value = new String[1];
        SwingUtilities.invokeAndWait(new Runnable() {
            public void run() {
                try { value[0] = String.valueOf(method.invoke(found)); }
                catch (Exception failure) { throw new AssertionError(failure); }
            }
        });
        return value[0];
    }

    private static Component find(Component root, String name) {
        if (root == null) return null;
        if (name.equals(root.getName())) return root;
        if (root instanceof java.awt.Container) {
            Component[] children = ((java.awt.Container) root).getComponents();
            for (int i = 0; i < children.length; i++) {
                Component found = find(children[i], name);
                if (found != null) return found;
            }
        }
        return null;
    }

    private static void click(final AbstractButton button) throws Exception {
        check(button != null, "missing button");
        SwingUtilities.invokeAndWait(new Runnable() {
            public void run() { button.doClick(); }
        });
    }

    private static Runnable select(final JComboBox<?> box, final Object value) {
        return new Runnable() {
            public void run() { box.setSelectedItem(value); }
        };
    }

    private static Runnable spin(final JSpinner spinner, final Object value) {
        return new Runnable() {
            public void run() { spinner.setValue(value); }
        };
    }

    private static void dispose(final Window window) throws Exception {
        if (window == null) return;
        SwingUtilities.invokeAndWait(new Runnable() {
            public void run() { window.dispose(); }
        });
    }
}
