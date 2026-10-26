// Captures whatever is playing on the default output device (WASAPI loopback)
// and serves it as an endless 16-bit PCM WAV stream at http://<host>:9000/stream
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using NAudio.Wave;

const int Port = 9000;

// Optional: AudioServer.exe --password <secret>  (HTTP Basic auth, any username)
string? password = null;
int pwArg = Array.IndexOf(args, "--password");
if (pwArg >= 0 && pwArg + 1 < args.Length)
    password = args[pwArg + 1];

using var capture = new WasapiLoopbackCapture();
var src = capture.WaveFormat;
if (src.BitsPerSample != 32)
{
    Console.Error.WriteLine($"Unsupported capture format: {src}. Expected 32-bit float.");
    return 1;
}

int inChannels = src.Channels;
const int outChannels = 2;
const int outBlockAlign = outChannels * 2;
const float Mix = 0.707f; // -3 dB for center and surround channels
var clients = new ConcurrentDictionary<Channel<byte[]>, byte>();
long lastDataTick = Environment.TickCount64;

static short ToPcm16(float v) => (short)(Math.Clamp(v, -1f, 1f) * short.MaxValue);

void Broadcast(byte[] pcm)
{
    foreach (var ch in clients.Keys)
        ch.Writer.TryWrite(pcm);
}

capture.DataAvailable += (_, e) =>
{
    // Downmix to stereo assuming standard WAVE channel order:
    // FL FR FC LFE then surround pairs (left on even index, right on odd). LFE is dropped.
    int frames = e.BytesRecorded / (4 * inChannels);
    var input = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, frames * 4 * inChannels));
    var pcm = new byte[frames * outBlockAlign];
    for (int f = 0; f < frames; f++)
    {
        var s = input.Slice(f * inChannels, inChannels);
        float l = s[0], r = inChannels > 1 ? s[1] : s[0];
        if (inChannels > 2) { l += Mix * s[2]; r += Mix * s[2]; }
        for (int c = 4; c < inChannels; c++)
        {
            if (c % 2 == 0) l += Mix * s[c]; else r += Mix * s[c];
        }
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(f * 4), ToPcm16(l));
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(f * 4 + 2), ToPcm16(r));
    }
    Interlocked.Exchange(ref lastDataTick, Environment.TickCount64);
    Broadcast(pcm);
};

capture.RecordingStopped += (_, e) =>
{
    Console.Error.WriteLine($"Capture stopped: {e.Exception?.Message ?? "no error"}");
    Environment.Exit(2);
};

// Loopback delivers no data while nothing is playing; fill the gap with silence
// so VLC doesn't stall.
_ = Task.Run(async () =>
{
    long prev = Environment.TickCount64;
    while (true)
    {
        await Task.Delay(20);
        long now = Environment.TickCount64;
        long elapsed = now - prev;
        prev = now;
        if (now - Interlocked.Read(ref lastDataTick) > 100)
            Broadcast(new byte[outBlockAlign * (int)(src.SampleRate * elapsed / 1000)]);
    }
});

byte[] WavHeader()
{
    var h = new byte[44];
    var s = h.AsSpan();
    Encoding.ASCII.GetBytes("RIFF").CopyTo(s);
    BinaryPrimitives.WriteUInt32LittleEndian(s[4..], uint.MaxValue);
    Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(s[8..]);
    BinaryPrimitives.WriteInt32LittleEndian(s[16..], 16);
    BinaryPrimitives.WriteInt16LittleEndian(s[20..], 1); // PCM
    BinaryPrimitives.WriteInt16LittleEndian(s[22..], (short)outChannels);
    BinaryPrimitives.WriteInt32LittleEndian(s[24..], src.SampleRate);
    BinaryPrimitives.WriteInt32LittleEndian(s[28..], src.SampleRate * outBlockAlign);
    BinaryPrimitives.WriteInt16LittleEndian(s[32..], (short)outBlockAlign);
    BinaryPrimitives.WriteInt16LittleEndian(s[34..], 16);
    Encoding.ASCII.GetBytes("data").CopyTo(s[36..]);
    BinaryPrimitives.WriteUInt32LittleEndian(s[40..], uint.MaxValue);
    return h;
}

bool Authorized(string? auth)
{
    if (password == null)
        return true;
    if (auth == null || !auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        return false;
    try
    {
        var cred = Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..].Trim()));
        int colon = cred.IndexOf(':');
        return colon >= 0 && cred[(colon + 1)..] == password;
    }
    catch (FormatException)
    {
        return false;
    }
}

async Task Serve(TcpClient tcp)
{
    using var _ = tcp;
    var remote = tcp.Client.RemoteEndPoint;
    var stream = tcp.GetStream();
    var reader = new StreamReader(stream, Encoding.ASCII);
    var requestLine = await reader.ReadLineAsync() ?? "";
    string? auth = null;
    string? line;
    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
    {
        if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
            auth = line["Authorization:".Length..].Trim();
    }

    var parts = requestLine.Split(' ');
    if (parts.Length < 2 || parts[1] != "/stream")
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        return;
    }

    if (!Authorized(auth))
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"AudioServer\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        return;
    }

    await stream.WriteAsync(Encoding.ASCII.GetBytes(
        "HTTP/1.1 200 OK\r\nContent-Type: audio/wav\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n"));
    await stream.WriteAsync(WavHeader());

    // ~1s of buffered chunks; a slow client loses its oldest audio instead of blocking capture
    var ch = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(100)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    clients.TryAdd(ch, 0);
    Console.WriteLine($"Client connected: {remote}");
    try
    {
        await foreach (var chunk in ch.Reader.ReadAllAsync())
            await stream.WriteAsync(chunk);
    }
    catch (IOException) { }
    finally
    {
        clients.TryRemove(ch, out var _);
        Console.WriteLine($"Client disconnected: {remote}");
    }
}

var listener = new TcpListener(IPAddress.Any, Port);
listener.Start();
capture.StartRecording();
Console.WriteLine($"Capturing {src.SampleRate} Hz, {inChannels} ch -> 16-bit stereo PCM");
foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()).Where(a => a.AddressFamily == AddressFamily.InterNetwork))
    Console.WriteLine($"Streaming at http://{ip}:{Port}/stream");
Console.WriteLine(password == null ? "No password set (open to the LAN)" : "Password required (any username)");
Console.WriteLine("Ctrl+C to quit");

while (true)
{
    var tcp = await listener.AcceptTcpClientAsync();
    _ = Task.Run(async () =>
    {
        try { await Serve(tcp); }
        catch (Exception ex) { Console.Error.WriteLine($"Client error: {ex.Message}"); }
    });
}
