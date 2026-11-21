using System.Diagnostics;
using System.Drawing.Drawing2D;
using static ScreenDraw.Native;

namespace ScreenDraw;

static class Program
{
    // ---- Hotkeys (change here if they clash with another app) ----
    const uint ToggleMods = MOD_CONTROL | MOD_SHIFT;           // Ctrl+Shift+D  -> start/stop drawing
    const Keys ToggleKey = Keys.D;
    const uint PanicMods = MOD_CONTROL | MOD_ALT | MOD_SHIFT;  // Ctrl+Alt+Shift+Q -> kill the app instantly
    const Keys PanicKey = Keys.Q;

    const int HotkeyToggleId = 1, HotkeyPanicId = 2;

    static Control uiInvoker = null!;
    static OverlayForm? overlay;

    [STAThread]
    static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var mutex = new Mutex(true, @"Local\ScreenDraw-SingleInstance", out bool firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("Screen Draw is already running (see the tray icon).", "Screen Draw");
            return;
        }

        uiInvoker = new Control();
        _ = uiInvoker.Handle; // force handle creation so BeginInvoke works

        // Hotkeys live on their own thread so the panic key still works even if the UI thread hangs.
        string? hotkeyError = null;
        using var ready = new ManualResetEventSlim();
        var hotkeyThread = new Thread(() => HotkeyLoop(ready, e => hotkeyError = e))
        {
            IsBackground = true,
            Name = "Hotkeys",
        };
        hotkeyThread.Start();
        ready.Wait();
        if (hotkeyError != null)
        {
            MessageBox.Show(hotkeyError, "Screen Draw", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        bool feedOk = StrokeFeed.Start();

        using var icon = MakeTrayIcon();
        using var menu = new ContextMenuStrip();
        menu.Items.Add("Draw  (Ctrl+Shift+D)", null, (_, _) => ToggleOverlay());
        menu.Items.Add("Copy OBS Browser Source URL", null, (_, _) => Clipboard.SetText(StrokeFeed.Url));
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());
        using var tray = new NotifyIcon
        {
            Icon = icon,
            Text = "Screen Draw\nCtrl+Shift+D: draw\nCtrl+Alt+Shift+Q: emergency quit",
            ContextMenuStrip = menu,
            Visible = true,
        };
        if (feedOk)
            tray.ShowBalloonTip(3000, "Screen Draw is running",
                $"Ctrl+Shift+D to draw, Esc to stop.\nOBS Browser Source: {StrokeFeed.Url}", ToolTipIcon.None);
        else
            tray.ShowBalloonTip(5000, "Screen Draw: OBS feed unavailable",
                $"Port {StrokeFeed.Port} is in use, so the OBS Browser Source won't update. Drawing still works.",
                ToolTipIcon.Warning);

        Application.Run();
        tray.Visible = false;
    }

    static void HotkeyLoop(ManualResetEventSlim ready, Action<string> fail)
    {
        bool toggleOk = RegisterHotKey(IntPtr.Zero, HotkeyToggleId, ToggleMods | MOD_NOREPEAT, (uint)ToggleKey);
        bool panicOk = RegisterHotKey(IntPtr.Zero, HotkeyPanicId, PanicMods | MOD_NOREPEAT, (uint)PanicKey);
        if (!toggleOk || !panicOk)
        {
            fail((!toggleOk ? "Ctrl+Shift+D" : "Ctrl+Alt+Shift+Q") +
                 " is already used by another program, so Screen Draw can't start.");
            ready.Set();
            return;
        }
        ready.Set();

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message != WM_HOTKEY) continue;
            switch ((int)msg.wParam)
            {
                case HotkeyPanicId:
                    Process.GetCurrentProcess().Kill(); // no cleanup, no waiting: guaranteed exit
                    break;
                case HotkeyToggleId:
                    uiInvoker.BeginInvoke(ToggleOverlay);
                    break;
            }
        }
    }

    static void ToggleOverlay()
    {
        if (overlay != null)
        {
            overlay.Close();
            return;
        }
        var screen = Screen.FromPoint(Cursor.Position);
        var form = new OverlayForm(screen);
        form.FormClosed += (_, _) => overlay = null;
        overlay = form;
        form.Show();
        form.Activate();
    }

    static Icon MakeTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Color.FromArgb(237, 28, 36), 5) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawBezier(pen, 4, 24, 10, 4, 20, 30, 28, 8);
        }
        IntPtr h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }
}
