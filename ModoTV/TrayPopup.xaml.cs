using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using WpfButton = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Size = System.Windows.Size;

namespace ModoTV;

public class PopupState
{
    public bool TvMode, Busy, TvAvailable;
    public int DeskActive;
    public string DeskNames = "", TvResolution = "", AudioName = "", PadName, Status, ChordLabel = "Select + Start";
    public bool SwitchAudio, Autostart, ControllerEnabled;
    public string TvPreset = "auto", Brand = "", Subtitle = "", Title = "";
    public List<string> Presets = new();
}

public partial class TrayPopup : Window
{
    public event Action GoTv, GoDesk, Toggle, ToggleAudio, ToggleStartup, TogglePad, ShowMenu;
    public event Action<string> SetPreset;
    public DateTime HiddenAt { get; private set; }

    public TrayPopup()
    {
        InitializeComponent();
        VersionText.Text = "v" + (typeof(TrayPopup).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
        PowerButton.Click += (_, _) => Toggle?.Invoke();
        SwitchButton.Click += (_, _) => Toggle?.Invoke();
        DeskTile.Click += (_, _) => GoDesk?.Invoke();
        TvTile.Click += (_, _) => GoTv?.Invoke();
        AudioChip.Click += (_, _) => ToggleAudio?.Invoke();
        StartupChip.Click += (_, _) => ToggleStartup?.Invoke();
        PadChip.Click += (_, _) => TogglePad?.Invoke();
        MoreButton.Click += (_, _) => ShowMenu?.Invoke();
        PresetA.Click += (_, _) => PickPreset(0);
        PresetB.Click += (_, _) => PickPreset(1);
        Deactivated += (_, _) => { HiddenAt = DateTime.UtcNow; Hide(); };
    }

    static void On(WpfButton b, bool on) => b.Tag = on ? "on" : null;

    List<string> presets = new();
    string currentPreset = "auto";

    void FillPreset(WpfButton tile, System.Windows.Controls.TextBlock res, System.Windows.Controls.TextBlock hz, int i, string chosen)
    {
        if (i >= presets.Count || !Config.TryParseMode(presets[i], out var m)) { tile.Visibility = Visibility.Collapsed; return; }
        tile.Visibility = Visibility.Visible;
        res.Text = Config.ShortRes(m.W, m.H);
        hz.Text = $"  {m.Hz} Hz";
        On(tile, presets[i] == chosen);
        currentPreset = chosen;
    }

    // Clicar na opção já escolhida volta para "automático".
    void PickPreset(int i)
    {
        if (i >= presets.Count) return;
        SetPreset?.Invoke(presets[i] == currentPreset ? "auto" : presets[i]);
    }

    public void Apply(PopupState s)
    {
        bool tv = s.TvMode;
        On(PowerButton, tv);
        On(DeskTile, !tv);
        On(TvTile, tv);
        On(AudioChip, s.SwitchAudio);
        On(StartupChip, s.Autostart);
        On(PadChip, s.ControllerEnabled);
        presets = s.Presets;
        TitleText.Text = s.Title;
        SubtitleText.Text = s.Subtitle;
        BrandText.Text = s.Brand.ToUpperInvariant();
        FillPreset(PresetA, PresetARes, PresetAHz, 0, s.TvPreset);
        FillPreset(PresetB, PresetBRes, PresetBHz, 1, s.TvPreset);
        PresetGrid.Columns = Math.Max(1, presets.Count);
        PresetGrid.Visibility = presets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PresetHint.Visibility = presets.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        PresetA.IsEnabled = PresetB.IsEnabled = !s.Busy;

        ScreenOn.Opacity = tv ? 1 : 0;
        ScreenBloom.Opacity = tv ? 1 : 0;
        ScreenLabel.Opacity = tv ? 0.95 : 0;
        TvGlow.Opacity = tv ? 1 : 0;
        PowerLed.Fill = new SolidColorBrush(tv ? Color.FromRgb(0x3D, 0xD0, 0x7A) : Color.FromRgb(0xE0, 0x45, 0x3A));
        ResolutionBadge.Text = string.IsNullOrEmpty(s.TvResolution) ? "HDMI" : s.TvResolution;
        ScreenLabel.Text = "Jogando";

        MonitorsRun.Text = s.DeskActive == 0 ? "em espera" : s.DeskActive == 1 ? "1 ativo" : $"{s.DeskActive} ativos";
        TvRun.Text = tv ? "em uso" : s.TvAvailable ? "pronta" : "não encontrada";
        ModeTag.Text = tv ? "TV" : "MESA";

        BigText.Text = tv ? "Na TV" : "Na mesa";
        BigSub.Text = tv ? "Monitores apagados · jogos abrem na TV" : s.DeskNames;

        SwitchText.Text = s.Busy ? "Trocando…" : tv ? "Voltar para a mesa" : "Ir para a TV";
        SwitchIcon.Data = Geometry.Parse(tv
            ? "M1.5,5 H10.5 V12.5 H1.5 Z M13.5,5 H22.5 V12.5 H13.5 Z M6,12.5 V16 M18,12.5 V16 M3.5,16 H8.5 M15.5,16 H20.5"
            : "M2.5,4.5 H21.5 V16.5 H2.5 Z M7.5,20 L9.5,16.5 M16.5,20 L14.5,16.5");
        SwitchButton.IsEnabled = !s.Busy && (tv || s.TvAvailable);
        TvTile.IsEnabled = !s.Busy && (tv || s.TvAvailable);
        DeskTile.IsEnabled = !s.Busy;
        PowerButton.IsEnabled = SwitchButton.IsEnabled;

        AudioText.Text = string.IsNullOrEmpty(s.AudioName) ? "—" : s.AudioName;
        StatusText.Text = s.Status ?? (s.ControllerEnabled
            ? $"Segure {s.ChordLabel} ou Ctrl+Alt+F11"
            : "Atalho: Ctrl+Alt+F11");

        PadChip.ToolTip = $"Trocar segurando {s.ChordLabel} no controle por 1,5 s";
        bool pad = s.ControllerEnabled && !string.IsNullOrEmpty(s.PadName);
        PadDot.Fill = new SolidColorBrush(pad ? Color.FromRgb(0x3D, 0xD0, 0x7A) : Color.FromRgb(0x6E, 0x6E, 0x75));
        PadText.Text = !s.ControllerEnabled ? "Controle desativado" : pad ? $"{s.PadName} conectado" : "Nenhum controle conectado";
    }

    // ---------- Posição: canto da área de trabalho onde está o cursor ----------

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(System.Drawing.Point pt, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dx, out uint dy);

    public void ShowNearTray()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var work = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
        double scale = 1;
        if (GetDpiForMonitor(MonitorFromPoint(cursor, 2), 0, out uint dpi, out _) == 0) scale = dpi / 96.0;

        Opacity = 0;
        Show();
        UpdateLayout();
        int w = (int)Math.Ceiling(ActualWidth * scale), h = (int)Math.Ceiling(ActualHeight * scale);
        // Barra de tarefas embaixo (padrão); se estiver em cima, abre colado em cima.
        bool top = cursor.Y < work.Top + work.Height / 2;
        int x = Math.Min(Math.Max(cursor.X - w / 2, work.Left), work.Right - w);
        int y = top ? work.Top : work.Bottom - h;
        SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004); // NOSIZE | NOZORDER
        Opacity = 1;
        Activate();
        PlayOpen();
    }

    // Entrada curta (160 ms): sobe 6 px e aparece. Nada fica rodando depois disso.
    void PlayOpen()
    {
        if (!SystemParameters.ClientAreaAnimation) return; // respeita "efeitos de animação" desligados no Windows
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var time = TimeSpan.FromMilliseconds(160);
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, time) { EasingFunction = ease });
        Lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, time) { EasingFunction = ease });
    }

    // Gera uma imagem da janela (usado para pré-visualizar o design).
    public static void RenderPreview(PopupState s, string file)
    {
        var p = new TrayPopup();
        p.Apply(s);
        Snapshot(p, p.Root, file);
    }

    public static void Snapshot(Window w, FrameworkElement root, string file)
    {
        w.Content = null;
        root.Measure(new Size(w.Width, double.PositiveInfinity));
        root.Arrange(new Rect(0, 0, w.Width, root.DesiredSize.Height));
        root.UpdateLayout();
        const double scale = 2;
        var size = new Size(w.Width, root.DesiredSize.Height); // inclui a margem onde fica a sombra
        int pw = (int)(size.Width * scale), ph = (int)(size.Height * scale);
        var layer = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        layer.Render(root);
        var host = new DrawingVisual();
        using (var dc = host.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x5A, 0x6B, 0x80)), null, new Rect(size));
            dc.DrawImage(layer, new Rect(size));
        }
        var bmp = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(host);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(file);
        enc.Save(fs);
    }
}
