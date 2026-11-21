using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace ScreenDraw;

/// <summary>
/// Serves a transparent web page for an OBS Browser Source and streams strokes to it live
/// (Server-Sent Events). All public methods are called from the UI thread.
/// </summary>
static class StrokeFeed
{
    public const int Port = 7878;
    public static string Url => $"http://localhost:{Port}/";

    static readonly object gate = new();
    static readonly List<Channel<string>> clients = new();
    static readonly List<List<int[]>> strokes = new();
    static int screenNum = 1, width = 1920, height = 1080;
    static HttpListener? listener;

    public static bool Start()
    {
        var primary = Screen.PrimaryScreen;
        if (primary != null) SetScreen(primary);
        try
        {
            listener = new HttpListener();
            listener.Prefixes.Add(Url);
            listener.Start();
        }
        catch (HttpListenerException)
        {
            listener = null;
            return false;
        }
        _ = AcceptLoop(listener);
        return true;
    }

    // ---------- Called by the overlay ----------

    public static void Reset(Screen screen)
    {
        lock (gate) { SetScreen(screen); strokes.Clear(); Broadcast(ResetMessage()); }
    }

    public static void Clear()
    {
        lock (gate) { strokes.Clear(); Broadcast(ResetMessage()); }
    }

    public static void Begin(Point p)
    {
        lock (gate)
        {
            strokes.Add(new List<int[]> { new[] { p.X, p.Y } });
            Broadcast($"{{\"t\":\"begin\",\"x\":{p.X},\"y\":{p.Y}}}");
        }
    }

    public static void Add(Point p)
    {
        lock (gate)
        {
            if (strokes.Count == 0) return;
            strokes[^1].Add(new[] { p.X, p.Y });
            Broadcast($"{{\"t\":\"pt\",\"x\":{p.X},\"y\":{p.Y}}}");
        }
    }

    public static void Undo()
    {
        lock (gate)
        {
            if (strokes.Count == 0) return;
            strokes.RemoveAt(strokes.Count - 1);
            Broadcast("{\"t\":\"undo\"}");
        }
    }

    // ---------- Internals ----------

    static void SetScreen(Screen s)
    {
        var digits = new string(s.DeviceName.Where(char.IsDigit).ToArray()); // "\\.\DISPLAY2" -> 2
        screenNum = int.TryParse(digits, out int n) ? n : 1;
        width = s.Bounds.Width;
        height = s.Bounds.Height;
    }

    static string ResetMessage() =>
        JsonSerializer.Serialize(new { t = "reset", screen = screenNum, w = width, h = height, strokes });

    static void Broadcast(string msg)
    {
        foreach (var c in clients) c.Writer.TryWrite(msg);
    }

    static async Task AcceptLoop(HttpListener l)
    {
        while (l.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await l.GetContextAsync(); }
            catch { return; }
            _ = Handle(ctx);
        }
    }

    static async Task Handle(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        try
        {
            if (ctx.Request.Url?.AbsolutePath == "/events")
            {
                await StreamEvents(res);
                return;
            }
            var body = Encoding.UTF8.GetBytes(Page);
            res.ContentType = "text/html; charset=utf-8";
            res.Headers["Cache-Control"] = "no-cache";
            res.ContentLength64 = body.Length;
            await res.OutputStream.WriteAsync(body);
        }
        catch { /* client went away */ }
        finally
        {
            try { res.Close(); } catch { }
        }
    }

    static async Task StreamEvents(HttpListenerResponse res)
    {
        res.ContentType = "text/event-stream";
        res.Headers["Cache-Control"] = "no-cache";
        res.SendChunked = true;
        var ch = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        lock (gate)
        {
            ch.Writer.TryWrite(ResetMessage()); // bring a fresh/reconnected page up to date
            clients.Add(ch);
        }
        try
        {
            await foreach (var msg in ch.Reader.ReadAllAsync())
            {
                await res.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("data: " + msg + "\n\n"));
                await res.OutputStream.FlushAsync();
            }
        }
        finally
        {
            lock (gate) clients.Remove(ch);
        }
    }

    const string Page = """
<!doctype html>
<html><head><meta charset="utf-8"><title>Screen Draw feed</title>
<style>html,body{margin:0;background:transparent;overflow:hidden}canvas{display:block;width:100vw;height:100vh}</style>
</head><body><canvas id="c"></canvas><script>
const want = new URLSearchParams(location.search).get('screen');
const c = document.getElementById('c'), g = c.getContext('2d');
const COLOR = '#ed1c24', WIDTH = 3;
let strokes = [], active = true;
function setup(w, h) {
  if (c.width !== w || c.height !== h) { c.width = w; c.height = h; }
  g.strokeStyle = g.fillStyle = COLOR; g.lineWidth = WIDTH; g.lineCap = g.lineJoin = 'round';
}
function seg(a, b) { g.beginPath(); g.moveTo(a[0], a[1]); g.lineTo(b[0], b[1]); g.stroke(); }
function dot(p) { g.beginPath(); g.arc(p[0], p[1], WIDTH / 2, 0, Math.PI * 2); g.fill(); }
function drawStroke(s) {
  if (s.length === 1) { dot(s[0]); return; }
  g.beginPath(); g.moveTo(s[0][0], s[0][1]);
  for (let i = 1; i < s.length; i++) g.lineTo(s[i][0], s[i][1]);
  g.stroke();
}
function redraw() { g.clearRect(0, 0, c.width, c.height); if (active) strokes.forEach(drawStroke); }
setup(1920, 1080);
const es = new EventSource('/events');
es.onmessage = e => {
  const m = JSON.parse(e.data);
  if (m.t === 'reset') { active = !want || String(m.screen) === want; setup(m.w, m.h); strokes = m.strokes; redraw(); }
  else if (m.t === 'begin') { strokes.push([[m.x, m.y]]); if (active) dot([m.x, m.y]); }
  else if (m.t === 'pt') { const s = strokes[strokes.length - 1]; if (!s) return; const a = s[s.length - 1]; s.push([m.x, m.y]); if (active) seg(a, [m.x, m.y]); }
  else if (m.t === 'undo') { strokes.pop(); redraw(); }
};
es.onerror = () => { strokes = []; redraw(); }; // app closed: wipe; EventSource reconnects by itself
</script></body></html>
""";
}
