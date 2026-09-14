using System.Drawing;
using System.Drawing.Imaging;
using MechrevoLite.UI;

namespace MechrevoLite.Tests;

/// <summary>
/// 线性图标是现画的（GDI+），画错了不会报错、只会渲染成空白——所以这里把十个图标
/// 拼成一张对照图落盘，并在测试里断言"确实画了东西"（非透明像素占比）。
/// 对照图：artifacts/ui-glyphs.png
/// </summary>
public class UiGlyphRenderTests
{
    // 参数用 int 而不是 UiGlyph.Kind：Kind 是 internal（UiGlyph 本身 internal），
    // 公开的测试方法不能出现可访问性更低的参数类型。
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    public void EveryGlyphActuallyDrawsSomething(int kindIndex)
    {
        UiGlyph.Kind kind = (UiGlyph.Kind)kindIndex;
        using Bitmap bitmap = UiGlyph.Render(kind, 16, Color.White);

        int painted = 0;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).A > 8) painted++;

        double ratio = painted / (double)(bitmap.Width * bitmap.Height);
        Assert.True(ratio > 0.03, $"{kind} 几乎没画出东西（非透明像素 {ratio:P1}）");
    }

    /// <summary>把十个图标拼成一张图，便于人眼核对形状。</summary>
    [Fact]
    public void GlyphContactSheet()
    {
        UiGlyph.Kind[] kinds = Enum.GetValues<UiGlyph.Kind>();
        const int cell = 32;
        using var sheet = new Bitmap(cell * kinds.Length, cell);
        using (Graphics g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.FromArgb(0x11, 0x1A, 0x2B));
            for (int i = 0; i < kinds.Length; i++)
            {
                using Bitmap glyph = UiGlyph.Render(kinds[i], 24, Color.FromArgb(0x8F, 0xA3, 0xBF));
                g.DrawImage(glyph, i * cell + 4, 4);
            }
        }

        string path = Path.Combine(FindRepoRoot(), "artifacts", "ui-glyphs.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        sheet.Save(path, ImageFormat.Png);
        Assert.True(File.Exists(path));
    }

    static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MechrevoLite.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
