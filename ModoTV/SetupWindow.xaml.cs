using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using WpfButton = System.Windows.Controls.Button;

namespace ModoTV;

public class CandidateView : INotifyPropertyChanged
{
    public TvCandidate Candidate { get; init; }
    public string Title { get; init; }
    public string Detail { get; init; }
    public string Icon { get; init; }
    public Visibility BadgeVisibility => Candidate.LooksLikeTv ? Visibility.Visible : Visibility.Collapsed;

    bool selected;
    public bool Selected { get => selected; set { selected = value; PropertyChanged?.Invoke(this, new(nameof(Tag))); } }
    public string Tag => selected ? "on" : null;

    public event PropertyChangedEventHandler PropertyChanged;
}

// Tela de configuração: detecta, o usuário confirma (e pode testar) e só então salva.
public partial class SetupWindow : Window
{
    const string TvIcon = "M2.5,4.5 H21.5 V16.5 H2.5 Z M7.5,20 L9.5,16.5 M16.5,20 L14.5,16.5";
    const string MonitorIcon = "M3,4 H21 V15.5 H3 Z M12,15.5 V19.5 M8,19.5 H16";

    readonly Config cfg;
    readonly Switcher switcher;
    readonly Func<bool> getAutostart;
    readonly Action<bool> setAutostart;
    readonly ControllerWatcher pads;
    readonly DispatcherTimer padTimer;
    Pad chord;
    bool recordingChord;
    List<CandidateView> views = new();
    List<MonitorInfo> monitors = new();
    bool startup, audio, busy, saved, nameEdited;
    (List<string> ids, string name, string brand, List<string> audio, string display) original;

    public event Action Saved;

    public SetupWindow(Config cfg, Switcher switcher, ControllerWatcher pads, Func<bool> getAutostart, Action<bool> setAutostart)
    {
        InitializeComponent();
        Icon = AppIcon.Window();
        this.cfg = cfg;
        this.switcher = switcher;
        this.pads = pads;
        this.getAutostart = getAutostart;
        this.setAutostart = setAutostart;
        chord = cfg.ControllerChord;
        original = (cfg.TvMonitorIds.ToList(), cfg.TvName, cfg.TvBrand, cfg.TvAudioNames.ToList(), cfg.DisplayName);

        startup = !cfg.IsConfigured || getAutostart(); // primeira vez: já vem marcado
        audio = cfg.SwitchAudio;

        Header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        CloseButton.Click += (_, _) => Close();
        RescanButton.Click += (_, _) => Scan();
        StartupChip.Click += (_, _) => { startup = !startup; Refresh(); };
        AudioChip.Click += (_, _) => { audio = !audio; Refresh(); };
        NameBox.TextChanged += (_, _) => { if (NameBox.IsKeyboardFocused) nameEdited = true; };
        TestButton.Click += async (_, _) => await TestAsync();
        SaveButton.Click += (_, _) => Save();
        Closed += (_, _) => { StopChordUi(); if (!saved) RestoreOriginal(); };

        RecordButton.Click += (_, _) => ToggleRecording();
        ResetChordButton.Click += (_, _) => { CancelRecording(); chord = PadNames.DefaultChord; pads.Chord = chord; UpdatePad(); };
        pads.RecordProgress += OnRecordProgress;
        pads.Recorded += OnRecorded;
        // Atualiza o nome do controle conectado enquanto a janela está aberta (1x por segundo).
        padTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        padTimer.Tick += (_, _) => UpdatePad();
        padTimer.Start();
        UpdatePad();

        if (cfg.IsConfigured) { NameBox.Text = cfg.DisplayName; nameEdited = true; }
        Scan();
    }

    CandidateView Selected => views.FirstOrDefault(v => v.Selected);

    void Scan()
    {
        try { monitors = Display.GetMonitors(); } catch { monitors = new(); }
        var ranked = Detector.Rank(monitors);
        views = ranked.Select(c => new CandidateView
        {
            Candidate = c,
            Title = Detector.Pretty(c.Monitor),
            Detail = string.Join(" · ", new[]
            {
                c.Monitor.Connection,
                c.Monitor.Inches > 0 ? $"{c.Monitor.Inches:0}\"" : null,
                c.Monitor.Active && c.Monitor.Width > 0 ? $"{c.Monitor.Width}x{c.Monitor.Height}" : "desligada no Windows",
            }.Where(x => !string.IsNullOrEmpty(x))),
            Icon = c.LooksLikeTv ? TvIcon : MonitorIcon,
        }).ToList();

        // Mantém a TV já salva; senão, sugere a mais provável (só se ela realmente parecer uma TV).
        var pick = views.FirstOrDefault(v => cfg.IsTv(v.Candidate.Monitor.Id))
                   ?? views.FirstOrDefault(v => v.Candidate.LooksLikeTv);
        if (pick != null) pick.Selected = true;

        Candidates.ItemsSource = views;
        NoneHint.Visibility = views.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = pick == null && views.Count > 0
            ? "Não temos certeza de qual é a TV. Toque nela na lista acima."
            : "Dica: use “Testar” para ver a imagem ir para a TV por 10 segundos antes de salvar.";
        Refresh();
    }

    void Candidate_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not CandidateView v) return;
        foreach (var x in views) x.Selected = x == v;
        if (!nameEdited) NameBox.Text = "";
        Refresh();
    }

    void Refresh()
    {
        var tv = Selected?.Candidate.Monitor;
        var desk = monitors.Where(m => m.Available && (tv == null || m.Id != tv.Id)).ToList();
        DeskText.Text = desk.Count == 0
            ? "Nenhum outro monitor. A TV seria a única tela."
            : string.Join(" + ", desk.Select(Detector.Pretty)) + "\nApagam no modo TV e voltam do mesmo jeito.";

        string tvAudio = tv == null ? null : Detector.FindTvAudio(tv);
        AudioText.Text = tv == null ? "—"
            : tvAudio ?? "Procuramos quando a TV ligar (pelo nome da TV).";

        if (!nameEdited && tv != null) NameBox.Text = Detector.Pretty(tv);

        StartupChip.Tag = startup ? "on" : null;
        AudioChip.Tag = audio ? "on" : null;
        bool ok = tv != null && desk.Count > 0 && !busy;
        SaveButton.IsEnabled = tv != null && !busy;
        TestButton.IsEnabled = ok;
        RescanButton.IsEnabled = !busy;
    }

    // Copia a escolha para a configuração (em memória).
    void ApplySelection()
    {
        var tv = Selected.Candidate.Monitor;
        cfg.TvMonitorIds = new() { tv.Id };
        cfg.TvName = tv.Name ?? "";
        cfg.TvBrand = Detector.BrandOf(tv);
        var tvAudio = Detector.FindTvAudio(tv);
        cfg.TvAudioNames = new() { tvAudio ?? tv.Name ?? "" };
        cfg.DisplayName = string.IsNullOrWhiteSpace(NameBox.Text) ? Detector.Pretty(tv) : NameBox.Text.Trim();
        cfg.SwitchAudio = audio;
        if (!string.Equals(original.ids.FirstOrDefault(), tv.Id, StringComparison.OrdinalIgnoreCase))
        {
            // TV diferente: as resoluções salvas eram da anterior.
            cfg.TvModes = new();
            cfg.TvPreset = "auto";
        }
    }

    async Task TestAsync()
    {
        if (Selected == null) return;
        ApplySelection();
        busy = true; Refresh();
        StatusText.Text = "Testando: a imagem vai para a TV por 10 segundos e volta sozinha…";
        var r = await Task.Run(() => switcher.Test(10));
        busy = false; Refresh();
        Activate();
        StatusText.Text = r.Ok
            ? "Voltamos. A imagem apareceu na TV? Se sim, clique em “Está certo, salvar”. Se não, escolha outra tela."
            : "Não deu certo: " + r.Message;
    }

    void Save()
    {
        if (Selected == null) return;
        ApplySelection();
        CancelRecording();
        cfg.ControllerChord = chord;
        pads.Chord = chord;
        if (!switcher.IsTvMode()) switcher.RememberDesk(); // guarda a disposição atual da mesa
        cfg.Save();
        setAutostart(startup);
        saved = true;
        Saved?.Invoke();
        Close();
    }

    // ---------- Atalho do controle ----------

    void UpdatePad()
    {
        string name = pads.ConnectedName;
        PadDot.Fill = name != null ? System.Windows.Media.Brushes.MediumSeaGreen : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6E, 0x6E, 0x75));
        PadText.Text = name != null ? $"{name} conectado" : "Nenhum controle conectado agora";
        RecordButton.IsEnabled = name != null || recordingChord;
        ResetChordButton.IsEnabled = chord != PadNames.DefaultChord;
        if (!recordingChord)
        {
            ChordText.Text = PadNames.Format(chord, pads.Family);
            ChordHint.Text = "segurar 1,5 s";
        }
    }

    void ToggleRecording()
    {
        if (recordingChord) { CancelRecording(); UpdatePad(); return; }
        recordingChord = true;
        pads.StartRecording();
        RecordText.Text = "Cancelar";
        ChordText.Text = "Segure os botões que quiser…";
        ChordHint.Text = "por 2 s";
        ChordFill.Width = 0;
    }

    void CancelRecording()
    {
        if (!recordingChord) return;
        recordingChord = false;
        pads.CancelRecording();
        RecordText.Text = "Gravar outro atalho";
        ChordFill.Width = 0;
    }

    // Os eventos chegam da thread do controle; a tela só é tocada pela thread dela.
    void OnRecordProgress(Pad pressed, double progress) => Dispatcher.BeginInvoke(() =>
    {
        if (!recordingChord) return;
        ChordText.Text = PadNames.Count(pressed) >= 2 ? PadNames.Format(pressed, pads.Family) : "Segure 2 ou mais botões juntos…";
        ChordFill.Width = Math.Clamp(progress, 0, 1) * ChordBox.ActualWidth;
    });

    void OnRecorded(Pad pressed) => Dispatcher.BeginInvoke(() =>
    {
        recordingChord = false;
        chord = pressed;
        pads.Chord = chord; // já vale para testar; só fica salvo ao clicar em salvar
        RecordText.Text = "Gravar outro atalho";
        ChordFill.Width = 0;
        UpdatePad();
        StatusText.Text = $"Atalho gravado: {PadNames.Format(chord, pads.Family)}. Segure por 1,5 s para trocar de tela.";
    });

    void StopChordUi()
    {
        padTimer.Stop();
        CancelRecording();
        pads.RecordProgress -= OnRecordProgress;
        pads.Recorded -= OnRecorded;
    }

    void RestoreOriginal()
    {
        pads.Chord = cfg.ControllerChord;
        cfg.TvMonitorIds = original.ids;
        cfg.TvName = original.name;
        cfg.TvBrand = original.brand;
        cfg.TvAudioNames = original.audio;
        cfg.DisplayName = original.display;
        cfg.Save();
    }
}
