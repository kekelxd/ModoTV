using System.IO;
using System.Windows.Media.Imaging;

namespace ModoTV;

// O mesmo ícone (ModoTV.ico, embutido no .exe) na bandeja, nas janelas e no próprio arquivo.
public static class AppIcon
{
    static Stream Open() => typeof(AppIcon).Assembly.GetManifestResourceStream("ModoTV.ModoTV.ico");

    // Tamanho certo para a bandeja na escala atual do Windows.
    public static Icon Tray()
    {
        using var s = Open();
        return new Icon(s, SystemInformation.SmallIconSize);
    }

    // Mesmo ícone com um ponto verde no canto: aviso de atualização disponível.
    public static Icon TrayWithBadge()
    {
        using var baseIcon = Tray();
        using var bmp = baseIcon.ToBitmap();
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float d = bmp.Width * 0.45f, x = bmp.Width - d, y = 0;
            using var ring = new SolidBrush(Color.FromArgb(27, 27, 30));
            using var dot = new SolidBrush(Color.FromArgb(61, 208, 122));
            g.FillEllipse(ring, x - 1, y - 1, d + 2, d + 2);
            g.FillEllipse(dot, x, y, d, d);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public static System.Windows.Media.ImageSource Window()
    {
        using var s = Open();
        var decoder = new IconBitmapDecoder(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
    }
}
