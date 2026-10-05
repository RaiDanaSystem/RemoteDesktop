using RemoteSupport.Shared.ScreenStreaming.Video;

const int w = 320;
const int h = 176;
var bgra = new byte[w * h * 4];
for (var y = 0; y < h; y++)
for (var x = 0; x < w; x++)
{
    var o = (y * w + x) * 4;
    bgra[o] = (byte)x;
    bgra[o + 1] = (byte)y;
    bgra[o + 2] = 80;
    bgra[o + 3] = 255;
}

Console.WriteLine($"dll exists={File.Exists(OpenH264Native.DllPath)}");
using var encoder = new OpenH264Encoder(w, h, fps: 15, bitrate: 800_000);
using var decoder = new OpenH264Decoder();
byte[]? decoded = null;
var produced = 0;
for (var i = 0; i < 8; i++)
{
    var annexB = encoder.EncodeBgra(bgra, w, h, out var key);
    Console.WriteLine($"frame {i}: bytes={annexB?.Length ?? 0} key={key}");
    if (annexB is not { Length: > 0 })
        continue;
    produced++;
    decoded = decoder.DecodeAnnexB(annexB, w, h);
    Console.WriteLine($"  decoded={(decoded?.Length ?? 0)}");
}

if (produced == 0)
{
    Console.WriteLine("FAIL: no H.264 output");
    return 2;
}

Console.WriteLine(decoded is { Length: > 0 }
    ? $"PASS produced={produced} decodedBgra={decoded.Length}"
    : $"PARTIAL encode ok decode empty produced={produced}");
return decoded is { Length: > 0 } ? 0 : 1;
