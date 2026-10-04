#:package Svg.Skia@5.2.3
#:property ManagePackageVersionsCentrally=false

// Renders docs/brand/app-icon.svg into the multi-size .ico used by both executables,
// plus a 256 px PNG for the README. Run from the repo root: dotnet run scripts/make-icons.cs
using SkiaSharp;
using Svg.Skia;

var brand = Path.Combine("docs", "brand");
using var svg = new SKSvg();
svg.Load(Path.Combine(brand, "app-icon.svg"));
var picture = svg.Picture ?? throw new InvalidOperationException("app-icon.svg did not render");

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var pngs = sizes.Select(size => Render(picture, size)).ToList();

File.WriteAllBytes(Path.Combine(brand, "app-icon-256.png"), pngs[^1]);

using var ico = File.Create(Path.Combine(brand, "xiv-vault.ico"));
using var writer = new BinaryWriter(ico);
writer.Write((ushort)0);
writer.Write((ushort)1);
writer.Write((ushort)sizes.Length);
var offset = 6 + 16 * sizes.Length;
for (var i = 0; i < sizes.Length; i++)
{
    // A 256 px entry is stored as 0 in the one-byte width and height fields.
    writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
    writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
    writer.Write((byte)0);
    writer.Write((byte)0);
    writer.Write((ushort)1);
    writer.Write((ushort)32);
    writer.Write(pngs[i].Length);
    writer.Write(offset);
    offset += pngs[i].Length;
}
foreach (var png in pngs)
{
    writer.Write(png);
}

Console.WriteLine($"Wrote xiv-vault.ico ({string.Join(", ", sizes)}) and app-icon-256.png");

static byte[] Render(SKPicture picture, int size)
{
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);
    var scale = size / picture.CullRect.Width;
    canvas.Scale(scale);
    canvas.DrawPicture(picture);
    canvas.Flush();
    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}
