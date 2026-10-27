using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;

public sealed class TrailSettings
{
    public bool Enabled = true;
    public bool HideWhileSelecting = true;
    public double Lifetime = 0.48;
    public float StrokeWidth = 4.2f;
    public float Brightness = 1f;
    public float Glow = 1f;
    public double FadePower = 2;
    public bool Rainbow = true;
    public double ColorCycle = 18;
    public int FixedColor = unchecked((int)0xff50d8ff);
    public int FrameInterval = 16;
    public int TrailShape = 0;

    public void Normalize()
    {
        Lifetime = Clamp(Lifetime, 0.1, 1.5, 0.48);
        StrokeWidth = (float)Clamp(StrokeWidth, 1, 12, 4.2);
        Brightness = (float)Clamp(Brightness, 0.1, 1, 1);
        Glow = (float)Clamp(Glow, 0, 2, 1);
        FadePower = Clamp(FadePower, 1, 4, 2);
        ColorCycle = Clamp(ColorCycle, 4, 60, 18);
        FrameInterval = FrameInterval == 33 ? 33 : 16;
        TrailShape = TrailShape == 1 ? 1 : 0;
        FixedColor = Color.FromArgb(255, Color.FromArgb(FixedColor)).ToArgb();
    }
    private static double Clamp(double n, double min, double max, double fallback)
    { return double.IsNaN(n) || double.IsInfinity(n) ? fallback : Math.Max(min, Math.Min(max, n)); }

    public void CopyFrom(TrailSettings source)
    {
        Enabled = source.Enabled; HideWhileSelecting = source.HideWhileSelecting;
        Lifetime = source.Lifetime; StrokeWidth = source.StrokeWidth;
        Brightness = source.Brightness; Glow = source.Glow; FadePower = source.FadePower;
        Rainbow = source.Rainbow; ColorCycle = source.ColorCycle;
        FixedColor = source.FixedColor; FrameInterval = source.FrameInterval;
        TrailShape = source.TrailShape;
    }
}

internal static class SettingsStore
{
    internal static string OverridePath { get; set; }
    internal static string FilePath
    { get { return OverridePath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.xml"); } }
    internal static string LoadWarning;

    internal static TrailSettings Load()
    {
        LoadWarning = null;
        try
        {
            if (!File.Exists(FilePath)) return new TrailSettings();
            XmlReaderSettings safe = new XmlReaderSettings();
            safe.DtdProcessing = DtdProcessing.Prohibit;
            safe.XmlResolver = null;
            using (XmlReader reader = XmlReader.Create(FilePath, safe))
            {
                TrailSettings settings = (TrailSettings)new XmlSerializer(typeof(TrailSettings)).Deserialize(reader);
                settings.Normalize();
                return settings;
            }
        }
        catch (Exception ex)
        {
            LoadWarning = "未能读取旧设置，已使用默认值。" + ex.GetBaseException().Message;
            return new TrailSettings();
        }
    }
    internal static bool Save(TrailSettings settings, out string error)
    {
        string temp = FilePath + ".tmp";
        try
        {
            settings.Normalize();
            using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                new XmlSerializer(typeof(TrailSettings)).Serialize(stream, settings);
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
            else File.Move(temp, FilePath);
            error = null;
            return true;
        }
        catch (Exception ex) { error = "设置保存失败：" + ex.GetBaseException().Message; return false; }
    }
}

internal sealed class SettingsForm : Form
{
    private readonly TrailSettings settings;
    private readonly Action apply;
    private readonly Action quit;
    private bool loading;
    private readonly Dictionary<string, TrackBar> sliders = new Dictionary<string, TrackBar>();
    private readonly Dictionary<string, Label> values = new Dictionary<string, Label>();
    private readonly Dictionary<string, Label> captions = new Dictionary<string, Label>();
    private CheckBox enabledBox, selectionBox;
    private ComboBox colorMode, frameRate, trailShape;
    private Button colorButton;
    private Label status;
    private TrailPreview preview;
    private readonly System.Windows.Forms.Timer saveTimer = new System.Windows.Forms.Timer();
    private static readonly Color Background = Color.FromArgb(17, 22, 33);
    private static readonly Color Card = Color.FromArgb(25, 32, 46);
    private static readonly Color Foreground = Color.FromArgb(230, 237, 247);
    private static readonly Color Muted = Color.FromArgb(153, 168, 190);
    private static readonly Color Accent = Color.FromArgb(92, 220, 224);

    internal SettingsForm(TrailSettings options, Action onChanged, Action onExit)
    {
        settings = options; apply = onChanged; quit = onExit;
        Text = "NeonMouse · 荧光拖尾设置";
        Icon = Program.AppIcon;
        Name = "NeonMouseSettings";
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = Background; ForeColor = Foreground;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(720, 780);
        MinimumSize = new Size(640, 600);
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;

        var root = new TableLayoutPanel();
        root.Dock = DockStyle.Fill; root.Padding = new Padding(24, 18, 24, 16);
        root.ColumnCount = 1; root.RowCount = 5;
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 148));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        Controls.Add(root);

        var header = new Panel { Dock = DockStyle.Fill };
        var title = TextLabel("NeonMouse", Foreground, 21f);
        title.Location = new Point(0, 0); title.Size = new Size(300, 35);
        var subtitle = TextLabel("让每一次移动，都留下一道光。", Muted, 9f);
        subtitle.Location = new Point(2, 38); subtitle.Size = new Size(450, 22);
        header.Controls.Add(title); header.Controls.Add(subtitle);
        root.Controls.Add(header, 0, 0);

        var previewCard = new Panel { Dock = DockStyle.Fill, BackColor = Card, Margin = new Padding(0, 5, 0, 12) };
        var previewTitle = TextLabel("实时预览  /  参数会同时应用到桌面", Muted, 9f);
        previewTitle.Dock = DockStyle.Top; previewTitle.Height = 30; previewTitle.Padding = new Padding(12, 6, 0, 0);
        preview = new TrailPreview(settings) { Dock = DockStyle.Fill, BackColor = Card };
        previewCard.Controls.Add(preview); previewCard.Controls.Add(previewTitle);
        root.Controls.Add(previewCard, 0, 1);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        var controls = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(0, 0, 12, 0) };
        controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(controls); root.Controls.Add(scroll, 0, 2);

        var switches = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, WrapContents = false, Margin = Padding.Empty };
        enabledBox = new CheckBox { Name = "Enabled", Text = "启用荧光拖尾", AutoSize = true, Margin = new Padding(0, 8, 24, 0) };
        selectionBox = new CheckBox { Name = "HideWhileSelecting", Text = "拖选时隐藏（避免遮住文字）", AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        enabledBox.CheckedChanged += Changed; selectionBox.CheckedChanged += Changed;
        switches.Controls.Add(enabledBox); switches.Controls.Add(selectionBox); controls.Controls.Add(switches);

        var shapeRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 43, WrapContents = false, Margin = Padding.Empty };
        var shapeLabel = TextLabel("拖尾样式", Foreground, 9f);
        shapeLabel.Size = new Size(104, 30); shapeLabel.TextAlign = ContentAlignment.MiddleLeft;
        trailShape = Combo("TrailShape", 172);
        trailShape.Items.AddRange(new object[] { "经典荧光线条", "心动爱心" });
        trailShape.SelectedIndexChanged += Changed;
        var shapeHint = TextLabel("心形随轨迹缩小、淡出", Muted, 8.5f);
        shapeHint.Size = new Size(230, 30); shapeHint.TextAlign = ContentAlignment.MiddleLeft;
        shapeRow.Controls.Add(shapeLabel); shapeRow.Controls.Add(trailShape); shapeRow.Controls.Add(shapeHint);
        controls.Controls.Add(shapeRow);

        AddSlider(controls, "Lifetime", "拖尾时长", 10, 150);
        AddSlider(controls, "StrokeWidth", "轨迹粗细", 10, 120);
        AddSlider(controls, "Brightness", "整体亮度", 10, 100);
        AddSlider(controls, "Glow", "柔光范围", 0, 200);
        AddSlider(controls, "FadePower", "淡出强度", 10, 40);
        AddSlider(controls, "ColorCycle", "变色周期", 4, 60);

        var colorRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 43, WrapContents = false, Margin = new Padding(0, 8, 0, 0) };
        var colorLabel = TextLabel("颜色模式", Foreground, 9f);
        colorLabel.Size = new Size(104, 30); colorLabel.TextAlign = ContentAlignment.MiddleLeft;
        colorMode = Combo("ColorMode", 172); colorMode.Items.AddRange(new object[] { "流动彩虹", "固定颜色" });
        colorButton = MakeButton("选择颜色", 136); colorButton.Name = "ChooseColor";
        colorButton.Click += delegate
        {
            using (ColorDialog dialog = new ColorDialog())
            {
                dialog.Color = Color.FromArgb(settings.FixedColor); dialog.FullOpen = true;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                { settings.FixedColor = dialog.Color.ToArgb(); ReloadColor(); Changed(this, EventArgs.Empty); }
            }
        };
        colorMode.SelectedIndexChanged += Changed;
        colorRow.Controls.Add(colorLabel); colorRow.Controls.Add(colorMode); colorRow.Controls.Add(colorButton);
        controls.Controls.Add(colorRow);

        var fpsRow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 43, WrapContents = false, Margin = Padding.Empty };
        var fpsLabel = TextLabel("刷新模式", Foreground, 9f);
        fpsLabel.Size = new Size(104, 30); fpsLabel.TextAlign = ContentAlignment.MiddleLeft;
        frameRate = Combo("FrameRate", 172); frameRate.Items.AddRange(new object[] { "流畅 · 约 60 FPS", "省电 · 约 30 FPS" });
        frameRate.SelectedIndexChanged += Changed;
        fpsRow.Controls.Add(fpsLabel); fpsRow.Controls.Add(frameRate); controls.Controls.Add(fpsRow);

        status = TextLabel("", Muted, 8.5f); status.Dock = DockStyle.Fill;
        status.TextAlign = ContentAlignment.MiddleLeft; status.AutoEllipsis = true;
        root.Controls.Add(status, 0, 3);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        var reset = MakeButton("恢复默认", 108); reset.Name = "ResetDefaults";
        reset.Click += delegate { settings.CopyFrom(new TrailSettings()); Reload(); CommitChanges(); };
        var save = MakeButton("保存设置", 108); save.Name = "SaveSettings";
        save.BackColor = Color.FromArgb(37, 97, 108); save.Click += delegate { SaveNow(); };
        var hide = MakeButton("隐藏到托盘", 130); hide.Name = "HideToTray";
        hide.Click += delegate { SaveNow(); Hide(); };
        var exit = MakeButton("退出程序", 108); exit.Name = "ExitApplication";
        exit.Click += delegate { SaveNow(); quit(); };
        actions.Controls.Add(reset); actions.Controls.Add(save); actions.Controls.Add(hide); actions.Controls.Add(exit);
        root.Controls.Add(actions, 0, 4);

        saveTimer.Interval = 600;
        saveTimer.Tick += delegate { SaveNow(); };
        VisibleChanged += delegate { preview.Animate = Visible; };
        FormClosing += delegate(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            { e.Cancel = true; SaveNow(); Hide(); }
        };
        Reload();
        status.Text = SettingsStore.LoadWarning ?? "调整即时生效并自动保存；关闭面板后仍在托盘运行。";
    }

    private static Label TextLabel(string text, Color color, float size)
    { return new Label { Text = text, ForeColor = color, Font = new Font("Microsoft YaHei UI", size), Margin = Padding.Empty }; }
    private static ComboBox Combo(string name, int width)
    { return new ComboBox { Name = name, Width = width, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Card, ForeColor = Foreground, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 3, 14, 0) }; }
    private static Button MakeButton(string text, int width)
    {
        var button = new Button { Text = text, Width = width, Height = 34, FlatStyle = FlatStyle.Flat, BackColor = Card,
                                  ForeColor = Foreground, Margin = new Padding(0, 5, 12, 0), Cursor = Cursors.Hand };
        button.FlatAppearance.BorderColor = Color.FromArgb(56, 73, 94);
        return button;
    }
    private void AddSlider(TableLayoutPanel parent, string name, string caption, int min, int max)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Top, Height = 47, ColumnCount = 3, Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        var label = TextLabel(caption, Foreground, 9f); label.Dock = DockStyle.Fill; label.TextAlign = ContentAlignment.MiddleLeft;
        var slider = new TrackBar { Name = name, Minimum = min, Maximum = max, TickStyle = TickStyle.None, Dock = DockStyle.Fill,
                                    AutoSize = false, Height = 36, BackColor = Background, Margin = new Padding(0, 8, 6, 0),
                                    SmallChange = 1, LargeChange = Math.Max(1, (max - min) / 10), AccessibleName = caption };
        var value = TextLabel("", Accent, 9f); value.Dock = DockStyle.Fill; value.TextAlign = ContentAlignment.MiddleRight;
        slider.ValueChanged += Changed;
        sliders.Add(name, slider); values.Add(name, value); captions.Add(name, label);
        row.Controls.Add(label, 0, 0); row.Controls.Add(slider, 1, 0); row.Controls.Add(value, 2, 0);
        parent.Controls.Add(row);
    }
    internal void Reload()
    {
        loading = true;
        try
        {
            enabledBox.Checked = settings.Enabled; selectionBox.Checked = settings.HideWhileSelecting;
            sliders["Lifetime"].Value = (int)Math.Round(settings.Lifetime * 100);
            sliders["StrokeWidth"].Value = (int)Math.Round(settings.StrokeWidth * 10);
            sliders["Brightness"].Value = (int)Math.Round(settings.Brightness * 100);
            sliders["Glow"].Value = (int)Math.Round(settings.Glow * 100);
            sliders["FadePower"].Value = (int)Math.Round(settings.FadePower * 10);
            sliders["ColorCycle"].Value = (int)Math.Round(settings.ColorCycle);
            colorMode.SelectedIndex = settings.Rainbow ? 0 : 1;
            frameRate.SelectedIndex = settings.FrameInterval == 33 ? 1 : 0;
            trailShape.SelectedIndex = settings.TrailShape;
            ReloadColor(); UpdateValues(); preview.Invalidate();
        }
        finally { loading = false; }
    }
    private void ReloadColor()
    {
        Color color = Color.FromArgb(settings.FixedColor);
        colorButton.Text = "颜色  #" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");
        colorButton.ForeColor = color;
    }
    private void Changed(object sender, EventArgs e)
    {
        if (loading) return;
        settings.Enabled = enabledBox.Checked; settings.HideWhileSelecting = selectionBox.Checked;
        settings.Lifetime = sliders["Lifetime"].Value / 100.0;
        settings.StrokeWidth = sliders["StrokeWidth"].Value / 10f;
        settings.Brightness = sliders["Brightness"].Value / 100f;
        settings.Glow = sliders["Glow"].Value / 100f;
        settings.FadePower = sliders["FadePower"].Value / 10.0;
        settings.ColorCycle = sliders["ColorCycle"].Value;
        settings.Rainbow = colorMode.SelectedIndex == 0;
        settings.FrameInterval = frameRate.SelectedIndex == 1 ? 33 : 16;
        settings.TrailShape = trailShape.SelectedIndex == 1 ? 1 : 0;
        UpdateValues(); CommitChanges();
    }
    private void CommitChanges()
    {
        apply(); preview.Invalidate(); status.Text = "已应用到桌面，正在保存…";
        saveTimer.Stop(); saveTimer.Start();
    }
    private void UpdateValues()
    {
        captions["StrokeWidth"].Text = settings.TrailShape == 1 ? "爱心大小" : "轨迹粗细";
        values["Lifetime"].Text = settings.Lifetime.ToString("0.00") + " 秒";
        values["StrokeWidth"].Text = settings.TrailShape == 1 ? (8 + settings.StrokeWidth * 2.2f).ToString("0") + " px" : settings.StrokeWidth.ToString("0.0") + " px";
        values["Brightness"].Text = Math.Round(settings.Brightness * 100) + "%";
        values["Glow"].Text = Math.Round(settings.Glow * 100) + "%";
        values["FadePower"].Text = settings.FadePower.ToString("0.0") + " ×";
        values["ColorCycle"].Text = settings.ColorCycle.ToString("0") + " 秒";
        sliders["ColorCycle"].Enabled = settings.Rainbow;
        colorButton.Enabled = !settings.Rainbow;
    }
    internal void SaveNow()
    {
        saveTimer.Stop(); string error;
        bool success = SettingsStore.Save(settings, out error);
        status.Text = success ? "设置已保存 · 下次启动自动恢复" : error;
        status.ForeColor = success ? Muted : Color.Salmon;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) saveTimer.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class TrailPreview : Panel
{
    private readonly TrailSettings settings;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    internal bool Animate { set { timer.Enabled = value; } }
    internal TrailPreview(TrailSettings options)
    {
        settings = options; DoubleBuffered = true;
        timer.Interval = 33; timer.Tick += delegate { Invalidate(); };
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using (Pen grid = new Pen(Color.FromArgb(32, 85, 105, 130)))
        {
            for (int x = 0; x < Width; x += 32) g.DrawLine(grid, x, 0, x, Height);
            for (int y = 0; y < Height; y += 32) g.DrawLine(grid, 0, y, Width, y);
        }
        if (!settings.Enabled)
        {
            TextRenderer.DrawText(g, "效果已暂停 · 勾选「启用荧光拖尾」恢复", Font, ClientRectangle,
                                  Color.FromArgb(153, 168, 190), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        double now = clock.Elapsed.TotalSeconds + 10;
        List<Sample> samples = new List<Sample>();
        for (int i = 0; i <= 100; ++i)
        {
            double t = now - settings.Lifetime * (1 - i / 100.0);
            double phase = t * 2.0;
            samples.Add(new Sample(Width / 2f + (float)Math.Sin(phase) * Math.Max(20, Width / 2f - 45),
                                   Height / 2f + (float)Math.Sin(phase * 2) * Math.Max(10, Height / 2f - 23), t));
        }
        TrailForm.Draw(g, samples, now, 0, 0, settings);
        Sample head = samples[samples.Count - 1];
        using (SolidBrush brush = new SolidBrush(Color.FromArgb(245, 250, 255)))
            g.FillEllipse(brush, head.X - 3, head.Y - 3, 6, 6);
    }
    protected override void Dispose(bool disposing)
    { if (disposing) timer.Dispose(); base.Dispose(disposing); }
}
