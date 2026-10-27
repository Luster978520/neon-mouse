using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// Standalone desktop overlay. No hooks, network requests, or startup registration.
internal static class Program
{
    internal static readonly Icon AppIcon = LoadIcon();
    internal static EventWaitHandle SettingsWake;
    private static Icon LoadIcon()
    {
        using (System.IO.Stream stream = typeof(Program).Assembly.GetManifestResourceStream("NeonMouse.App.ico"))
        {
            if (stream == null) return (Icon)SystemIcons.Application.Clone();
            using (Icon icon = new Icon(stream)) return (Icon)icon.Clone();
        }
    }
    [STAThread]
    private static void Main(string[] args)
    {
        try { Native.SetProcessDpiAwareness(2); }
        catch { Native.SetProcessDPIAware(); }
        if (args.Length == 2 && (args[0] == "--preview" || args[0] == "--heart-preview"))
        {
            using (Bitmap image = TrailForm.Preview(args[0] == "--heart-preview")) image.Save(args[1], ImageFormat.Png);
            return;
        }
        bool created;
        using (Mutex instance = new Mutex(true, "Local\\NeonMouse.Desktop.Trail.v1", out created))
        using (EventWaitHandle wake = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\NeonMouse.SettingsWake.v2"))
        {
            if (!created)
            {
                wake.Set();
                return;
            }
            SettingsWake = wake;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrailForm());
        }
    }
}

internal sealed class Sample
{
    public float X, Y;
    public double Time;
    public double Distance;
    public Sample(float x, float y, double time, double distance = -1)
    { X = x; Y = y; Time = time; Distance = distance; }
}

internal sealed class TrailForm : Form
{
    private readonly TrailSettings settings = SettingsStore.Load();
    private SettingsForm settingsWindow;
    private readonly List<Sample> points = new List<Sample>();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    private NotifyIcon tray;
    private ToolStripMenuItem pauseItem;
    private Point previous;
    private double previousTime;
    private bool wasVisible;
    private bool selectionHeld;
    private double travelled;

    public TrailForm()
    {
        Icon = Program.AppIcon;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(-32000, -32000, 1, 1);
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开设置面板", null, delegate { OpenSettings(); });
        pauseItem = new ToolStripMenuItem("暂停效果");
        pauseItem.CheckOnClick = true;
        pauseItem.Checked = !settings.Enabled;
        pauseItem.CheckedChanged += delegate
        {
            settings.Enabled = !pauseItem.Checked;
            ApplySettings();
            if (settingsWindow != null) settingsWindow.Reload();
            string error;
            SettingsStore.Save(settings, out error);
        };
        menu.Items.Add(pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出荧光拖尾", null, delegate { Close(); });
        tray = new NotifyIcon();
        tray.Icon = Program.AppIcon;
        tray.Text = "NeonMouse — 双击打开设置";
        tray.DoubleClick += delegate { OpenSettings(); };
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        previous = Cursor.Position;
        timer.Interval = settings.FrameInterval;
        timer.Tick += TickTrail;
        Shown += delegate { timer.Start(); OpenSettings(); };
    }

    private void OpenSettings()
    {
        if (settingsWindow == null || settingsWindow.IsDisposed)
            settingsWindow = new SettingsForm(settings, ApplySettings, delegate { Close(); });
        settingsWindow.Show();
        settingsWindow.WindowState = FormWindowState.Normal;
        settingsWindow.Activate();
    }

    private void ApplySettings()
    {
        timer.Interval = settings.FrameInterval;
        points.Clear();
        previous = Cursor.Position;
        if (wasVisible) { Native.ShowWindow(Handle, 0); wasVisible = false; }
        if (pauseItem.Checked == settings.Enabled) pauseItem.Checked = !settings.Enabled;
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams p = base.CreateParams;
            // Layered + transparent to input + no activation + tool window.
            p.ExStyle |= 0x80000 | 0x20 | 0x08000000 | 0x80;
            return p;
        }
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x84) { m.Result = new IntPtr(-1); return; }
        if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; }
        base.WndProc(ref m);
    }
    private void TickTrail(object sender, EventArgs e)
    {
        if (Program.SettingsWake != null && Program.SettingsWake.WaitOne(0)) OpenSettings();
        double now = clock.Elapsed.TotalSeconds;
        Point pos = Cursor.Position;
        // Respect swapped mouse buttons. Clear old light as soon as a selection
        // starts, and reset the origin on release to avoid a line across the selection.
        int primaryButton = Native.GetSystemMetrics(23) != 0 ? 0x02 : 0x01;
        bool primaryDown = (Native.GetAsyncKeyState(primaryButton) & 0x8000) != 0;
        if (!settings.Enabled || (settings.HideWhileSelecting && (primaryDown || selectionHeld)))
        {
            selectionHeld = primaryDown;
            points.Clear();
            previous = pos;
            previousTime = now;
            if (wasVisible) { Native.ShowWindow(Handle, 0); wasVisible = false; }
            return;
        }
        selectionHeld = false;
        points.RemoveAll(delegate(Sample s) { return now - s.Time >= settings.Lifetime; });
        if (pos != previous)
        {
            double distance = Math.Sqrt((double)(pos.X - previous.X) * (pos.X - previous.X) +
                                        (double)(pos.Y - previous.Y) * (pos.Y - previous.Y));
            // Treat cursor warps as a new stroke; do not draw across the entire desktop.
            if (distance > 1800)
            {
                points.Clear(); previous = pos; previousTime = now;
                if (wasVisible) { Native.ShowWindow(Handle, 0); wasVisible = false; }
                return;
            }
            if (points.Count == 0)
            {
                travelled = 0;
                points.Add(new Sample(previous.X, previous.Y, now - 0.016, travelled));
            }
            int steps = Math.Max(1, Math.Min(100, (int)(distance / 5)));
            double start = Math.Max(previousTime, now - 0.040);
            for (int i = 1; i <= steps; ++i)
            {
                float f = (float)i / steps;
                points.Add(new Sample(previous.X + (pos.X - previous.X) * f,
                                      previous.Y + (pos.Y - previous.Y) * f, start + (now - start) * f, travelled + distance * f));
            }
            travelled += distance;
        }
        previous = pos;
        previousTime = now;
        if (points.Count > 1600) points.RemoveRange(0, points.Count - 1600);
        if (points.Count < 2)
        {
            if (wasVisible) { Native.ShowWindow(Handle, 0); wasVisible = false; }
            return;
        }
        float minX = points[0].X, maxX = minX, minY = points[0].Y, maxY = minY;
        foreach (Sample p in points)
        {
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }
        int margin = (int)Math.Ceiling(settings.StrokeWidth * (1 + 4 * settings.Glow) / 2) + 5;
        if (settings.TrailShape == 1) margin = (int)Math.Ceiling(8 + settings.StrokeWidth * 2.2f + 10 * settings.Glow) + 16;
        Rectangle region = Rectangle.FromLTRB((int)Math.Floor(minX) - margin, (int)Math.Floor(minY) - margin,
                                              (int)Math.Ceiling(maxX) + margin, (int)Math.Ceiling(maxY) + margin);
        using (Bitmap bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bitmap)) Draw(g, points, now, region.X, region.Y, settings);
            Present(bitmap, region.Location);
        }
        if (!wasVisible)
        {
            Native.ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE
            wasVisible = true;
        }
    }

    internal static void Draw(Graphics g, List<Sample> samples, double now, float ox, float oy, TrailSettings options)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingQuality = CompositingQuality.HighQuality;
        if (options.TrailShape == 1)
        {
            DrawHearts(g, samples, now, ox, oy, options);
            return;
        }
        // Broad faint light, soft halo, coloured body, then a fine bright core.
        float[] scales = { 1 + 4 * options.Glow, 1 + 1.8f * options.Glow, 1.0f, 0.32f };
        int[] opacity = { 12, 28, 160, 235 };
        for (int layer = 0; layer < scales.Length; ++layer)
        {
            if (layer < 2 && options.Glow == 0) continue;
            for (int i = 1; i < samples.Count; ++i)
            {
                Sample a = samples[i - 1], b = samples[i];
                float life = (float)Math.Max(0, Math.Min(1, 1 - (now - b.Time) / options.Lifetime));
                if (life <= 0) continue;
                Color color = options.Rainbow ? Hue(now / options.ColorCycle - (now - b.Time) * 0.35) : Color.FromArgb(options.FixedColor);
                if (layer == 3)
                    color = Color.FromArgb((color.R + 510) / 3, (color.G + 510) / 3, (color.B + 510) / 3);
                using (Pen pen = new Pen(Color.FromArgb((int)(opacity[layer] * options.Brightness * Math.Pow(life, options.FadePower)), color),
                                         Math.Max(0.25f, options.StrokeWidth * scales[layer] * life)))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, a.X - ox, a.Y - oy, b.X - ox, b.Y - oy);
                }
            }
        }
    }
    private static void DrawHearts(Graphics g, List<Sample> samples, double now, float ox, float oy, TrailSettings options)
    {
        float fullSize = 8 + options.StrokeWidth * 2.2f;
        double spacing = Math.Max(12, fullSize * 1.4);
        double fallbackDistance = 0;
        // Anchor particles to travelled distance, not the changing head position.
        // This keeps existing hearts still while new ones appear along movement.
        for (int i = 1; i < samples.Count; i++)
        {
            Sample a = samples[i - 1], b = samples[i];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            double start = a.Distance >= 0 ? a.Distance : fallbackDistance;
            double end = b.Distance >= 0 ? b.Distance : start + length;
            fallbackDistance = end;
            if (end <= start) continue;
            double anchor = (Math.Floor(start / spacing) + 1) * spacing;
            for (; anchor <= end; anchor += spacing)
            {
                double f = (anchor - start) / (end - start);
                double born = a.Time + (b.Time - a.Time) * f;
                double age = Math.Max(0, now - born);
                float life = (float)Math.Max(0, Math.Min(1, 1 - age / options.Lifetime));
                if (life <= 0) continue;
                float size = fullSize * (0.35f + 0.65f * life) * (1 + 0.05f * (float)Math.Sin(age * 12));
                float x = (float)(a.X + dx * f) - ox;
                float y = (float)(a.Y + dy * f) - oy - (float)(age / options.Lifetime) * 7;
                Color color = options.Rainbow ? Hue(now / options.ColorCycle - age * 0.35) : Color.FromArgb(options.FixedColor);
                int alpha = (int)(230 * options.Brightness * Math.Pow(life, options.FadePower));
                DrawHeart(g, x, y, size, (float)Math.Sin(anchor * 0.07) * 12, color, alpha, options.Glow);
            }
        }
    }

    private static void DrawHeart(Graphics g, float x, float y, float size, float rotation, Color color, int alpha, float glow)
    {
        if (alpha <= 0) return;
        using (GraphicsPath heart = new GraphicsPath())
        {
            // Unit heart with a visible notch, rounded lobes, and a tapered tip.
            heart.AddBezier(0, -0.27f, -0.25f, -0.62f, -0.72f, -0.20f, -0.38f, 0.14f);
            heart.AddBezier(-0.38f, 0.14f, -0.25f, 0.29f, -0.08f, 0.41f, 0, 0.52f);
            heart.AddBezier(0, 0.52f, 0.08f, 0.41f, 0.25f, 0.29f, 0.38f, 0.14f);
            heart.AddBezier(0.38f, 0.14f, 0.72f, -0.20f, 0.25f, -0.62f, 0, -0.27f);
            heart.CloseFigure();
            using (Matrix transform = new Matrix())
            {
                transform.Translate(x, y); transform.Rotate(rotation); transform.Scale(size, size);
                heart.Transform(transform);
            }
            if (glow > 0)
            {
                using (Pen outer = new Pen(Color.FromArgb(alpha / 12, color), 2 + glow * 7))
                { outer.LineJoin = LineJoin.Round; g.DrawPath(outer, heart); }
                using (Pen halo = new Pen(Color.FromArgb(alpha / 6, color), 1 + glow * 3))
                { halo.LineJoin = LineJoin.Round; g.DrawPath(halo, heart); }
            }
            Color light = Color.FromArgb(alpha, (color.R + 255) / 2, (color.G + 255) / 2, (color.B + 255) / 2);
            RectangleF bounds = heart.GetBounds();
            using (LinearGradientBrush fill = new LinearGradientBrush(bounds, light, Color.FromArgb(alpha * 3 / 5, color), 65f))
                g.FillPath(fill, heart);
            using (Pen edge = new Pen(Color.FromArgb(alpha, color), Math.Max(0.6f, size * 0.06f)))
                g.DrawPath(edge, heart);
            // A tiny glint beside each heart adds a light, sparkling finish.
            float starX = x + size * 0.65f, starY = y - size * 0.32f;
            using (Pen glint = new Pen(Color.FromArgb(alpha * 2 / 3, light), 0.9f))
            {
                g.DrawLine(glint, starX - size * 0.10f, starY, starX + size * 0.10f, starY);
                g.DrawLine(glint, starX, starY - size * 0.10f, starX, starY + size * 0.10f);
            }
        }
    }

    private static Color Hue(double hue)
    {
        hue = (hue - Math.Floor(hue)) * 6;
        int sector = (int)hue;
        int t = (int)(255 * (hue - sector));
        switch (sector)
        {
            case 0: return Color.FromArgb(255, t, 0);
            case 1: return Color.FromArgb(255 - t, 255, 0);
            case 2: return Color.FromArgb(0, 255, t);
            case 3: return Color.FromArgb(0, 255 - t, 255);
            case 4: return Color.FromArgb(t, 0, 255);
            default: return Color.FromArgb(255, 0, 255 - t);
        }
    }
    private void Present(Bitmap bitmap, Point at)
    {
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        IntPtr memory = Native.CreateCompatibleDC(screen);
        IntPtr hbitmap = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            hbitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            old = Native.SelectObject(memory, hbitmap);
            Native.POINT dest = new Native.POINT(at.X, at.Y), source = new Native.POINT(0, 0);
            Native.SIZE size = new Native.SIZE(bitmap.Width, bitmap.Height);
            Native.BLEND blend = new Native.BLEND();
            blend.SourceConstantAlpha = 255; blend.AlphaFormat = 1;
            if (!Native.UpdateLayeredWindow(Handle, screen, ref dest, ref size, memory, ref source, 0, ref blend, 2))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            if (old != IntPtr.Zero) Native.SelectObject(memory, old);
            if (hbitmap != IntPtr.Zero) Native.DeleteObject(hbitmap);
            Native.DeleteDC(memory);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Stop(); timer.Dispose();
            if (settingsWindow != null) { settingsWindow.SaveNow(); settingsWindow.Dispose(); }
            if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
        }
        base.Dispose(disposing);
    }
    internal static Bitmap Preview(bool hearts = false)
    {
        TrailSettings options = new TrailSettings();
        if (hearts)
        {
            options.TrailShape = 1; options.Rainbow = false;
            options.FixedColor = Color.FromArgb(255, 92, 160).ToArgb(); options.StrokeWidth = 6;
            options.FadePower = 1.4;
        }
        Bitmap bitmap = new Bitmap(780, 280);
        var samples = new List<Sample>();
        for (int i = 0; i <= 160; ++i)
        {
            float f = i / 160f;
            samples.Add(new Sample(60 + f * 650, 140 + (float)Math.Sin(f * Math.PI * 2) * 60,
                                   10 - options.Lifetime * (1 - f) * 0.98));
        }
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.FromArgb(15, 19, 30));
            Draw(g, samples, 10, 0, 0, options);
        }
        return bitmap;
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct POINT
    { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] internal struct SIZE
    { public int CX, CY; public SIZE(int x, int y) { CX = x; CY = y; } }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] internal struct BLEND
    { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [DllImport("shcore.dll")] internal static extern int SetProcessDpiAwareness(int awareness);
    [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr dc, ref POINT destination, ref SIZE size, IntPtr sourceDC,
        ref POINT source, uint key, ref BLEND blend, uint flags);
}
