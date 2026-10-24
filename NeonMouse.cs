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
    [STAThread]
    private static void Main(string[] args)
    {
        try { Native.SetProcessDpiAwareness(2); }
        catch { Native.SetProcessDPIAware(); }
        if (args.Length == 2 && args[0] == "--preview")
        {
            using (Bitmap image = TrailForm.Preview()) image.Save(args[1], ImageFormat.Png);
            return;
        }
        bool created;
        using (Mutex instance = new Mutex(true, "Local\\NeonMouse.Desktop.Trail.v1", out created))
        {
            if (!created) return;
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
    public Sample(float x, float y, double time) { X = x; Y = y; Time = time; }
}

internal sealed class TrailForm : Form
{
    // Tune these values, then compile again if you want a different default.
    private const double Lifetime = 0.58;
    private const float StrokeWidth = 4.2f;
    private readonly List<Sample> points = new List<Sample>();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    private NotifyIcon tray;
    private bool paused;
    private Point previous;
    private double previousTime;
    private bool wasVisible;
    private bool selectionHeld;

    public TrailForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(-32000, -32000, 1, 1);
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        var menu = new ContextMenuStrip();
        var pause = new ToolStripMenuItem("暂停 / 继续");
        pause.CheckOnClick = true;
        pause.CheckedChanged += delegate { paused = pause.Checked; points.Clear(); previous = Cursor.Position; };
        menu.Items.Add(pause);
        menu.Items.Add("退出荧光拖尾", null, delegate { Close(); });
        tray = new NotifyIcon();
        tray.Icon = SystemIcons.Application;
        tray.Text = "荧光鼠标拖尾 — 右键暂停或退出";
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        previous = Cursor.Position;
        timer.Interval = 16;
        timer.Tick += TickTrail;
        Shown += delegate { timer.Start(); };
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
        double now = clock.Elapsed.TotalSeconds;
        Point pos = Cursor.Position;
        // Respect swapped mouse buttons. Clear old light as soon as a selection
        // starts, and reset the origin on release to avoid a line across the selection.
        int primaryButton = Native.GetSystemMetrics(23) != 0 ? 0x02 : 0x01;
        bool primaryDown = (Native.GetAsyncKeyState(primaryButton) & 0x8000) != 0;
        if (primaryDown || selectionHeld)
        {
            selectionHeld = primaryDown;
            points.Clear();
            previous = pos;
            previousTime = now;
            if (wasVisible) { Native.ShowWindow(Handle, 0); wasVisible = false; }
            return;
        }
        points.RemoveAll(delegate(Sample s) { return now - s.Time >= Lifetime; });
        if (!paused && pos != previous)
        {
            double distance = Math.Sqrt((double)(pos.X - previous.X) * (pos.X - previous.X) +
                                        (double)(pos.Y - previous.Y) * (pos.Y - previous.Y));
            // Treat cursor warps as a new stroke; do not draw across the entire desktop.
            if (distance > 1800) points.Clear();
            if (points.Count == 0) points.Add(new Sample(previous.X, previous.Y, now - 0.016));
            int steps = Math.Max(1, Math.Min(100, (int)(distance / 5)));
            double start = Math.Max(previousTime, now - 0.040);
            for (int i = 1; i <= steps; ++i)
            {
                float f = (float)i / steps;
                points.Add(new Sample(previous.X + (pos.X - previous.X) * f,
                                      previous.Y + (pos.Y - previous.Y) * f, start + (now - start) * f));
            }
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
        const int margin = 24;
        Rectangle region = Rectangle.FromLTRB((int)Math.Floor(minX) - margin, (int)Math.Floor(minY) - margin,
                                              (int)Math.Ceiling(maxX) + margin, (int)Math.Ceiling(maxY) + margin);
        using (Bitmap bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bitmap)) Draw(g, points, now, region.X, region.Y);
            Present(bitmap, region.Location);
        }
        if (!wasVisible)
        {
            Native.ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE
            wasVisible = true;
        }
    }

    private static void Draw(Graphics g, List<Sample> samples, double now, float ox, float oy)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingQuality = CompositingQuality.HighQuality;
        // Broad faint light, soft halo, coloured body, then a fine bright core.
        float[] scales = { 5.0f, 2.8f, 1.0f, 0.32f };
        int[] opacity = { 12, 28, 160, 235 };
        for (int layer = 0; layer < scales.Length; ++layer)
        {
            for (int i = 1; i < samples.Count; ++i)
            {
                Sample a = samples[i - 1], b = samples[i];
                float life = (float)Math.Max(0, Math.Min(1, 1 - (now - b.Time) / Lifetime));
                if (life <= 0) continue;
                Color color = Hue(now * 0.055 - (now - b.Time) * 0.35);
                if (layer == 3)
                    color = Color.FromArgb((color.R + 510) / 3, (color.G + 510) / 3, (color.B + 510) / 3);
                using (Pen pen = new Pen(Color.FromArgb((int)(opacity[layer] * life * life), color),
                                         Math.Max(0.25f, StrokeWidth * scales[layer] * life)))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, a.X - ox, a.Y - oy, b.X - ox, b.Y - oy);
                }
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
            if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
        }
        base.Dispose(disposing);
    }
    internal static Bitmap Preview()
    {
        Bitmap bitmap = new Bitmap(780, 280);
        var samples = new List<Sample>();
        for (int i = 0; i <= 160; ++i)
        {
            float f = i / 160f;
            samples.Add(new Sample(60 + f * 650, 140 + (float)Math.Sin(f * Math.PI * 2) * 60,
                                   10 - Lifetime * (1 - f) * 0.98));
        }
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.FromArgb(15, 19, 30));
            Draw(g, samples, 10, 0, 0);
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
