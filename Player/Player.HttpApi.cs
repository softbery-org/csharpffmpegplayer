using System.Net;
using System.Text;
using System.Text.Json;

namespace CSharpFFmpeg;

public sealed partial class Player
{
    private HttpListener? _apiListener;
    private Thread? _apiThread;
    private volatile bool _apiRunning;
    private volatile bool _pendingApiSave;
    private volatile bool _pendingApiQuit;
    private bool _apiPublic;

    public void StartHttpApi(int port, bool publicInterface = false)
    {
        if (_apiRunning) return;
        _apiRunning = true;
        _apiPublic = publicInterface;
        _apiThread = new Thread(ApiLoop) { IsBackground = true, Name = "HttpApi" };
        _apiThread.Start(port);
        string host = publicInterface ? "0.0.0.0" : "localhost";
        Console.Error.WriteLine($"[HttpApi] Listening on http://{host}:{port}/");
    }

    public void StopHttpApi()
    {
        _apiRunning = false;
        try { _apiListener?.Stop(); } catch { }
        _apiThread?.Join(1000);
    }

    private void ApiLoop(object? portObj)
    {
        int port = portObj as int? ?? 9876;
        try
        {
            _apiListener = new HttpListener();
            string host = _apiPublic ? "0.0.0.0" : "localhost";
            _apiListener.Prefixes.Add($"http://{host}:{port}/");
            _apiListener.Start();

            while (_apiRunning)
            {
                var ctx = _apiListener.GetContext();
                ThreadPool.QueueUserWorkItem(_ => HandleApiRequest(ctx));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HttpApi] Error: {ex.Message}");
        }
    }

    private void HandleApiRequest(HttpListenerContext ctx)
    {
        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";
            string method = ctx.Request.HttpMethod;

            if (path == "/api/save" && method == "POST")
            {
                _pendingApiSave = true;
                SendJson(ctx, new { success = true, message = "save queued" });
            }
            else if (path == "/api/status" && method == "GET")
            {
                SendJson(ctx, new
                {
                    playing = !_paused,
                    position = GetMasterClock(),
                    duration = _duration,
                    volume = _renderer.Volume,
                    playlistVisible = _renderer.PlaylistPanelVisible,
                    alwaysOnTop = _renderer.AlwaysOnTop,
                    file = Path.GetFileName(_videoPath)
                });
            }
            else if (path == "/api/quit" && method == "POST")
            {
                _pendingApiQuit = true;
                SendJson(ctx, new { success = true, message = "quit queued" });
            }
            else
            {
                ctx.Response.StatusCode = 404;
                SendJson(ctx, new { error = "not found" });
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HttpApi] Request error: {ex.Message}");
            try
            {
                ctx.Response.StatusCode = 500;
                SendJson(ctx, new { error = ex.Message });
            }
            catch { }
        }
    }

    private void SendJson(HttpListenerContext ctx, object obj)
    {
        string json = JsonSerializer.Serialize(obj);
        byte[] buffer = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = buffer.Length;
        ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
        ctx.Response.Close();
    }

    private void ProcessApiRequests()
    {
        if (_pendingApiSave)
        {
            _pendingApiSave = false;
            SaveSession();
            Console.Error.WriteLine("[HttpApi] Session saved via API");
        }
        if (_pendingApiQuit)
        {
            _pendingApiQuit = false;
            SaveSession();
            _running = false;
        }
    }
}
