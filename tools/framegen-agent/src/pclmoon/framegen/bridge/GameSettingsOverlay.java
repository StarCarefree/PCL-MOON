package pclmoon.framegen.bridge;

import java.awt.Color;
import java.awt.Component;
import java.awt.Dimension;
import java.awt.Font;
import java.awt.Graphics;
import java.awt.Graphics2D;
import java.awt.GraphicsConfiguration;
import java.awt.GraphicsDevice;
import java.awt.GraphicsEnvironment;
import java.awt.Insets;
import java.awt.Rectangle;
import java.awt.RenderingHints;
import java.awt.Window;
import java.awt.event.MouseAdapter;
import java.awt.event.MouseEvent;
import java.awt.geom.AffineTransform;
import java.awt.geom.RoundRectangle2D;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicInteger;
import javax.swing.BorderFactory;
import javax.swing.Box;
import javax.swing.BoxLayout;
import javax.swing.JButton;
import javax.swing.JCheckBox;
import javax.swing.JComboBox;
import javax.swing.JComponent;
import javax.swing.JDialog;
import javax.swing.JLabel;
import javax.swing.JPanel;
import javax.swing.JScrollPane;
import javax.swing.JSpinner;
import javax.swing.SpinnerNumberModel;
import javax.swing.SwingUtilities;
import javax.swing.border.EmptyBorder;

/**
 * Optional in-game settings chrome. Render-thread {@link #update} only posts the latest
 * GLFW screen rectangle; every AWT call runs on the EDT. No input hooks and no capture.
 */
public final class GameSettingsOverlay implements AutoCloseable {
    static final String OVERLAY_PROPERTY = "pclmoon.framegen.overlay";
    private static final int[] FPS = {30, 60, 90, 120, 144, 165, 240};
    private static final Integer[] MULTIPLIERS = {Integer.valueOf(2), Integer.valueOf(3), Integer.valueOf(4)};
     private static final String[] QUALITY = {"性能", "均衡", "画质"};
     private static final String[] WAIT_MODE = {"节能", "均衡", "精确"};
     private static final Integer[] FLOW = {Integer.valueOf(80), Integer.valueOf(120), Integer.valueOf(160), Integer.valueOf(240), Integer.valueOf(320)};
    private static final Color INK = new Color(232, 236, 241);
    private static final Color MUTED = new Color(168, 178, 190);
    private static final Color PANEL = new Color(28, 32, 38);
    private static final Color EDGE = new Color(72, 82, 96);
    private static final Color ACCENT = new Color(92, 168, 214);
    private static final long MOVE_INTERVAL_NS = 50000000L;
    private static final int ERROR_BUDGET = 8;
    private static final String BODY_HTML =
        "<html><div style='width:190px'>先按 Esc 释放鼠标。插帧会增加延迟，HUD、遮挡和快速运动可能出现伪影。"
         + "无法消除 GC、加载或驱动停顿。未保存的设置仅本次生效，不改 options.txt；原生未接入时会明确标明。"
         + "独占全屏可能遮挡浮层。独立限帧与插帧目标分开，0 表示关闭。</div></html>";

    private final AtomicBoolean posted = new AtomicBoolean();
    private final AtomicBoolean closed = new AtomicBoolean();
    private final AtomicInteger errors = new AtomicInteger();
    private final Runnable pump = new Runnable() {
        public void run() { posted.set(false); apply(); }
    };
    private volatile int x;
    private volatile int y;
    private volatile int width;
    private volatile int height;
    private volatile boolean foreground;
    private volatile long lastMoveNs;
    private volatile boolean placed;

    private JDialog gear;
    private JDialog panel;
    private JDialog hud;
    private javax.swing.Timer metricsTimer;
    private JButton gearButton;
    private JButton fpsButton;
    private JCheckBox generationBox;
    private JCheckBox diagnosticsBox;
    private JCheckBox adaptiveBox;
    private JCheckBox protectionBox;
    private JCheckBox nativeBox;
    private JCheckBox showFpsBox;
    private JComboBox<Integer> fpsBox;
    private JComboBox<Integer> multiplierBox;
    private JComboBox<String> qualityBox;
    private JComboBox<Integer> flowBox;
    private JSpinner gpuSpinner;
    private JSpinner stallSpinner;
     private JLabel status;
     private JLabel profileStatus;
     private JSpinner fpsLimitSpinner;
     private JComboBox<String> waitModeBox;
     private JCheckBox telemetryBox;
     private JButton saveProfileButton;
     private JButton resetProfileButton;
    private JLabel hudPresented;
    private JLabel hudRender;
    private JLabel hudGenerated;
    private JLabel hudLow;
    private JLabel hudGpu;
    private JLabel hudReason;
    private JLabel hudNative;
    private JLabel hudCpu;
    private JLabel hudProcess;
    private JLabel hudGpuUse;
    private JLabel hudGpuTemp;
    private JLabel hudCpuTemp;
    private JButton hudDetailsButton;
    private JPanel hudDetails;
    private boolean hudDetailsOpen;
    private boolean open;
    private boolean built;
    private boolean syncing;

    public GameSettingsOverlay() { }

    /**
     * Hot path: coalesce to one pending EDT task. Must not reflect, block, or touch GL.
     */
    public void update(int x, int y, int width, int height, boolean gameForeground) {
        if (closed.get()) return;
        this.x = x;
        this.y = y;
        this.width = width;
        this.height = height;
        this.foreground = gameForeground;
        if (posted.compareAndSet(false, true)) {
            try {
                SwingUtilities.invokeLater(pump);
            } catch (Throwable failure) {
                posted.set(false);
                report(failure);
            }
        }
    }

    public void close() {
        if (!closed.compareAndSet(false, true)) return;
        Runnable dispose = new Runnable() {
            public void run() { disposeWindows(); }
        };
        try {
            if (SwingUtilities.isEventDispatchThread()) dispose.run();
            else SwingUtilities.invokeLater(dispose);
        } catch (Throwable failure) {
            report(failure);
        }
    }

    private void apply() {
        if (closed.get()) {
            disposeWindows();
            return;
        }
        try {
            if (!built) build();
            boolean show = width > 0 && height > 0 && (foreground || panelOwnsFocus());
            if (!show) {
                open = false;
                hide(panel);
                hide(hud);
                hide(gear);
                return;
            }
            placeGear();
            placeHud();
            if (open) placePanel();
            else hide(panel);
        } catch (Throwable failure) {
            report(failure);
            close();
        }
    }

    private void build() {
        built = true;
        gearButton = iconButton("pcl-framegen-gear", "实验性插帧设置", true);
        gearButton.addActionListener(new java.awt.event.ActionListener() {
            public void actionPerformed(java.awt.event.ActionEvent event) { toggle(); }
        });
        fpsButton = iconButton("pcl-framegen-fps-toggle", "显示/隐藏 FPS（呈现提交，不是物理显示器帧数）", false);
        fpsButton.addActionListener(new java.awt.event.ActionListener() {
            public void actionPerformed(java.awt.event.ActionEvent event) {
                FrameSettings.setShowFps(!FrameSettings.showFps());
                if (showFpsBox != null) showFpsBox.setSelected(FrameSettings.showFps());
                fpsButton.repaint();
                placeHud();
            }
        });

        gear = new JDialog((Window) null);
        gear.setName("pcl-framegen-gear-window");
        gear.setUndecorated(true);
        gear.setFocusableWindowState(false);
        gear.setAlwaysOnTop(true);
        gear.setBackground(new Color(0, 0, 0, 0));
        JPanel gearHost = new JPanel();
        gearHost.setLayout(new BoxLayout(gearHost, BoxLayout.X_AXIS));
        gearHost.setOpaque(false);
        gearHost.add(fpsButton);
        gearHost.add(Box.createHorizontalStrut(4));
        gearHost.add(gearButton);
        gear.setContentPane(gearHost);

        generationBox = box("实验性插帧", FrameSettings.generation(), "pcl-framegen-generation");
        diagnosticsBox = box("记录长帧诊断", FrameSettings.diagnostics(), "pcl-framegen-diagnostics");
        adaptiveBox = box("自适应节奏", FrameSettings.adaptivePacing(), "pcl-framegen-adaptive");
        protectionBox = box("低帧保护", FrameSettings.lowFpsProtection(), "pcl-framegen-protection");
        nativeBox = box("原生限帧协调（仅本次）", FrameSettings.nativeFrameControl(), "pcl-framegen-native-control");
        nativeBox.setToolTipText("临时协调游戏限帧，不改 options.txt，不取消 tick 或物理逻辑");
        showFpsBox = box("显示 FPS", FrameSettings.showFps(), "pcl-framegen-show-fps");
        generationBox.addActionListener(new java.awt.event.ActionListener() {
            public void actionPerformed(java.awt.event.ActionEvent event) {
                if (!syncing) FrameSettings.setGeneration(generationBox.isSelected());
                refreshStatus();
            }
        });
        diagnosticsBox.addActionListener(new java.awt.event.ActionListener() {
            public void actionPerformed(java.awt.event.ActionEvent event) {
                if (!syncing) FrameSettings.setDiagnostics(diagnosticsBox.isSelected());
                refreshStatus();
            }
        });
        adaptiveBox.addActionListener(new java.awt.event.ActionListener() {
            public void actionPerformed(java.awt.event.ActionEvent event) {
                if (!syncing) FrameSettings.setAdaptivePacing(adaptiveBox.isSelected());
            }
        });
        protectionBox.addActionListener(new java.awt.event.ActionListener() {
            public void actionPerformed(java.awt.event.ActionEvent event) {
                if (!syncing) FrameSettings.setLowFpsProtection(protectionBox.isSelected());
            }
        });
        nativeBox.addActionListener(new java.awt.event.ActionListener() {
            public void actionPerformed(java.awt.event.ActionEvent event) {
                if (!syncing) FrameSettings.setNativeFrameControl(nativeBox.isSelected());
                refreshStatus();
            }
        });
        showFpsBox.addActionListener(new java.awt.event.ActionListener() {
            public void actionPerformed(java.awt.event.ActionEvent event) {
                if (!syncing) FrameSettings.setShowFps(showFpsBox.isSelected());
                if (fpsButton != null) fpsButton.repaint();
                placeHud();
            }
        });

        fpsBox = integerCombo(FPS, "pcl-framegen-fps");
        selectInteger(fpsBox, FrameSettings.outputFps(), true);
        fpsBox.addActionListener(new java.awt.event.ActionListener() {
            public void actionPerformed(java.awt.event.ActionEvent event) {
                Object selected = fpsBox.getSelectedItem();
                if (!syncing && selected instanceof Integer) FrameSettings.setOutputFps(((Integer) selected).intValue());
                refreshStatus();
            }
        });
        multiplierBox = new JComboBox<Integer>(MULTIPLIERS);
        styleCombo(multiplierBox, "pcl-framegen-multiplier");
        selectInteger(multiplierBox, FrameSettings.multiplier(), false);
        multiplierBox.addActionListener(new java.awt.event.ActionListener() {
            public void actionPerformed(java.awt.event.ActionEvent event) {
                Object selected = multiplierBox.getSelectedItem();
                if (!syncing && selected instanceof Integer) FrameSettings.setMultiplier(((Integer) selected).intValue());
                refreshStatus();
            }
        });
        qualityBox = new JComboBox<String>(QUALITY);
        styleCombo(qualityBox, "pcl-framegen-quality");
        int quality = FrameSettings.quality();
        if (quality >= 0 && quality < QUALITY.length) qualityBox.setSelectedIndex(quality);
        qualityBox.addActionListener(new java.awt.event.ActionListener() {
            public void actionPerformed(java.awt.event.ActionEvent event) {
                if (!syncing) FrameSettings.setQuality(qualityBox.getSelectedIndex());
            }
        });
        flowBox = new JComboBox<Integer>(FLOW);
        styleCombo(flowBox, "pcl-framegen-flow");
        selectInteger(flowBox, FrameSettings.flowWidth(), false);
        flowBox.addActionListener(new java.awt.event.ActionListener() {
            public void actionPerformed(java.awt.event.ActionEvent event) {
                Object selected = flowBox.getSelectedItem();
                if (!syncing && selected instanceof Integer) FrameSettings.setFlowWidth(((Integer) selected).intValue());
            }
        });
        gpuSpinner = spinner(2, 20, FrameSettings.gpuBudgetMs(), "pcl-framegen-gpu-budget");
         stallSpinner = spinner(50, 500, FrameSettings.stallThresholdMs(), "pcl-framegen-stall");
         fpsLimitSpinner = spinner(0, 999, PacingSettings.fpsLimit(), "pcl-framegen-fps-limit");
         fpsLimitSpinner.setToolTipText("<html><div style='width:190px'>独立限帧 FPS，0 表示关闭。与插帧呈现目标分开，不依赖显示 FPS。</div></html>");
         waitModeBox = new JComboBox<String>(WAIT_MODE);
         styleCombo(waitModeBox, "pcl-framegen-wait-mode");
         int waitMode = clamp(0, WAIT_MODE.length - 1, PacingSettings.waitMode());
         waitModeBox.setSelectedIndex(waitMode);
         waitModeBox.setToolTipText("<html><div style='width:190px'>精确最多忙等 1ms，均衡最多忙等 100µs，节能不忙等。精确会明显增加 CPU 占用。</div></html>");
         telemetryBox = box("硬件监控", PacingSettings.telemetry(), "pcl-framegen-telemetry");
         telemetryBox.setToolTipText("<html><div style='width:190px'>显示系统 CPU、进程 CPU、GPU 占用和温度。无传感器时显示 N/A，不估计。</div></html>");
         gpuSpinner.addChangeListener(new javax.swing.event.ChangeListener() {
             public void stateChanged(javax.swing.event.ChangeEvent event) {
                 if (!syncing) FrameSettings.setGpuBudgetMs(((Number) gpuSpinner.getValue()).intValue());
             }
         });
         stallSpinner.addChangeListener(new javax.swing.event.ChangeListener() {
             public void stateChanged(javax.swing.event.ChangeEvent event) {
                 if (!syncing) FrameSettings.setStallThresholdMs(((Number) stallSpinner.getValue()).intValue());
             }
         });
         fpsLimitSpinner.addChangeListener(new javax.swing.event.ChangeListener() {
             public void stateChanged(javax.swing.event.ChangeEvent event) {
                 if (!syncing) PacingSettings.setFpsLimit(((Number) fpsLimitSpinner.getValue()).intValue());
             }
         });
         waitModeBox.addActionListener(new java.awt.event.ActionListener() {
             public void actionPerformed(java.awt.event.ActionEvent event) {
                 if (!syncing) PacingSettings.setWaitMode(waitModeBox.getSelectedIndex());
             }
         });
         telemetryBox.addActionListener(new java.awt.event.ActionListener() {
             public void actionPerformed(java.awt.event.ActionEvent event) {
                 if (!syncing) PacingSettings.setTelemetry(telemetryBox.isSelected());
             }
         });
         saveProfileButton = textButton("保存当前实例", "pcl-framegen-save-instance");
         resetProfileButton = textButton("恢复默认", "pcl-framegen-reset-instance");
         saveProfileButton.addActionListener(new java.awt.event.ActionListener() {
             public void actionPerformed(java.awt.event.ActionEvent event) { saveProfile(); }
         });
         resetProfileButton.addActionListener(new java.awt.event.ActionListener() {
             public void actionPerformed(java.awt.event.ActionEvent event) { resetProfile(); }
         });
         JPanel profileButtons = row();
         profileButtons.add(saveProfileButton);
         profileButtons.add(Box.createHorizontalStrut(6));
         profileButtons.add(resetProfileButton);
         profileButtons.add(Box.createHorizontalGlue());
         profileStatus = label("", 12, MUTED);
         profileStatus.setName("pcl-framegen-profile-status");

        JButton closeButton = textButton("X", "pcl-framegen-close");
        closeButton.addActionListener(new java.awt.event.ActionListener() {
            public void actionPerformed(java.awt.event.ActionEvent event) { setOpen(false); }
        });

        JLabel title = label("实验性画面插帧", 14, INK);
        JLabel body = label(BODY_HTML, 12, MUTED);
        body.setName("pcl-framegen-body");
        status = label("", 12, ACCENT);
        status.setName("pcl-framegen-status");

        JPanel head = row();
        head.add(title);
        head.add(Box.createHorizontalGlue());
        head.add(closeButton);

        JPanel root = new JPanel();
        root.setLayout(new BoxLayout(root, BoxLayout.Y_AXIS));
        root.setOpaque(false);
        root.setBorder(new EmptyBorder(10, 12, 12, 12));
        add(root, body);
        add(root, Box.createVerticalStrut(8));
        add(root, generationBox);
        add(root, comboRow("倍率", multiplierBox));
        add(root, comboRow("呈现目标", fpsBox));
        add(root, comboRow("质量档", qualityBox));
        add(root, comboRow("运动分辨率", flowBox));
        add(root, spinnerRow("GPU 预算 ms", gpuSpinner));
        add(root, spinnerRow("低帧阈值 ms", stallSpinner));
        add(root, Box.createVerticalStrut(4));
        add(root, adaptiveBox);
        add(root, protectionBox);
        add(root, nativeBox);
         add(root, diagnosticsBox);
         add(root, showFpsBox);
         add(root, Box.createVerticalStrut(8));
         add(root, spinnerRow("独立限帧 FPS（0=关）", fpsLimitSpinner));
         add(root, comboRow("等待精度", waitModeBox));
         add(root, telemetryBox);
         add(root, profileButtons);
         add(root, profileStatus);
         add(root, Box.createVerticalStrut(6));
         add(root, status);
         refreshStatus();
         refreshProfileStatus();
         root.setPreferredSize(new Dimension(280, root.getPreferredSize().height));

        JPanel card = card();
        head.setBorder(new EmptyBorder(10, 12, 0, 12));
        card.add(head, java.awt.BorderLayout.NORTH);
        JScrollPane scroll = new JScrollPane(root);
        scroll.setName("pcl-framegen-scroll");
        scroll.setOpaque(false);
        scroll.getViewport().setOpaque(false);
        scroll.setBorder(BorderFactory.createEmptyBorder());
        scroll.setHorizontalScrollBarPolicy(JScrollPane.HORIZONTAL_SCROLLBAR_NEVER);
        scroll.setVerticalScrollBarPolicy(JScrollPane.VERTICAL_SCROLLBAR_AS_NEEDED);
        scroll.getVerticalScrollBar().setUnitIncrement(16);
        card.add(scroll);

        panel = new JDialog((Window) null, "实验性插帧");
        panel.setName("pcl-framegen-panel");
        panel.setUndecorated(true);
        panel.setType(Window.Type.UTILITY);
        panel.setAlwaysOnTop(true);
        panel.setFocusableWindowState(true);
        panel.setBackground(new Color(0, 0, 0, 0));
        panel.setContentPane(card);
        panel.setDefaultCloseOperation(JDialog.DO_NOTHING_ON_CLOSE);
        panel.addWindowListener(new java.awt.event.WindowAdapter() {
            public void windowClosing(java.awt.event.WindowEvent event) { setOpen(false); }
        });
        panel.addMouseListener(new MouseAdapter() {
            public void mousePressed(MouseEvent event) { panel.toFront(); }
        });
        panel.pack();
        int panelWidth = Math.max(300, Math.min(360, panel.getPreferredSize().width));
        panel.setSize(panelWidth, panel.getPreferredSize().height);

        buildHud();
    }

    private void buildHud() {
        hudPresented = metricLabel("pcl-framegen-hud-presented", 26, INK);
        hudRender = metricLabel("pcl-framegen-hud-render", 12, MUTED);
        hudGenerated = metricLabel("pcl-framegen-hud-generated", 12, MUTED);
        hudLow = metricLabel("pcl-framegen-hud-low", 12, INK);
        hudGpu = metricLabel("pcl-framegen-hud-gpu", 12, INK);
        hudReason = metricLabel("pcl-framegen-hud-reason", 12, MUTED);
        hudNative = metricLabel("pcl-framegen-hud-native", 12, MUTED);
        hudCpu = metricLabel("pcl-framegen-hud-cpu", 12, INK);
        hudProcess = metricLabel("pcl-framegen-hud-process", 12, INK);
        hudGpuUse = metricLabel("pcl-framegen-hud-gpu-use", 12, INK);
        hudGpuTemp = metricLabel("pcl-framegen-hud-gpu-temp", 12, INK);
        hudCpuTemp = metricLabel("pcl-framegen-hud-cpu-temp", 12, MUTED);
        JLabel caption = label("呈现提交", 11, MUTED);
        caption.setName("pcl-framegen-hud-caption");
        caption.setToolTipText("计数的是成功提交到呈现队列的次数，不是物理显示器刷新帧数。停顿时会如实降到 0，不用目标 FPS×倍率伪造。合成帧不改善真实渲染或输入延迟。");
        JLabel statusLine = metricLabel("pcl-framegen-hud-status", 12, ACCENT);
        hudDetailsButton = textButton("详情", "pcl-framegen-hud-details");
        hudDetailsButton.setToolTipText("展开或收起 1%low、帧时间、原生状态与硬件。信息不丢弃，只是默认收起以免占满画面。");
        hudDetailsButton.addActionListener(new java.awt.event.ActionListener() {
            public void actionPerformed(java.awt.event.ActionEvent event) {
                hudDetailsOpen = !hudDetailsOpen;
                if (hudDetails != null) hudDetails.setVisible(hudDetailsOpen);
                hudDetailsButton.setText(hudDetailsOpen ? "收起" : "详情");
                fitHud();
            }
        });

        JPanel split = row();
        split.add(hudRender);
        split.add(Box.createHorizontalStrut(10));
        split.add(hudGenerated);
        split.add(Box.createHorizontalGlue());

        JPanel head = row();
        head.add(caption);
        head.add(Box.createHorizontalGlue());
        head.add(hudDetailsButton);

        hudDetails = new JPanel();
        hudDetails.setLayout(new BoxLayout(hudDetails, BoxLayout.Y_AXIS));
        hudDetails.setOpaque(false);
        hudDetails.setAlignmentX(Component.LEFT_ALIGNMENT);
        hudDetails.setName("pcl-framegen-hud-detail-panel");
        add(hudDetails, hudLow);
        add(hudDetails, hudGpu);
        add(hudDetails, hudNative);
        add(hudDetails, hudCpu);
        add(hudDetails, hudProcess);
        add(hudDetails, hudGpuUse);
        add(hudDetails, hudGpuTemp);
        add(hudDetails, hudCpuTemp);
        hudDetails.setVisible(false);

        JPanel body = new JPanel();
        body.setLayout(new BoxLayout(body, BoxLayout.Y_AXIS));
        body.setOpaque(false);
        body.setBorder(new EmptyBorder(8, 10, 8, 10));
        body.setName("pcl-framegen-hud-body");
        add(body, head);
        add(body, hudPresented);
        add(body, split);
        add(body, statusLine);
        add(body, hudReason);
        add(body, hudDetails);

        JPanel card = card();
        card.add(body);
        hud = new JDialog((Window) null);
        hud.setName("pcl-framegen-hud");
        hud.setUndecorated(true);
        hud.setFocusableWindowState(false);
        hud.setAlwaysOnTop(true);
        hud.setType(Window.Type.UTILITY);
        hud.setBackground(new Color(0, 0, 0, 0));
        hud.setContentPane(card);
        fitHud();
        metricsTimer = new javax.swing.Timer(250, new java.awt.event.ActionListener() {
            public void actionPerformed(java.awt.event.ActionEvent event) {
                if (closed.get()) { ((javax.swing.Timer) event.getSource()).stop(); return; }
                boolean panelVisible = panel != null && panel.isVisible();
                boolean hudVisible = hud != null && hud.isVisible();
                if (panelVisible) syncControls();
                if (hudVisible) refreshHud();
            }
        });
        metricsTimer.start();
    }

    /** Prefer a compact card; never wider than the game window or 280px. */
    private void fitHud() {
        if (hud == null) return;
        hud.pack();
        int cap = 280;
        if (width > 0) cap = Math.max(160, Math.min(280, width - 20));
        int preferred = hud.getPreferredSize().width;
        int w = Math.max(160, Math.min(cap, Math.max(preferred, 200)));
        int h = Math.max(hud.getPreferredSize().height, hud.getHeight());
        hud.setSize(w, h);
        applyHudWrap(w);
        hud.pack();
        h = hud.getPreferredSize().height;
        if (height > 40) h = Math.min(h, Math.max(48, height - 20));
        hud.setSize(w, h);
    }

    private void applyHudWrap(int cardWidth) {
        int inner = Math.max(120, cardWidth - 28);
        wrap(hudReason, inner);
        wrap(hudNative, inner);
        wrap(hudGpuUse, inner);
        Component statusLine = findNamed(hud, "pcl-framegen-hud-status");
        if (statusLine instanceof JLabel) wrap((JLabel) statusLine, inner);
    }

    private JButton iconButton(final String name, String tip, final boolean gearIcon) {
        JButton button = new JButton() {
            public void paintComponent(Graphics graphics) {
                Graphics2D g = (Graphics2D) graphics.create();
                try {
                    g.setRenderingHint(RenderingHints.KEY_ANTIALIASING, RenderingHints.VALUE_ANTIALIAS_ON);
                    int w = getWidth();
                    int h = getHeight();
                    g.setColor(new Color(22, 26, 32, 220));
                    g.fillOval(1, 1, w - 2, h - 2);
                    boolean active = gearIcon ? open : FrameSettings.showFps();
                    g.setColor(active ? ACCENT : INK);
                    if (gearIcon) paintGear(g, w, h);
                    else paintFps(g, w, h);
                } finally {
                    g.dispose();
                }
            }
        };
        button.setName(name);
        button.setToolTipText(tip);
        button.setFocusable(false);
        button.setBorderPainted(false);
        button.setContentAreaFilled(false);
        button.setOpaque(false);
        button.setCursor(java.awt.Cursor.getPredefinedCursor(java.awt.Cursor.HAND_CURSOR));
        return button;
    }

    private static void paintGear(Graphics2D g, int w, int h) {
        g.setStroke(new java.awt.BasicStroke(Math.max(1.6f, w / 18f), java.awt.BasicStroke.CAP_ROUND, java.awt.BasicStroke.JOIN_ROUND));
        int cx = w / 2;
        int cy = h / 2;
        int outer = Math.max(4, w / 2 - 5);
        int inner = Math.max(2, outer / 3);
        g.drawOval(cx - inner, cy - inner, inner * 2, inner * 2);
        for (int i = 0; i < 8; i++) {
            double a = i * Math.PI / 4.0;
            int x1 = cx + (int) Math.round(Math.cos(a) * (inner + 1));
            int y1 = cy + (int) Math.round(Math.sin(a) * (inner + 1));
            int x2 = cx + (int) Math.round(Math.cos(a) * outer);
            int y2 = cy + (int) Math.round(Math.sin(a) * outer);
            g.drawLine(x1, y1, x2, y2);
        }
    }

    private static void paintFps(Graphics2D g, int w, int h) {
        g.setFont(ui(Math.max(9, w / 4)));
        String text = "FPS";
        int tw = g.getFontMetrics().stringWidth(text);
        g.drawString(text, Math.max(2, (w - tw) / 2), h / 2 + g.getFontMetrics().getAscent() / 3);
    }

    private void toggle() {
        setOpen(!open);
    }

    private void setOpen(boolean value) {
        open = value && !closed.get();
        if (gearButton != null) gearButton.repaint();
        if (!built || closed.get()) return;
        if (open) placePanel();
        else hide(panel);
    }

    private void placeGear() {
        int size = clamp(28, 44, Math.min(width, height) / 18);
        Rectangle bounds = gameBounds();
        int gap = 4;
        int total = size * 2 + gap;
        Rectangle spot = new Rectangle(bounds.x + bounds.width - total - 10, bounds.y + 10, total, size);
        spot = clampInside(spot, bounds);
        move(gear, spot, true);
    }

    private void placePanel() {
        syncControls();
        Rectangle bounds = gameBounds();
        int margin = 8;
        Rectangle limit = inset(bounds, margin);
        int gearSize = gear == null ? 36 : gear.getHeight();
        int maxH = Math.max(80, limit.height - gearSize - 6);
        int maxW = Math.max(220, Math.min(360, limit.width));
        Dimension preferred = panel.getPreferredSize();
        int pw = Math.min(Math.max(preferred.width, 300), maxW);
        int ph = Math.min(Math.max(preferred.height, 80), maxH);
        if (panel.getWidth() != pw || panel.getHeight() != ph) panel.setSize(pw, ph);
        int px = limit.x + limit.width - pw;
        int py = limit.y + gearSize + 6;
        if (py + ph > limit.y + limit.height) py = limit.y + limit.height - ph;
        Rectangle spot = clampInside(new Rectangle(px, py, pw, ph), limit);
        move(panel, spot, true);
    }

    private void placeHud() {
        if (hud == null || closed.get()) return;
        if (!FrameSettings.showFps() || width <= 0 || height <= 0 || !foreground) {
            hide(hud);
            return;
        }
        fitHud();
        refreshHud();
        Rectangle bounds = gameBounds();
        Dimension size = hud.getSize();
        if (size.width < 40 || size.height < 20) {
            hud.pack();
            size = hud.getSize();
        }
        int margin = 10;
        int hw = Math.min(size.width, Math.max(1, bounds.width - margin * 2));
        int hh = Math.min(size.height, Math.max(1, bounds.height - margin * 2));
        Rectangle spot = clampInside(new Rectangle(bounds.x + margin, bounds.y + margin, hw, hh), inset(bounds, margin));
        move(hud, spot, true);
    }

    private void syncControls() {
        syncFlag(generationBox, FrameSettings.generation());
        syncFlag(diagnosticsBox, FrameSettings.diagnostics());
        syncFlag(adaptiveBox, FrameSettings.adaptivePacing());
        syncFlag(protectionBox, FrameSettings.lowFpsProtection());
        syncFlag(nativeBox, FrameSettings.nativeFrameControl());
        syncFlag(showFpsBox, FrameSettings.showFps());
        if (fpsBox != null && !fpsBox.isPopupVisible()) selectInteger(fpsBox, FrameSettings.outputFps(), true);
        if (multiplierBox != null && !multiplierBox.isPopupVisible()) selectInteger(multiplierBox, FrameSettings.multiplier(), false);
        if (qualityBox != null && !qualityBox.isPopupVisible()) {
            int quality = clamp(0, QUALITY.length - 1, FrameSettings.quality());
            if (qualityBox.getSelectedIndex() != quality) {
                syncing = true;
                try { qualityBox.setSelectedIndex(quality); }
                finally { syncing = false; }
            }
        }
        if (flowBox != null && !flowBox.isPopupVisible()) selectInteger(flowBox, FrameSettings.flowWidth(), false);
        syncSpinner(gpuSpinner, FrameSettings.gpuBudgetMs());
         syncSpinner(stallSpinner, FrameSettings.stallThresholdMs());
         syncSpinner(fpsLimitSpinner, PacingSettings.fpsLimit());
         if (waitModeBox != null && !waitModeBox.isPopupVisible()) {
             int mode = clamp(0, WAIT_MODE.length - 1, PacingSettings.waitMode());
             if (waitModeBox.getSelectedIndex() != mode) {
                 syncing = true;
                 try { waitModeBox.setSelectedIndex(mode); }
                 finally { syncing = false; }
             }
         }
         syncFlag(telemetryBox, PacingSettings.telemetry());
         if (fpsButton != null) fpsButton.repaint();
         refreshStatus();
         refreshProfileStatus();
     }

     private void refreshProfileStatus() {
         if (profileStatus == null) return;
         String path = "";
         String state = "未配置";
         boolean configured = false;
         try {
             path = FramePacerProfiles.profilePath();
             String reported = FramePacerProfiles.status();
             configured = path != null && path.length() > 0;
             if (!configured) state = "未配置";
             else if (reported == null || reported.length() == 0) state = "已配置";
             else state = reported;
         } catch (Throwable ignored) {
             configured = false;
             state = "未配置";
             path = "";
         }
         if (saveProfileButton != null) saveProfileButton.setEnabled(configured);
         if (resetProfileButton != null) resetProfileButton.setEnabled(configured);
         profileStatus.setText("<html><div style='width:190px'>配置 " + state + "</div></html>");
         profileStatus.setToolTipText(configured ? path : "未配置实例路径");
     }

    private void saveProfile() {
        if (saveProfileButton != null && !saveProfileButton.isEnabled()) return;
        try { FramePacerProfiles.saveInstance(); } catch (Throwable ignored) { }
        refreshProfileStatus();
    }

    private void resetProfile() {
        if (resetProfileButton != null && !resetProfileButton.isEnabled()) return;
        try { FramePacerProfiles.resetInstance(); } catch (Throwable ignored) { }
        refreshProfileStatus();
    }

    private void refreshStatus() {
        if (status == null) return;
        FrameMetrics.Snapshot snap = FrameMetrics.snapshot();
        String hook = snap == null || snap.nativeHook == null || snap.nativeHook.length() == 0 ? "未接入" : snap.nativeHook;
        status.setText("<html><div style='width:190px'>本次会话 · 目标 " + FrameSettings.outputFps()
            + " · " + FrameSettings.multiplier() + "× · 原生 " + hook
            + "<br>不改 options.txt · 不取消 tick/物理 · 统计实际提交</div></html>");
    }

    private void refreshHud() {
        FrameMetrics.Snapshot snap = FrameMetrics.snapshot();
        setMetric(hudPresented, fmt(snap.presentedFps));
        setMetric(hudRender, "真帧 " + fmt(snap.renderFps));
        setMetric(hudGenerated, "生成 " + fmt(snap.generatedFps));
        setMetric(hudLow, "1%low " + fmt(snap.onePercentLow) + " · P95 " + fmtMillis(snap.p95Millis) + " ms");
        setMetric(hudGpu, "GPU 生成 " + fmtMillis(snap.gpuMillis) + " ms · 原生 " + fmtMillis(snap.nativeRenderMillis) + " ms");
        String reason = snap.reason == null || snap.reason.length() == 0 ? "无" : snap.reason;
        setMetric(hudReason, "跳过 " + snap.skippedFrames + " · " + reason);
        String hook = snap.nativeHook == null || snap.nativeHook.length() == 0 ? "未接入" : snap.nativeHook;
        setMetric(hudNative, "原生 " + hook);
        Component statusLine = findNamed(hud, "pcl-framegen-hud-status");
        if (statusLine instanceof JLabel) setMetric((JLabel) statusLine, diagnosis(snap));
        if (PacingSettings.telemetry() && hud != null && hud.isVisible()) {
            try { FrameTelemetry.request(PacingSettings.rendererName()); }
            catch (Throwable ignored) { }
        }
        FrameTelemetry.Snapshot hardware = FrameTelemetry.snapshot();
        boolean fresh = PacingSettings.telemetry() && hardware != null && hardware.sampleNanos != 0
            && System.nanoTime() - hardware.sampleNanos >= 0 && System.nanoTime() - hardware.sampleNanos <= 3000000000L;
        setMetric(hudCpu, "系统 CPU " + pct(fresh ? hardware.cpuPercent : Double.NaN));
        setMetric(hudProcess, "进程 CPU " + pct(fresh ? hardware.processCpuPercent : Double.NaN));
        String gpuName = fresh && hardware.gpuName != null ? hardware.gpuName : "";
        setMetric(hudGpuUse, "GPU " + pct(fresh ? hardware.gpuPercent : Double.NaN)
            + (gpuName.length() > 0 ? " · " + gpuName : ""));
        if (hudGpuUse != null) hudGpuUse.setToolTipText(gpuName.length() > 0 ? gpuName : "GPU 名称不可用");
        setMetric(hudGpuTemp, "GPU 温度 " + temp(fresh ? hardware.gpuTempC : Double.NaN));
        setMetric(hudCpuTemp, "CPU 温度 " + temp(fresh ? hardware.cpuTempC : Double.NaN));
        if (hud != null && hud.isVisible()) fitHud();
    }

    /** Plain diagnosis. Never calls a zero-submit or disabled session "运行中". */
    private static String diagnosis(FrameMetrics.Snapshot snap) {
        if (!FrameSettings.generation()) return "插帧未开启 · 仅统计实际提交";
        if (snap.presentedFps <= 0 && snap.renderFps <= 0 && snap.generatedFps <= 0) {
            String why = snap.reason == null || snap.reason.length() == 0 ? "等待画面" : snap.reason;
            return "未在提交 · " + why;
        }
        if (snap.generatedFps <= 0) return "未生成 · 真帧不受合成改善";
        return "正在提交 · 合成不改善输入";
    }

     private static String pct(double value) {
         if (Double.isNaN(value) || Double.isInfinite(value) || value < 0) return "N/A";
         return String.valueOf(Math.round(value)) + "%";
     }

     private static String temp(double value) {
         if (Double.isNaN(value) || Double.isInfinite(value)) return "N/A";
         return String.valueOf(Math.round(value)) + "°C";
     }

    private static void setMetric(JLabel label, String text) {
        if (label != null) label.setText(text);
    }

    private static String fmt(double value) {
        if (Double.isNaN(value) || Double.isInfinite(value) || value < 0) return "0";
        return String.valueOf(Math.round(value));
    }

    private static String fmtMillis(double value) {
        if (Double.isNaN(value) || Double.isInfinite(value) || value < 0) return "N/A";
        return String.format(java.util.Locale.US, "%.1f", Double.valueOf(value));
    }

    private boolean panelOwnsFocus() {
        if (panel == null || !panel.isVisible()) return false;
        Window active = java.awt.KeyboardFocusManager.getCurrentKeyboardFocusManager().getActiveWindow();
        if (active == panel) return true;
        Component focus = java.awt.KeyboardFocusManager.getCurrentKeyboardFocusManager().getFocusOwner();
        return focus != null && SwingUtilities.getWindowAncestor(focus) == panel;
    }

    private Rectangle gameBounds() {
        int w = Math.max(1, width);
        int h = Math.max(1, height);
        Rectangle raw = new Rectangle(x, y, w, h);
        GraphicsConfiguration gc = configurationFor(raw);
        if (gc == null) return raw;
        AffineTransform transform = gc.getDefaultTransform();
        double sx = transform.getScaleX() == 0 ? 1 : transform.getScaleX();
        double sy = transform.getScaleY() == 0 ? 1 : transform.getScaleY();
        Rectangle device = gc.getBounds();
        int ax = device.x + (int) Math.round((x - device.x) / sx);
        int ay = device.y + (int) Math.round((y - device.y) / sy);
        int aw = Math.max(1, (int) Math.round(w / sx));
        int ah = Math.max(1, (int) Math.round(h / sy));
        Rectangle scaled = new Rectangle(ax, ay, aw, ah);
        Rectangle screen = new Rectangle(device);
        Insets insets = java.awt.Toolkit.getDefaultToolkit().getScreenInsets(gc);
        screen.x += insets.left;
        screen.y += insets.top;
        screen.width = Math.max(1, screen.width - insets.left - insets.right);
        screen.height = Math.max(1, screen.height - insets.top - insets.bottom);
        return clampInside(scaled, screen);
    }

    private static GraphicsConfiguration configurationFor(Rectangle raw) {
        GraphicsEnvironment env = GraphicsEnvironment.getLocalGraphicsEnvironment();
        GraphicsDevice[] devices = env.getScreenDevices();
        GraphicsConfiguration best = null;
        int bestArea = -1;
        for (int i = 0; i < devices.length; i++) {
            GraphicsConfiguration gc = devices[i].getDefaultConfiguration();
            Rectangle hit = gc.getBounds().intersection(raw);
            int area = Math.max(0, hit.width) * Math.max(0, hit.height);
            if (area > bestArea) {
                bestArea = area;
                best = gc;
            }
        }
        if (best == null) best = env.getDefaultScreenDevice().getDefaultConfiguration();
        return best;
    }

    private void move(Window window, Rectangle spot, boolean show) {
        if (window == null || closed.get()) return;
        boolean same = window.isVisible() && window.getX() == spot.x && window.getY() == spot.y
            && window.getWidth() == spot.width && window.getHeight() == spot.height;
        long now = System.nanoTime();
        if (same) return;
        if (window.isVisible() && placed && now - lastMoveNs < MOVE_INTERVAL_NS
            && Math.abs(window.getX() - spot.x) + Math.abs(window.getY() - spot.y) <= 2) return;
        if (window instanceof JDialog && !window.isDisplayable()) ((JDialog) window).setType(Window.Type.UTILITY);
        window.setBounds(spot);
        if (show && !window.isVisible() && !closed.get()) window.setVisible(true);
        if (window == gear) {
            placed = true;
            lastMoveNs = now;
        }
    }

    private static void hide(Window window) {
        if (window != null && window.isVisible()) window.setVisible(false);
    }

    private void disposeWindows() {
        if (metricsTimer != null) { metricsTimer.stop(); metricsTimer = null; }
        open = false;
        hide(panel);
        hide(hud);
        hide(gear);
        if (panel != null) panel.dispose();
        if (hud != null) hud.dispose();
        if (gear != null) gear.dispose();
        panel = null;
        hud = null;
        gear = null;
        built = false;
    }

    private void report(Throwable failure) {
        int n = errors.incrementAndGet();
        if (n <= ERROR_BUDGET || n % 1000 == 0) {
            System.err.println("[PCL FrameGen] settings overlay suppressed (" + failure.getClass().getSimpleName()
                + ", " + n + "); game presentation unchanged");
        }
    }

    private static JCheckBox box(String text, boolean selected, String name) {
        JCheckBox box = new JCheckBox(text, selected);
        box.setName(name);
        box.setOpaque(false);
        box.setForeground(INK);
        box.setFont(ui(13));
        box.setAlignmentX(Component.LEFT_ALIGNMENT);
        return box;
    }

    private static JButton textButton(String text, String name) {
        JButton button = new JButton(text);
        button.setName(name);
        button.setFocusable(false);
        button.setFont(ui(12));
        button.setForeground(INK);
        button.setBackground(new Color(48, 54, 64));
        button.setBorder(BorderFactory.createEmptyBorder(2, 8, 2, 8));
        return button;
    }

    private static JLabel label(String text, int size, Color color) {
        JLabel label = new JLabel(text);
        label.setFont(ui(size));
        label.setForeground(color);
        label.setAlignmentX(Component.LEFT_ALIGNMENT);
        return label;
    }

    private static JLabel metricLabel(String name, int size, Color color) {
        JLabel label = label(" ", size, color);
        label.setName(name);
        return label;
    }

    private static void wrap(JLabel label, int width) {
        if (label == null) return;
        String plain = plain(label.getText());
        label.putClientProperty("pcl-plain", plain);
        label.setText("<html><div style='width:" + width + "px'>" + escape(plain) + "</div></html>");
    }

    private static String plain(String text) {
        if (text == null) return "";
        int div = text.indexOf("'>");
        int end = text.lastIndexOf("</div>");
        if (text.startsWith("<html>") && div >= 0 && end > div) {
            return text.substring(div + 2, end).replace("&lt;", "<").replace("&gt;", ">").replace("&amp;", "&");
        }
        return text;
    }

    private static String escape(String text) {
        return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;");
    }

    private static Component findNamed(Component root, String name) {
        if (root == null) return null;
        if (name.equals(root.getName())) return root;
        if (root instanceof java.awt.Container) {
            Component[] children = ((java.awt.Container) root).getComponents();
            for (int i = 0; i < children.length; i++) {
                Component found = findNamed(children[i], name);
                if (found != null) return found;
            }
        }
        return null;
    }

    private static JPanel row() {
        JPanel row = new JPanel();
        row.setLayout(new BoxLayout(row, BoxLayout.X_AXIS));
        row.setOpaque(false);
        row.setAlignmentX(Component.LEFT_ALIGNMENT);
        return row;
    }

    private static JPanel comboRow(String caption, JComponent editor) {
        JPanel row = row();
        row.add(label(caption, 13, INK));
        row.add(Box.createHorizontalStrut(8));
        row.add(editor);
        row.add(Box.createHorizontalGlue());
        return row;
    }

    private static JPanel spinnerRow(String caption, JSpinner spinner) {
        return comboRow(caption, spinner);
    }

    private static void add(JComponent parent, Component child) {
        if (child instanceof JComponent) ((JComponent) child).setAlignmentX(Component.LEFT_ALIGNMENT);
        parent.add(child);
    }

    private static Font ui(int size) {
        String family = Font.DIALOG;
        String[] preferred = {"Microsoft YaHei UI", "Microsoft YaHei", "PingFang SC", "Noto Sans CJK SC", "SimHei"};
        String[] available = GraphicsEnvironment.getLocalGraphicsEnvironment().getAvailableFontFamilyNames();
        for (int i = 0; i < preferred.length && family == Font.DIALOG; i++) {
            for (int j = 0; j < available.length; j++) {
                if (preferred[i].equalsIgnoreCase(available[j])) { family = available[j]; break; }
            }
        }
        return new Font(family, size >= 20 ? Font.BOLD : Font.PLAIN, size);
    }

    private JComboBox<Integer> integerCombo(int[] values, String name) {
        Integer[] choices = new Integer[values.length];
        for (int i = 0; i < values.length; i++) choices[i] = Integer.valueOf(values[i]);
        JComboBox<Integer> box = new JComboBox<Integer>(choices);
        styleCombo(box, name);
        return box;
    }

    private static void styleCombo(JComboBox<?> box, String name) {
        box.setName(name);
        box.setBackground(new Color(40, 46, 54));
        box.setForeground(INK);
        box.setFont(ui(12));
        box.setMaximumSize(new Dimension(140, 26));
    }

    private JSpinner spinner(int min, int max, int value, String name) {
        JSpinner spinner = new JSpinner(new SpinnerNumberModel(clamp(min, max, value), min, max, 1));
        spinner.setName(name);
        spinner.setFont(ui(12));
        spinner.setMaximumSize(new Dimension(90, 26));
        Component editor = spinner.getEditor();
        if (editor instanceof JSpinner.DefaultEditor) {
            ((JSpinner.DefaultEditor) editor).getTextField().setColumns(3);
        }
        return spinner;
    }

    private void selectInteger(JComboBox<Integer> box, int value, boolean allowCustom) {
        if (box == null) return;
        syncing = true;
        try {
            Integer wanted = Integer.valueOf(value);
            boolean exists = false;
            for (int i = 0; i < box.getItemCount(); i++) if (wanted.equals(box.getItemAt(i))) exists = true;
            if (!exists && allowCustom) box.addItem(wanted);
            if (exists || allowCustom) {
                if (!wanted.equals(box.getSelectedItem())) box.setSelectedItem(wanted);
            }
        } finally { syncing = false; }
    }

    private void syncFlag(JCheckBox box, boolean value) {
        if (box != null && box.isSelected() != value) box.setSelected(value);
    }

    private void syncSpinner(JSpinner spinner, int value) {
        if (spinner == null) return;
        Object current = spinner.getValue();
        if (!(current instanceof Number) || ((Number) current).intValue() != value) {
            syncing = true;
            try { spinner.setValue(Integer.valueOf(value)); }
            finally { syncing = false; }
        }
    }

    private static JPanel card() {
        JPanel card = new JPanel(new java.awt.BorderLayout()) {
            protected void paintComponent(Graphics graphics) {
                Graphics2D g = (Graphics2D) graphics.create();
                try {
                    g.setRenderingHint(RenderingHints.KEY_ANTIALIASING, RenderingHints.VALUE_ANTIALIAS_ON);
                    g.setColor(PANEL);
                    g.fill(new RoundRectangle2D.Float(0, 0, getWidth() - 1, getHeight() - 1, 16, 16));
                    g.setColor(EDGE);
                    g.draw(new RoundRectangle2D.Float(0.5f, 0.5f, getWidth() - 2f, getHeight() - 2f, 16, 16));
                } finally {
                    g.dispose();
                }
            }
        };
        card.setOpaque(false);
        return card;
    }

    private static Rectangle inset(Rectangle bounds, int margin) {
        int w = Math.max(1, bounds.width - margin * 2);
        int h = Math.max(1, bounds.height - margin * 2);
        return new Rectangle(bounds.x + margin, bounds.y + margin, w, h);
    }

    private static Rectangle clampInside(Rectangle spot, Rectangle limit) {
        int w = Math.min(spot.width, Math.max(1, limit.width));
        int h = Math.min(spot.height, Math.max(1, limit.height));
        int x = clamp(limit.x, limit.x + limit.width - w, spot.x);
        int y = clamp(limit.y, limit.y + limit.height - h, spot.y);
        return new Rectangle(x, y, w, h);
    }

    private static int clamp(int min, int max, int value) {
        if (max < min) return min;
        return Math.max(min, Math.min(max, value));
    }

}
