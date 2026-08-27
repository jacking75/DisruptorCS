// 패키지 아이콘 생성기. `dotnet run build/generate-icon.cs` 로 build/icon.png 를 다시 만든다.
//
// 바이너리를 저장소에 커밋하면 출처가 사라진다. 생성기를 같이 두면 색을 바꾸고 싶을 때
// 이미지 편집기 없이 이 파일만 고치면 된다. 외부 이미지 라이브러리 의존도 없다.

using System.IO.Compression;

const int Size = 128;      // NuGet 권장 크기
const int Samples = 4;     // 축당 슈퍼샘플 수 (4x4 = 픽셀당 16 샘플)

// 링 버퍼 모티프: 어두운 라운드 사각형 위에 12칸짜리 고리.
// 헤드 직후 칸이 가장 어둡고 헤드로 갈수록 밝아진다 — 커서가 한 바퀴 도는 모습.
var background = Rgb(0x18, 0x1C, 0x2A);
var head = Rgb(0x22, 0xD3, 0xEE);   // 퍼블리시 커서
var rampFrom = Rgb(0x35, 0x1C, 0x7A);
var rampTo = Rgb(0x7C, 0x4D, 0xFF);

const double CornerRadius = 26;
const double Center = Size / 2.0;
const double ROuter = 46;
const double RInner = 27;
const int Slots = 12;
const double Gap = 0.14;            // 칸 사이 간격(라디안)

var pixels = new byte[Size * Size * 4];

for (var y = 0; y < Size; y++)
{
    for (var x = 0; x < Size; x++)
    {
        double r = 0, g = 0, b = 0, a = 0;

        for (var sy = 0; sy < Samples; sy++)
        for (var sx = 0; sx < Samples; sx++)
        {
            var px = x + (sx + 0.5) / Samples;
            var py = y + (sy + 0.5) / Samples;

            if (!InsideRoundedSquare(px, py))
                continue;

            var color = ColorAt(px, py);
            r += color.R; g += color.G; b += color.B; a += 255;
        }

        var total = Samples * Samples;
        var offset = (y * Size + x) * 4;

        // 커버리지가 0 인 픽셀(모서리 바깥)은 완전 투명으로 둔다.
        if (a > 0)
        {
            var covered = a / 255.0;
            pixels[offset + 0] = (byte)Math.Round(r / covered);
            pixels[offset + 1] = (byte)Math.Round(g / covered);
            pixels[offset + 2] = (byte)Math.Round(b / covered);
        }

        pixels[offset + 3] = (byte)Math.Round(a / total);
    }
}

var target = Path.Combine(AppContext.BaseDirectory, "icon.png");
if (args.Length > 0)
    target = args[0];
else if (File.Exists("build/generate-icon.cs"))
    target = "build/icon.png";

WritePng(target, pixels);
Console.WriteLine($"{target} ({Size}x{Size}, {new FileInfo(target).Length} bytes)");

// --- 도형 ---

static bool InsideRoundedSquare(double x, double y)
{
    // 모서리 원의 중심으로부터의 거리로 판정한다.
    var dx = Math.Max(Math.Max(CornerRadius - x, x - (Size - CornerRadius)), 0);
    var dy = Math.Max(Math.Max(CornerRadius - y, y - (Size - CornerRadius)), 0);
    return dx * dx + dy * dy <= CornerRadius * CornerRadius;
}

(double R, double G, double B) ColorAt(double x, double y)
{
    var dx = x - Center;
    var dy = y - Center;
    var radius = Math.Sqrt(dx * dx + dy * dy);

    if (radius < RInner || radius > ROuter)
        return background;

    // 12시 방향에서 시작해 시계 방향. 화면 좌표는 y 가 아래로 자라므로 atan2 가 그대로 시계 방향이다.
    var theta = Math.Atan2(dx, -dy);
    if (theta < 0) theta += 2 * Math.PI;

    var step = 2 * Math.PI / Slots;
    var index = (int)(theta / step);
    var within = theta - index * step;

    if (within < Gap / 2 || within > step - Gap / 2)
        return background;   // 칸 사이 틈

    if (index == 0)
        return head;

    var t = (index - 1) / (double)(Slots - 2);
    return Lerp(rampFrom, rampTo, t);
}

static (double R, double G, double B) Rgb(int r, int g, int b) => (r, g, b);

static (double R, double G, double B) Lerp((double R, double G, double B) a, (double R, double G, double B) b, double t)
    => (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

// --- PNG 인코딩 (RGBA8, 필터 없음) ---

static void WritePng(string path, byte[] rgba)
{
    using var file = File.Create(path);

    file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

    var ihdr = new byte[13];
    WriteBigEndian(ihdr, 0, Size);
    WriteBigEndian(ihdr, 4, Size);
    ihdr[8] = 8;    // 채널당 8비트
    ihdr[9] = 6;    // 트루컬러 + 알파
    WriteChunk(file, "IHDR", ihdr);

    // 스캔라인마다 필터 바이트 0 을 붙인 뒤 zlib 으로 압축한다.
    var raw = new byte[Size * (1 + Size * 4)];
    for (var y = 0; y < Size; y++)
        Array.Copy(rgba, y * Size * 4, raw, y * (1 + Size * 4) + 1, Size * 4);

    using var compressed = new MemoryStream();
    using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        zlib.Write(raw);

    WriteChunk(file, "IDAT", compressed.ToArray());
    WriteChunk(file, "IEND", Array.Empty<byte>());
}

static void WriteChunk(Stream stream, string type, byte[] data)
{
    var length = new byte[4];
    WriteBigEndian(length, 0, data.Length);
    stream.Write(length);

    var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
    stream.Write(typeBytes);
    stream.Write(data);

    var crc = new byte[4];
    WriteBigEndian(crc, 0, (int)Crc32(typeBytes, data));
    stream.Write(crc);
}

static void WriteBigEndian(byte[] buffer, int offset, int value)
{
    buffer[offset + 0] = (byte)(value >> 24);
    buffer[offset + 1] = (byte)(value >> 16);
    buffer[offset + 2] = (byte)(value >> 8);
    buffer[offset + 3] = (byte)value;
}

static uint Crc32(byte[] a, byte[] b)
{
    var table = new uint[256];
    for (uint i = 0; i < 256; i++)
    {
        var c = i;
        for (var k = 0; k < 8; k++)
            c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        table[i] = c;
    }

    var crc = 0xFFFFFFFFu;
    foreach (var chunk in new[] { a, b })
        foreach (var value in chunk)
            crc = table[(crc ^ value) & 0xFF] ^ (crc >> 8);

    return crc ^ 0xFFFFFFFFu;
}
