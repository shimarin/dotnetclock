using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace dotnetclock;

public partial class Form1 : Form
{
    #region P/Invoke

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage,
        out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public int biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public int bmiColors; // 32bpp BI_RGB はカラーテーブル不要
    }

    private const uint ULW_ALPHA = 2;

    #endregion

    private readonly System.Windows.Forms.Timer _timer;
    private Point _dragStart;
    private bool _dragging;
    private bool _resizing;
    private Point _resizeStartScreen;
    private int   _resizeStartSize;

    private const int ResizeHandleSize = 22;

    // WS_EX_LAYERED を付与してレイヤードウィンドウにする
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80000; return cp; }
    }

    public Form1()
    {
        InitializeComponent();

        _timer = new System.Windows.Forms.Timer { Interval = 50 };
        _timer.Tick += (_, _) => RenderLayeredWindow();
        _timer.Start();

        MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (IsResizeHandle(e.Location))
            {
                _resizing         = true;
                _resizeStartScreen = PointToScreen(e.Location);
                _resizeStartSize  = Width; // 常に正方形なので Width == Height
            }
            else
            {
                _dragging  = true;
                _dragStart = e.Location;
            }
        };
        MouseMove += (_, e) =>
        {
            if (_resizing)
            {
                var cur     = PointToScreen(e.Location);
                int delta   = Math.Max(cur.X - _resizeStartScreen.X,
                                       cur.Y - _resizeStartScreen.Y);
                int newSize = Math.Max(150, _resizeStartSize + delta);
                Size = new Size(newSize, newSize);
            }
            else if (_dragging)
            {
                Location = new Point(Left + e.X - _dragStart.X, Top + e.Y - _dragStart.Y);
            }
            else
            {
                // カーソルでリサイズハンドルを示す
                Cursor = IsResizeHandle(e.Location) ? Cursors.SizeNWSE : Cursors.Default;
            }
        };
        MouseUp += (_, _) => { _dragging = false; _resizing = false; };

        var menu = new ContextMenuStrip();
        menu.Items.Add("閉じる", null, (_, _) => Close());
        ContextMenuStrip = menu;
    }

    private bool IsResizeHandle(Point p) =>
        p.X >= Width - ResizeHandleSize && p.Y >= Height - ResizeHandleSize;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RenderLayeredWindow();
    }

    protected override void OnPaint(PaintEventArgs e) { /* レイヤードウィンドウでは使わない */ }

    protected override void OnResize(EventArgs e) { base.OnResize(e); RenderLayeredWindow(); }
    protected override void OnMove(EventArgs e)   { base.OnMove(e);   RenderLayeredWindow(); }

    private void RenderLayeredWindow()
    {
        if (!IsHandleCreated || Width <= 0 || Height <= 0) return;

        int w = Width, h = Height;

        // ── 1. GDI+ Bitmap に描画（ストレートアルファ） ──────────────────
        using var gdiBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(gdiBmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var sz     = Math.Min(w, h);
            var center = new PointF(w / 2f, h / 2f);
            var radius = sz / 2f - 10f;

            DrawFace(g, center, radius);
            var now = DateTime.Now;
            DrawDate(g, center, radius, now);
            DrawHourHand(g, center, radius, now);
            DrawMinuteHand(g, center, radius, now);
            DrawSecondHand(g, center, radius, now);

            float dotR = radius * 0.03f;
            using var db = new SolidBrush(Color.FromArgb(200, 160, 160, 160));
            g.FillEllipse(db, center.X - dotR, center.Y - dotR, dotR * 2, dotR * 2);

            DrawResizeGrip(g, w, h);
        }

        // ── 2. ピクセルを取得し、アルファ事前乗算に変換 ─────────────────
        //   UpdateLayeredWindow(AC_SRC_ALPHA) は事前乗算済みアルファを要求する。
        //   GDI+ はストレートアルファなので手動変換が必要。
        var bd = gdiBmp.LockBits(
            new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = Math.Abs(bd.Stride); // 32bpp なので w*4 と一致
        int total  = stride * h;
        var pixels = new byte[total];
        Marshal.Copy(bd.Scan0, pixels, 0, total);
        gdiBmp.UnlockBits(bd);

        for (int i = 0; i < total; i += 4)
        {
            int a = pixels[i + 3];
            pixels[i]     = (byte)(pixels[i]     * a / 255); // B
            pixels[i + 1] = (byte)(pixels[i + 1] * a / 255); // G
            pixels[i + 2] = (byte)(pixels[i + 2] * a / 255); // R
            // [i+3] = alpha はそのまま
        }

        // ── 3. CreateDIBSection で GDI HBITMAP を作成してデータをコピー ──
        var bi = new BITMAPINFO();
        bi.bmiHeader.biSize        = Marshal.SizeOf<BITMAPINFOHEADER>();
        bi.bmiHeader.biWidth       = w;
        bi.bmiHeader.biHeight      = -h; // top-down (GDI+ と同じ向き)
        bi.bmiHeader.biPlanes      = 1;
        bi.bmiHeader.biBitCount    = 32;
        bi.bmiHeader.biCompression = 0; // BI_RGB

        var screenDC = GetDC(IntPtr.Zero);
        var memDC    = CreateCompatibleDC(screenDC);
        var hBitmap  = CreateDIBSection(memDC, ref bi, 0, out IntPtr ppvBits, IntPtr.Zero, 0);
        Marshal.Copy(pixels, 0, ppvBits, total);
        var oldObj = SelectObject(memDC, hBitmap);

        // ── 4. UpdateLayeredWindow でウィンドウに反映 ────────────────────
        var winSize = new SIZE { cx = w, cy = h };
        var srcPt   = new POINT { x = 0, y = 0 };
        var dstPt   = new POINT { x = Left, y = Top };
        var blend   = new BLENDFUNCTION
        {
            BlendOp = 0, BlendFlags = 0,   // AC_SRC_OVER
            SourceConstantAlpha = 255,
            AlphaFormat = 1                 // AC_SRC_ALPHA
        };

        UpdateLayeredWindow(Handle, screenDC, ref dstPt, ref winSize,
                            memDC, ref srcPt, 0, ref blend, ULW_ALPHA);

        SelectObject(memDC, oldObj);
        DeleteObject(hBitmap);
        DeleteDC(memDC);
        ReleaseDC(IntPtr.Zero, screenDC);
    }

    // ── 描画メソッド ─────────────────────────────────────────────────────

    // 右下角にリサイズグリップ（3本の斜め線）を描画
    private static void DrawResizeGrip(Graphics g, int w, int h)
    {
        using var pen = new Pen(Color.FromArgb(140, 200, 200, 200), 1.5f);
        const int margin = 5;
        const int spacing = 5;
        for (int i = 0; i < 3; i++)
        {
            int offset = margin + i * spacing;
            g.DrawLine(pen,
                w - margin,        h - offset,
                w - offset,        h - margin);
        }
    }

    private static void DrawDate(Graphics g, PointF center, float radius, DateTime now)
    {
        var dow = now.DayOfWeek;
        string dowChar = dow switch
        {
            DayOfWeek.Monday    => "月",
            DayOfWeek.Tuesday   => "火",
            DayOfWeek.Wednesday => "水",
            DayOfWeek.Thursday  => "木",
            DayOfWeek.Friday    => "金",
            DayOfWeek.Saturday  => "土",
            DayOfWeek.Sunday    => "日",
            _                   => "?"
        };
        Color dowColor = dow switch
        {
            DayOfWeek.Saturday => Color.FromArgb(220, 110, 160, 255), // 青
            DayOfWeek.Sunday   => Color.FromArgb(220, 255, 90,  90),  // 赤
            _                  => Color.FromArgb(210, 210, 210, 210)
        };

        string datePart = $"{now.Month}月{now.Day}日";
        string dowPart  = $"({dowChar})";

        float fontSize = radius * 0.130f;
        // てっぺんと中心を 2:1 に分ける位置
        float posY = center.Y - radius * 0.33f;

        using var font  = new Font("Meiryo UI", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
        var sf = StringFormat.GenericTypographic;

        SizeF dateSz = g.MeasureString(datePart, font, PointF.Empty, sf);
        SizeF dowSz  = g.MeasureString(dowPart,  font, PointF.Empty, sf);
        float totalW = dateSz.Width + dowSz.Width;

        float x = center.X - totalW / 2f;
        float y = posY - dateSz.Height / 2f;

        using var normalBrush = new SolidBrush(Color.FromArgb(210, 210, 210, 210));
        using var dowBrush    = new SolidBrush(dowColor);

        g.DrawString(datePart, font, normalBrush, new PointF(x,                 y), sf);
        g.DrawString(dowPart,  font, dowBrush,    new PointF(x + dateSz.Width,  y), sf);
    }

    private static void DrawFace(Graphics g, PointF center, float radius)
    {
        // 半透明の暗い背景 (alpha > 0 なのでマウスイベントも受け取れる)
        using var bgBrush = new SolidBrush(Color.FromArgb(180, 20, 20, 20));
        g.FillEllipse(bgBrush, center.X - radius, center.Y - radius, radius * 2, radius * 2);

        using var rimPen = new Pen(Color.FromArgb(220, 220, 220, 220), 3f);
        g.DrawEllipse(rimPen, center.X - radius, center.Y - radius, radius * 2, radius * 2);

        for (int i = 0; i < 60; i++)
        {
            double angle    = i * 6.0 * Math.PI / 180.0;
            bool   isHour   = (i % 5 == 0);
            float  outerR   = radius * 0.92f;
            float  innerR   = isHour ? radius * 0.78f : radius * 0.88f;
            float  w        = isHour ? 3f : 1.2f;
            var    col      = isHour
                ? Color.FromArgb(230, 230, 230, 230)
                : Color.FromArgb(160, 140, 140, 140);

            using var pen = new Pen(col, w);
            g.DrawLine(pen, AngleToPoint(center, outerR, angle),
                            AngleToPoint(center, innerR, angle));
        }

        using var font = new Font("Segoe UI", radius * 0.10f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var tb   = new SolidBrush(Color.FromArgb(220, 220, 220, 220));
        var sf = new StringFormat
        {
            Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center
        };
        for (int h = 1; h <= 12; h++)
        {
            double angle = (h * 30 - 90) * Math.PI / 180.0;
            g.DrawString(h.ToString(), font, tb,
                         AngleToPoint(center, radius * 0.65f, angle), sf);
        }
    }

    private static void DrawHourHand(Graphics g, PointF center, float radius, DateTime t)
    {
        double angle = ((t.Hour % 12) * 30 + t.Minute * 0.5 + t.Second / 120.0 - 90) * Math.PI / 180.0;
        DrawHand(g, center, angle, radius * 0.50f, radius * 0.045f, Color.FromArgb(230, 220, 220, 220));
    }

    private static void DrawMinuteHand(Graphics g, PointF center, float radius, DateTime t)
    {
        double angle = (t.Minute * 6 + t.Second * 0.1 - 90) * Math.PI / 180.0;
        DrawHand(g, center, angle, radius * 0.72f, radius * 0.030f, Color.FromArgb(220, 200, 200, 200));
    }

    private static void DrawSecondHand(Graphics g, PointF center, float radius, DateTime t)
    {
        double angle = (t.Second * 6 + t.Millisecond * 0.006 - 90) * Math.PI / 180.0;
        var tip  = AngleToPoint(center,  radius * 0.82f, angle);
        var tail = AngleToPoint(center, -radius * 0.20f, angle);

        using var pen = new Pen(Color.FromArgb(230, 220, 60, 60), radius * 0.018f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap   = System.Drawing.Drawing2D.LineCap.Round
        };
        g.DrawLine(pen, tail, tip);

        float dr = radius * 0.04f;
        using var db = new SolidBrush(Color.FromArgb(230, 220, 60, 60));
        g.FillEllipse(db, center.X - dr, center.Y - dr, dr * 2, dr * 2);
    }

    private static void DrawHand(Graphics g, PointF center, double angle,
                                  float length, float width, Color color)
    {
        double perp = angle + Math.PI / 2;
        float  hw   = width / 2f;
        var    tip  = AngleToPoint(center,  length,       angle);
        var    tail = AngleToPoint(center, -width * 1.5f, angle);

        PointF[] poly =
        [
            new PointF(center.X + hw * (float)Math.Cos(perp), center.Y + hw * (float)Math.Sin(perp)),
            new PointF(center.X - hw * (float)Math.Cos(perp), center.Y - hw * (float)Math.Sin(perp)),
            new PointF(tail.X   - hw * (float)Math.Cos(perp), tail.Y   - hw * (float)Math.Sin(perp)),
            tip,
            new PointF(tail.X   + hw * (float)Math.Cos(perp), tail.Y   + hw * (float)Math.Sin(perp)),
        ];

        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, poly);
    }

    private static PointF AngleToPoint(PointF center, float r, double angle) =>
        new(center.X + r * (float)Math.Cos(angle),
            center.Y + r * (float)Math.Sin(angle));
}
