using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using static ScreenDraw.Native;

namespace ScreenDraw;

/// <summary>
/// A borderless, topmost, per-pixel-alpha window covering one monitor.
/// The background is alpha 1/255 (invisible, but still catches the mouse); strokes are fully opaque.
/// </summary>
sealed class OverlayForm : Form
{
    const float PenWidth = 3f;                                   // MS Paint brush, 2nd smallest size
    static readonly Color PenColor = Color.FromArgb(237, 28, 36); // MS Paint red
    static readonly Color Backdrop = Color.FromArgb(1, 0, 0, 0);

    readonly Rectangle area;
    readonly List<List<Point>> strokes = new();
    List<Point>? current;

    readonly IntPtr memDc, dib, oldBitmap;
    readonly Bitmap canvas;
    readonly Graphics g;
    readonly Pen pen;
    readonly SolidBrush dotBrush;

    public OverlayForm(Screen screen)
    {
        area = screen.Bounds;
        Text = "Screen Draw overlay";
        StrokeFeed.Reset(screen);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Bounds = area;
        Cursor = Cursors.Cross;
        KeyPreview = true;

        // 32-bit premultiplied DIB shared between GDI (for UpdateLayeredWindow) and GDI+ (for drawing).
        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = area.Width,
            biHeight = -area.Height, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr screenDc = GetDC(IntPtr.Zero);
        memDc = CreateCompatibleDC(screenDc);
        dib = CreateDIBSection(screenDc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        ReleaseDC(IntPtr.Zero, screenDc);
        oldBitmap = SelectObject(memDc, dib);

        canvas = new Bitmap(area.Width, area.Height, area.Width * 4, PixelFormat.Format32bppPArgb, bits);
        g = Graphics.FromImage(canvas);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingMode = CompositingMode.SourceOver;
        pen = MakePen();
        dotBrush = new SolidBrush(PenColor);
        g.Clear(Backdrop);
    }

    static Pen MakePen() => new(PenColor, PenWidth)
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round,
        LineJoin = LineJoin.Round,
    };

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Push();
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    // ---------- Drawing ----------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        current = new List<Point> { e.Location };
        strokes.Add(current);
        StrokeFeed.Begin(e.Location);
        DrawDot(g, e.Location);
        Push();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (current == null) return;
        if ((MouseButtons & MouseButtons.Left) == 0) { current = null; return; } // release was missed
        var last = current[^1];
        if (last == e.Location) return;
        current.Add(e.Location);
        StrokeFeed.Add(e.Location);
        g.DrawLine(pen, last, e.Location);
        Push();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) current = null;
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        current = null; // e.g. Alt+Tab mid-stroke
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        current = null;
    }

    void DrawDot(Graphics gr, Point p) =>
        gr.FillEllipse(dotBrush, p.X - PenWidth / 2, p.Y - PenWidth / 2, PenWidth, PenWidth);

    void DrawStrokes(Graphics gr)
    {
        foreach (var s in strokes)
        {
            if (s.Count == 1) DrawDot(gr, s[0]);
            else gr.DrawLines(pen, s.ToArray());
        }
    }

    void Redraw()
    {
        g.Clear(Backdrop);
        DrawStrokes(g);
        Push();
    }

    /// <summary>Copy the canvas to the screen.</summary>
    void Push()
    {
        if (!IsHandleCreated) return;
        g.Flush(FlushIntention.Sync);
        var dst = new POINT { x = area.X, y = area.Y };
        var size = new SIZE { cx = area.Width, cy = area.Height };
        var src = new POINT();
        var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
        UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
    }

    // ---------- Keys ----------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
            Close();
        else if (e.KeyCode == Keys.Z && e.Control && current == null && strokes.Count > 0)
        {
            strokes.RemoveAt(strokes.Count - 1);
            StrokeFeed.Undo();
            Redraw();
        }
        else if (e.KeyCode == Keys.C && e.Control)
            CopyToClipboard();
        else if (e.KeyCode == Keys.C && !e.Alt && !e.Shift && current == null)
        {
            strokes.Clear();
            StrokeFeed.Clear();
            Redraw();
        }
        else return;
        e.Handled = true;
    }

    void CopyToClipboard()
    {
        using var shot = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
        using (var sg = Graphics.FromImage(shot))
        {
            sg.CopyFromScreen(area.Location, Point.Empty, area.Size);
            sg.SmoothingMode = SmoothingMode.AntiAlias;
            DrawStrokes(sg); // in case the capture skipped this layered window
        }
        try { Clipboard.SetImage(shot); }
        catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy; ignore */ }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        StrokeFeed.Clear();
        g.Dispose();
        canvas.Dispose();
        pen.Dispose();
        dotBrush.Dispose();
        SelectObject(memDc, oldBitmap);
        DeleteObject(dib);
        DeleteDC(memDc);
    }
}
