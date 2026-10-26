# OpenStreaming for Windows

**Streams whatever your Windows PC is playing to VLC on any device on your
network. No drivers, no installer, no admin rights.**

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)
[![Platform: Windows x64](https://img.shields.io/badge/platform-Windows%20x64-0078D6.svg)](#requirements)

Run `AudioServer.exe` and open `http://<pc-ip>:9000/` in VLC on a phone,
laptop or another PC. You'll hear what the PC's speakers are playing.

## How it works

Windows can already record whatever plays on an output device. The feature is
called WASAPI loopback and has been built in since Vista. AudioServer:

1. captures the default output device with WASAPI loopback (via
   [NAudio](https://github.com/naudio/NAudio)),
2. mixes it down to stereo (the center and surround channels at −3 dB, the LFE
   channel dropped),
3. encodes it as Opus at 160 kbps (via
   [Concentus](https://github.com/lostromb/concentus), a pure C# Opus port, so
   there's no native DLL),
4. serves it on port 9000 (or the one you choose) as an endless Ogg Opus stream
   over plain HTTP.

At 160 kbps, Opus is generally indistinguishable from the original in blind
listening tests. That's about a ninth of the 1,411 kbps uncompressed CD-quality
audio would take, and silence costs about 4 kbps.

While nothing is playing, Windows sends no audio. AudioServer sends silence in
the gaps so VLC doesn't stall. Each client has its own buffer of about 1 second,
so a slow client loses its oldest audio instead of holding up the others.

The HTTP server is written on a raw TCP socket rather than `HttpListener`. That
is why it can listen on the network without an admin `netsh urlacl` entry.

## Download

Download `AudioServer.exe` from the
[latest release](https://github.com/riazhassan-za/openstreaming/releases/latest).
It's a single file with the .NET runtime bundled, for 64-bit Windows 10 or 11.
Nothing to install.

### Windows SmartScreen warning

The exe is unsigned, so the first time you run it Windows shows
**"Windows protected your PC"**. To run it anyway:

1. Click **More info**.
2. Click **Run anyway**.

Windows remembers the choice for that file, so this only happens once.

To check the download is the one the release workflow built, compare its hash
with `SHA256SUMS.txt` on the same release:

```powershell
(Get-FileHash .\AudioServer.exe -Algorithm SHA256).Hash.ToLower()
```

## Usage

```
AudioServer.exe                       # open to the LAN
AudioServer.exe --password s3cret     # require a password
AudioServer.exe --port 8080           # listen on another port (default 9000)
AudioServer.exe --bitrate 192         # Opus bitrate in kbps, 6-510 (default 160)
AudioServer.exe --bind 127.0.0.1      # this PC only (default 0.0.0.0, every interface)
```

`--bind` takes an IPv4 address of this PC. `127.0.0.1` keeps the stream on
this machine, so nothing else on the network can reach it and Windows doesn't
ask about the firewall. A LAN address such as `192.168.0.104` serves only that
network, which helps on a PC with a VPN or several adapters.

`localhost` works too. Windows tries IPv6 `::1` first, and VLC falls back to
`127.0.0.1` straight away.

The options can be combined, e.g. `AudioServer.exe --port 8080 --password s3cret`.

The console prints the stream URL for each of the PC's IPv4 addresses.

In VLC, choose **Media → Open Network Stream** and enter:

```
http://192.168.0.104:9000/
```

With a password set, VLC shows a login box. Any username works. To skip the
box, put the password in the URL: `http://any:s3cret@192.168.0.104:9000/`.

For lower latency, start VLC with `--network-caching=200` (the default buffer is
about 1 s).

### Firewall

The first time it runs, Windows asks whether to allow AudioServer on the
network. Allow it on **Private networks**. If you missed the prompt, run this
in an admin PowerShell (change `9000` if you use `--port`):

```powershell
New-NetFirewallRule -DisplayName "AudioServer 9000" -Direction Inbound -Protocol TCP -LocalPort 9000 -Action Allow -Profile Private
```

## Requirements

- Windows 10 or 11, x64.
- Nothing else. The release exe is self-contained and includes the .NET runtime.

## Building

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
dotnet publish AudioServer -c Release -o dist
```

This produces a single `dist\AudioServer.exe`.

### Releasing

Push a `v*` tag on a commit in `main`:

```
git tag v0.2.0
git push origin v0.2.0
```

The [release workflow](.github/workflows/release.yml) builds the exe, publishes
a GitHub Release with `AudioServer.exe` and `SHA256SUMS.txt`, and generates
notes from the commits since the previous tag.

## Limitations

- **Plain HTTP.** The password stops casual access but is sent base64-encoded,
  not encrypted. Use it only on a network you trust.
- **Lossy audio.** Opus at 160 kbps sounds the same as the original to most
  listeners but isn't bit-exact. If the source is already lossy (YouTube,
  Spotify), raising `--bitrate` to 192 or more leaves more headroom for the
  second encode.
- **About 100 ms of extra delay.** Opus packets are sent in 100 ms Ogg pages,
  on top of VLC's own buffer.
- **Default device only.** Changing the default output device or unplugging
  headphones stops the capture and the program exits.
- **Echo on the same PC.** If VLC plays the stream on the PC being captured,
  through the same output device, the audio loops back into the stream.
- **Downmix clipping.** Loud true-surround content can distort when mixed down
  to stereo. Ordinary stereo sources are unaffected.

## Donate

Tired of being ripped off for basic software that should be free? Send
donations to help fund ad-free/subs-free software for great justice.

Send Bitcoin:

[`bc1qrazvpeu7ppvre8jvyrf6p80lfhf8xv9xptmx8u`](bitcoin:bc1qrazvpeu7ppvre8jvyrf6p80lfhf8xv9xptmx8u)

## License

[GPL-3.0](LICENSE). NAudio is MIT-licensed; Concentus is BSD-licensed.
