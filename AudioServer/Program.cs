// Captures whatever is playing on the default output device (WASAPI loopback)
// and serves it as an endless Ogg Opus stream at http://<host>:<port>/
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Concentus;
using Concentus.Enums;
using NAudio.Wave;

// Optional: AudioServer.exe --port <n>  (default 9000)
int port = 9000;
int portArg = Array.IndexOf(args, "--port");
if (portArg >= 0)
{
    if (portArg + 1 >= args.Length || !int.TryParse(args[portArg + 1], out port) || port < 1 || port > 65535)
    {
        Console.Error.WriteLine("--port needs a number from 1 to 65535.");
        return 1;
    }
}

// Optional: AudioServer.exe --bind <ipv4>  (default 0.0.0.0, every interface)
var bindAddress = IPAddress.Any;
int bindArg = Array.IndexOf(args, "--bind");
if (bindArg >= 0)
{
    if (bindArg + 1 >= args.Length || !IPAddress.TryParse(args[bindArg + 1], out bindAddress)
        || bindAddress.AddressFamily != AddressFamily.InterNetwork)
    {
        Console.Error.WriteLine("--bind needs an IPv4 address, e.g. 127.0.0.1.");
        return 1;
    }
}

// Optional: AudioServer.exe --bitrate <kbps>  (default 160)
int bitrate = 160;
int brArg = Array.IndexOf(args, "--bitrate");
if (brArg >= 0)
{
    if (brArg + 1 >= args.Length || !int.TryParse(args[brArg + 1], out bitrate) || bitrate < 6 || bitrate > 510)
    {
        Console.Error.WriteLine("--bitrate needs a number of kbps from 6 to 510.");
        return 1;
    }
}

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
const float Mix = 0.707f; // -3 dB for center and surround channels
const int OpusRate = 48000;
const int FrameSamples = 960; // 20 ms at 48 kHz
const int FramesPerPage = 5;  // 100 ms of audio per Ogg page keeps container overhead low
var clients = new ConcurrentDictionary<Channel<byte[][]>, byte>();
long lastDataTick = Environment.TickCount64;

// Managed Opus only, so the exe never picks up whatever opus.dll happens to be on the machine
OpusCodecFactory.AttemptToUseNativeLibrary = false;
var encoder = OpusCodecFactory.CreateEncoder(OpusRate, outChannels, OpusApplication.OPUS_APPLICATION_AUDIO);
encoder.Bitrate = bitrate * 1000;
int preSkip = encoder.Lookahead;
var resampler = src.SampleRate == OpusRate
    ? null
    : ResamplerFactory.CreateResampler(outChannels, src.SampleRate, OpusRate, 10);
var pcm48 = new List<float>();
var packets = new List<byte[]>(FramesPerPage);
var packetBuffer = new byte[1275]; // largest possible Opus packet
var encodeLock = new object();

void Broadcast(byte[][] page)
{
    foreach (var ch in clients.Keys)
        ch.Writer.TryWrite(page);
}

// Called from the capture thread and the silence timer, hence the lock.
void Encode(float[] stereo, int frames)
{
    lock (encodeLock)
    {
        if (resampler == null)
        {
            pcm48.AddRange(stereo.AsSpan(0, frames * outChannels));
        }
        else
        {
            var resampled = new float[(frames * OpusRate / src.SampleRate + 16) * outChannels];
            int inLen = frames, outLen = resampled.Length / outChannels;
            resampler.ProcessInterleaved(stereo.AsSpan(0, frames * outChannels), ref inLen, resampled, ref outLen);
            pcm48.AddRange(resampled.AsSpan(0, outLen * outChannels));
        }

        while (pcm48.Count >= FrameSamples * outChannels)
        {
            var frame = CollectionsMarshal.AsSpan(pcm48)[..(FrameSamples * outChannels)];
            int len = encoder.Encode(frame, FrameSamples, packetBuffer, packetBuffer.Length);
            pcm48.RemoveRange(0, FrameSamples * outChannels);
            packets.Add(packetBuffer[..len]);
            if (packets.Count == FramesPerPage)
            {
                Broadcast(packets.ToArray());
                packets.Clear();
            }
        }
    }
}

capture.DataAvailable += (_, e) =>
{
    // Downmix to stereo assuming standard WAVE channel order:
    // FL FR FC LFE then surround pairs (left on even index, right on odd). LFE is dropped.
    int frames = e.BytesRecorded / (4 * inChannels);
    var input = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, frames * 4 * inChannels));
    var stereo = new float[frames * outChannels];
    for (int f = 0; f < frames; f++)
    {
        var s = input.Slice(f * inChannels, inChannels);
        float l = s[0], r = inChannels > 1 ? s[1] : s[0];
        if (inChannels > 2) { l += Mix * s[2]; r += Mix * s[2]; }
        for (int c = 4; c < inChannels; c++)
        {
            if (c % 2 == 0) l += Mix * s[c]; else r += Mix * s[c];
        }
        stereo[f * 2] = l;
        stereo[f * 2 + 1] = r;
    }
    Interlocked.Exchange(ref lastDataTick, Environment.TickCount64);
    Encode(stereo, frames);
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
        {
            int frames = (int)(src.SampleRate * elapsed / 1000);
            Encode(new float[frames * outChannels], frames);
        }
    }
});

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
    if (parts.Length < 2 || parts[1] != "/")
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
        "HTTP/1.1 200 OK\r\nContent-Type: audio/ogg\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n"));
    var ogg = new OggOpusStream(Random.Shared.Next(), preSkip, src.SampleRate, outChannels);
    await stream.WriteAsync(ogg.Headers());

    // ~1s of buffered pages; a slow client loses its oldest audio instead of blocking capture
    var ch = Channel.CreateBounded<byte[][]>(new BoundedChannelOptions(10)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    clients.TryAdd(ch, 0);
    Console.WriteLine($"Client connected: {remote}");
    try
    {
        await foreach (var page in ch.Reader.ReadAllAsync())
            await stream.WriteAsync(ogg.AudioPage(page, FrameSamples));
    }
    catch (IOException) { }
    finally
    {
        clients.TryRemove(ch, out var _);
        Console.WriteLine($"Client disconnected: {remote}");
    }
}

var listener = new TcpListener(bindAddress, port);
try
{
    listener.Start();
}
catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
{
    Console.Error.WriteLine($"Port {port} is already in use. Pick another with --port <n>.");
    return 1;
}
catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressNotAvailable)
{
    Console.Error.WriteLine($"{bindAddress} isn't an address of this PC.");
    return 1;
}
capture.StartRecording();
Console.WriteLine($"Capturing {src.SampleRate} Hz, {inChannels} ch -> Opus {bitrate} kbps stereo");
var urlAddresses = bindAddress.Equals(IPAddress.Any)
    ? Dns.GetHostAddresses(Dns.GetHostName()).Where(a => a.AddressFamily == AddressFamily.InterNetwork)
    : [bindAddress];
foreach (var ip in urlAddresses)
    Console.WriteLine($"Streaming at http://{ip}:{port}/");
Console.WriteLine(password == null ? "No password set (open to the LAN)" : "Password required (any username)");
Console.WriteLine("Ctrl+C to quit");
Console.WriteLine();
Console.WriteLine("Tired of being ripped off for basic software that should be free?");
Console.WriteLine("Send donations to help fund ad-free/subs-free software for great justice.");
Console.WriteLine("Send Bitcoin: bc1qrazvpeu7ppvre8jvyrf6p80lfhf8xv9xptmx8u");
Console.WriteLine();

while (true)
{
    var tcp = await listener.AcceptTcpClientAsync();
    _ = Task.Run(async () =>
    {
        try { await Serve(tcp); }
        catch (Exception ex) { Console.Error.WriteLine($"Client error: {ex.Message}"); }
    });
}
