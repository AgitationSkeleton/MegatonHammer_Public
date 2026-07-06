using System.IO.Pipes;
using System.Text;

namespace MegatonHammer.Editor;

/// <summary>
/// Minimal Discord Rich Presence client, spoken directly over the local Discord IPC named pipe
/// (discord-ipc-0 .. discord-ipc-9) — self-contained, no external package. Everything is best-effort and
/// runs on a background thread: if Discord isn't running, or no Application ID is set, it silently does
/// nothing and keeps retrying, never affecting the editor. The top line of the presence ("Megaton Hammer")
/// is the Discord APPLICATION's name (from the configured App ID); the map + game go in details/state.
/// </summary>
public static class DiscordRpc
{
    private static readonly object _lock = new();
    private static NamedPipeClientStream? _pipe;
    private static Thread? _worker;
    private static volatile bool _running;
    private static string _appId = "";
    private static string? _details, _state, _largeText, _largeImage;
    private static long _startUnix;
    private static bool _dirty;
    private static bool _connected;

    /// <summary>True once the IPC handshake with a running Discord client has succeeded.</summary>
    public static bool Connected => _connected;

    /// <summary>Start (or reconfigure) the presence worker with the given Discord Application (client) ID.</summary>
    public static void Start(string? appId)
    {
        lock (_lock)
        {
            _appId = appId?.Trim() ?? "";
            if (_startUnix == 0) _startUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _dirty = true;
            if (_running) return;
            _running = true;
            _worker = new Thread(Loop) { IsBackground = true, Name = "DiscordRpc" };
            _worker.Start();
        }
    }

    /// <summary>Stop the worker and clear the presence in Discord.</summary>
    public static void Stop()
    {
        lock (_lock) { _running = false; }
        try { if (_connected) { WriteFrame(1, ClearActivityJson()); } } catch { }
        try { _worker?.Join(400); } catch { }
        Disconnect();
    }

    /// <summary>Set the presence: details = "Editing: map", state = "For game", largeImage = an asset key
    /// registered on the Discord app (mh/oot/mm), largeText = its tooltip. Null hides a line.</summary>
    public static void SetPresence(string? details, string? state, string largeImage = "mh", string largeText = "Megaton Hammer")
    {
        lock (_lock)
        {
            if (details == _details && state == _state && largeImage == _largeImage && largeText == _largeText) return;
            _details = details; _state = state; _largeImage = largeImage; _largeText = largeText; _dirty = true;
        }
    }

    private static void Loop()
    {
        while (true)
        {
            string appId; bool run;
            lock (_lock) { run = _running; appId = _appId; }
            if (!run) break;
            if (string.IsNullOrWhiteSpace(appId)) { if (_connected) Disconnect(); Thread.Sleep(1500); continue; }
            try
            {
                if (!_connected && !TryConnect(appId)) { Thread.Sleep(3000); continue; }
                bool dirty; lock (_lock) { dirty = _dirty; _dirty = false; }
                if (dirty) SendActivity();
            }
            catch { Disconnect(); }
            Thread.Sleep(1500);
        }
    }

    private static bool TryConnect(string appId)
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
                pipe.Connect(300);
                _pipe = pipe;
                WriteFrame(0, $"{{\"v\":1,\"client_id\":\"{JsonEscape(appId)}\"}}");   // handshake
                ReadFrame();   // READY (or the pipe closes on a bad app id → throws → next pipe)
                _connected = true;
                lock (_lock) _dirty = true;
                return true;
            }
            catch { try { _pipe?.Dispose(); } catch { } _pipe = null; _connected = false; }
        }
        return false;
    }

    private static void SendActivity()
    {
        WriteFrame(1, BuildActivityJson());
        ReadFrame();   // ack / error frame
    }

    private static string BuildActivityJson()
    {
        string details, state, largeText, largeImage;
        lock (_lock) { details = _details ?? ""; state = _state ?? ""; largeText = _largeText ?? "Megaton Hammer"; largeImage = _largeImage ?? "mh"; }
        var sb = new StringBuilder();
        sb.Append("{\"cmd\":\"SET_ACTIVITY\",\"nonce\":\"").Append(Guid.NewGuid().ToString("N"))
          .Append("\",\"args\":{\"pid\":").Append(Environment.ProcessId).Append(",\"activity\":{");
        bool first = true;
        void Field(string k, string v) { if (!first) sb.Append(','); first = false; sb.Append('"').Append(k).Append("\":\"").Append(JsonEscape(v)).Append('"'); }
        if (details.Length > 0) Field("details", details);
        if (state.Length > 0) Field("state", state);
        if (!first) sb.Append(',');
        sb.Append("\"timestamps\":{\"start\":").Append(_startUnix).Append('}');
        sb.Append(",\"assets\":{\"large_image\":\"").Append(JsonEscape(largeImage))
          .Append("\",\"large_text\":\"").Append(JsonEscape(largeText)).Append("\"}");
        sb.Append("}}}");
        return sb.ToString();
    }

    private static string ClearActivityJson() =>
        $"{{\"cmd\":\"SET_ACTIVITY\",\"nonce\":\"{Guid.NewGuid():N}\",\"args\":{{\"pid\":{Environment.ProcessId},\"activity\":null}}}}";

    private static void WriteFrame(int opcode, string json)
    {
        var p = _pipe ?? throw new InvalidOperationException("no pipe");
        var data = Encoding.UTF8.GetBytes(json);
        var header = new byte[8];
        BitConverter.TryWriteBytes(header.AsSpan(0, 4), opcode);       // little-endian (x86/x64)
        BitConverter.TryWriteBytes(header.AsSpan(4, 4), data.Length);
        p.Write(header, 0, 8);
        p.Write(data, 0, data.Length);
        p.Flush();
    }

    private static void ReadFrame()
    {
        var header = new byte[8];
        ReadExact(header, 8);
        int len = BitConverter.ToInt32(header, 4);
        if (len > 0 && len < (1 << 20)) { var buf = new byte[len]; ReadExact(buf, len); }
    }

    private static void ReadExact(byte[] buf, int count)
    {
        var p = _pipe ?? throw new InvalidOperationException("no pipe");
        int got = 0;
        while (got < count)
        {
            int n = p.Read(buf, got, count - got);
            if (n <= 0) throw new EndOfStreamException();
            got += n;
        }
    }

    private static void Disconnect()
    {
        _connected = false;
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
    }

    private static string JsonEscape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default: if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c); break;
            }
        return sb.ToString();
    }
}
