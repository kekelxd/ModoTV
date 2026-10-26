using System.Diagnostics;
using System.Media;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ModoTV;

public class TrayApp : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const int HotkeyId = 1, WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000, VK_F11 = 0x7A;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    readonly Config cfg;
    readonly Switcher switcher;
    readonly NotifyIcon tray;
    readonly ContextMenuStrip menu;
    readonly HotkeyWindow hotkeyWindow;
    readonly ControllerWatcher controller;
    readonly SynchronizationContext ui;
    TrayPopup popup;
    SetupWindow setup;
    int busy;
    // Modo que o usuário escolheu por último. Se o Windows mudar as telas sozinho, é a referência.
    volatile bool wantTv;
    DateTime ignoreDisplayUntil;
    System.Windows.Forms.Timer displayDebounce;
    string lastMessage;

    public TrayApp(Config cfg)
    {
        this.cfg = cfg;
        switcher = new Switcher(cfg);
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        menu = new ContextMenuStrip();
        menu.Items.Add("Modo TV", null, (_, _) => Run(switcher.ToTv, "menu"));
        menu.Items.Add("Modo mesa", null, (_, _) => Run(switcher.ToDesk, "menu"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Configurar TV e atalho…", null, (_, _) => ShowSetup());
        menu.Items.Add("Sair", null, (_, _) => ExitThread());

        tray = new NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "Modo TV",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (cfg.IsConfigured) TogglePopup(); else ShowSetup();
        };

        hotkeyWindow = new HotkeyWindow(() => Run(switcher.Toggle, "Ctrl+Alt+F11"));
        if (!RegisterHotKey(hotkeyWindow.Handle, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_F11))
        {
            Log.Write("Ctrl+Alt+F11 já está em uso por outro programa.");
            Notify("Ctrl+Alt+F11 já está em uso por outro programa. O controle e o painel continuam funcionando.", true);
        }

        controller = new ControllerWatcher { HoldMs = cfg.ControllerHoldMs, Enabled = cfg.ControllerEnabled, Chord = cfg.ControllerChord };
        controller.Triggered += () =>
        {
            Log.Write($"Atalho do controle segurado ({controller.ConnectedName}).");
            ui.Post(_ => Run(switcher.Toggle, "controle"), null);
        };

        // Primeira execução: abre a configuração. Depois, só guarda a disposição da mesa se faltar.
        if (!cfg.IsConfigured) ui.Post(_ => ShowSetup(), null);
        else if (cfg.DeskLayout.Count == 0 && !switcher.IsTvMode()) switcher.RememberDesk();

        // Ao ligar a TV, o Windows costuma reativá-la como tela estendida (e mandar o som para ela).
        // No modo mesa, o app desfaz isso. O evento chega várias vezes seguidas, então espera assentar.
        wantTv = cfg.IsConfigured && switcher.IsTvMode();
        displayDebounce = new System.Windows.Forms.Timer { Interval = 1500 };
        displayDebounce.Tick += (_, _) => { displayDebounce.Stop(); GuardDesk(); };
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        ui.Post(_ => GuardDesk(), null); // também ao iniciar (ex.: PC ligado com a TV já ligada)
    }

    void OnDisplayChanged(object sender, EventArgs e)
    {
        if (DateTime.UtcNow < ignoreDisplayUntil) return; // mudança feita pelo próprio app
        ui.Post(_ => { displayDebounce.Stop(); displayDebounce.Start(); }, null);
    }

    void GuardDesk()
    {
        if (!cfg.KeepTvOffAtDesk || !cfg.IsConfigured || busy == 1 || setup != null) return;
        Task.Run(() =>
        {
            List<MonitorInfo> mons;
            try { mons = Display.GetMonitors(); } catch (Exception ex) { Log.Write("Falha ao ler as telas: " + ex.Message); return; }
            bool tvOn = mons.Any(m => m.Active && cfg.IsTv(m.Id));
            bool deskOn = mons.Any(m => m.Active && !cfg.IsTv(m.Id));
            // TV desligada no modo TV: o Windows volta sozinho para os monitores; passa a valer o modo mesa.
            if (wantTv) { if (!tvOn) wantTv = false; return; }
            if (!tvOn || !deskOn) return;
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            Log.Write("Windows reativou a TV no modo mesa; desligando de novo.");
            ignoreDisplayUntil = DateTime.UtcNow.AddSeconds(4);
            Result r;
            try { r = switcher.ToDesk(); }
            catch (Exception ex) { r = new(false, ex.Message); }
            finally { Interlocked.Exchange(ref busy, 0); }
            Log.Write($"Modo mesa restaurado: {(r.Ok ? "ok" : "falhou")} — {r.Message}");
            if (!r.Ok) ui.Post(_ => Notify(r.Message, true), null);
        });
    }

    void ShowSetup()
    {
        popup?.Hide();
        if (setup != null) { setup.Activate(); return; }
        setup = new SetupWindow(cfg, switcher, controller, IsAutostart, SetAutostart);
        setup.Saved += () => Notify($"Pronto! Use o atalho do controle ou Ctrl+Alt+F11 para ir para {cfg.DisplayName}.");
        setup.Closed += (_, _) => setup = null;
        setup.Show();
        setup.Activate();
    }

    // ---------- Painel ----------

    TrayPopup Popup()
    {
        if (popup != null) return popup;
        popup = new TrayPopup();
        popup.Toggle += () => Run(switcher.Toggle);
        popup.GoTv += () => Run(switcher.ToTv);
        popup.GoDesk += () => Run(switcher.ToDesk);
        popup.ToggleAudio += () => { cfg.SwitchAudio = !cfg.SwitchAudio; cfg.Save(); RefreshPopup(); };
        popup.ToggleStartup += () => { SetAutostart(!IsAutostart()); RefreshPopup(); };
        popup.TogglePad += () =>
        {
            cfg.ControllerEnabled = !cfg.ControllerEnabled;
            controller.Enabled = cfg.ControllerEnabled;
            cfg.Save();
            RefreshPopup();
        };
        popup.SetPreset += preset =>
        {
            if (cfg.TvPreset == preset) return;
            cfg.TvPreset = preset;
            cfg.Save();
            if (switcher.IsTvMode()) Run(switcher.ApplyTvPreset);
            else RefreshPopup();
        };
        popup.ShowMenu += () => { popup.Hide(); menu.Show(Cursor.Position); };
        return popup;
    }

    void TogglePopup()
    {
        var p = Popup();
        // O clique no ícone tira o foco do painel (que se esconde) antes de chegar aqui.
        if (p.IsVisible || (DateTime.UtcNow - p.HiddenAt).TotalMilliseconds < 250) { p.Hide(); return; }
        lastMessage = null;
        p.Apply(BuildState());
        p.ShowNearTray();
    }

    void RefreshPopup()
    {
        if (popup == null || !popup.IsVisible) return;
        Task.Run(BuildState).ContinueWith(t => ui.Post(_ => popup.Apply(t.Result), null));
    }

    PopupState BuildState()
    {
        var s = new PopupState
        {
            Busy = busy == 1,
            SwitchAudio = cfg.SwitchAudio,
            Autostart = IsAutostart(),
            ControllerEnabled = cfg.ControllerEnabled,
            PadName = controller.ConnectedName,
            ChordLabel = controller.ChordLabel,
            TvPreset = cfg.TvPreset,
            Presets = cfg.SuggestedPresets(),
            Title = string.IsNullOrEmpty(cfg.DisplayName) ? "Modo TV" : cfg.DisplayName,
            Brand = cfg.TvBrand ?? "",
            Status = lastMessage,
        };
        try
        {
            var mons = Display.GetMonitors();
            var tv = mons.FirstOrDefault(m => cfg.IsTv(m.Id) && m.Available);
            var desk = mons.Where(m => m.Active && !cfg.IsTv(m.Id)).OrderBy(m => m.X).ToList();
            s.TvAvailable = tv != null;
            s.TvMode = mons.Any(m => m.Active) && mons.Where(m => m.Active).All(m => cfg.IsTv(m.Id));
            s.DeskActive = desk.Count;
            s.DeskNames = desk.Count > 0 ? string.Join(" + ", desk.Select(Detector.Pretty)) : "Monitores desligados";
            var known = tv ?? mons.FirstOrDefault(m => cfg.IsTv(m.Id));
            s.Subtitle = string.Join(" · ", new[]
            {
                cfg.TvBrand, cfg.TvName,
                known != null && known.Inches > 0 ? $"{known.Inches:0}\"" : null,
            }.Where(x => !string.IsNullOrEmpty(x)).Distinct());
            s.TvResolution = tv != null && tv.Active
                ? Config.ShortRes(tv.Width, tv.Height) + (tv.Hz > 0 ? $" · {tv.Hz} Hz" : "")
                : (known?.Connection ?? "HDMI");
        }
        catch { }
        try
        {
            var a = Audio.GetDefault()?.Name ?? "";
            int paren = a.IndexOf(" (");
            s.AudioName = paren > 0 ? a[..paren] : a;
            if (s.AudioName == "Speakers" || s.AudioName == "Alto-falantes") s.AudioName = a.Contains("Realtek") ? "Realtek" : s.AudioName;
        }
        catch { }
        return s;
    }

    // ---------- Troca ----------

    void Run(Func<Result> action, string origin = "")
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) { Log.Write($"Troca ignorada ({origin}): outra troca em andamento."); return; }
        Log.Write($"Troca pedida ({origin}).");
        SystemSounds.Asterisk.Play(); // aviso sonoro: útil quando a tela está apagada
        popup?.Hide();
        Task.Run(() =>
        {
            Result r;
            try
            {
                r = action();
                ignoreDisplayUntil = DateTime.UtcNow.AddSeconds(4);
                if (r.Ok) wantTv = switcher.IsTvMode();
            }
            catch (Exception ex) { r = new(false, ex.Message); Log.Write("Erro na troca: " + ex); }
            finally { Interlocked.Exchange(ref busy, 0); }
            Log.Write($"Troca terminou ({origin}): {(r.Ok ? "ok" : "falhou")} — {r.Message}");
            ui.Post(_ =>
            {
                lastMessage = r.Ok ? null : r.Message;
                if (!r.Ok) SystemSounds.Hand.Play();
                Notify(r.Message, !r.Ok);
            }, null);
        });
    }

    void Notify(string text, bool error = false)
    {
        tray.ShowBalloonTip(3000, "Modo TV", text, error ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        displayDebounce.Dispose();
        UnregisterHotKey(hotkeyWindow.Handle, HotkeyId);
        hotkeyWindow.DestroyHandle();
        controller.Dispose();
        popup?.Close();
        tray.Visible = false;
        tray.Dispose();
        base.ExitThreadCore();
    }

    static bool IsAutostart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("ModoTV") is string;
    }

    static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue("ModoTV", $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue("ModoTV", false);
    }

    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var body = new SolidBrush(Color.FromArgb(76, 155, 255));
            g.FillRectangle(body, 2, 5, 28, 18);
            g.FillRectangle(Brushes.Black, 4, 7, 24, 14);
            using var pen = new Pen(Color.FromArgb(76, 155, 255), 2.5f);
            g.DrawLine(pen, 10, 29, 12, 23);
            g.DrawLine(pen, 22, 29, 20, 23);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    class HotkeyWindow : NativeWindow
    {
        readonly Action onHotkey;
        public HotkeyWindow(Action onHotkey) { this.onHotkey = onHotkey; CreateHandle(new CreateParams()); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY) onHotkey();
            base.WndProc(ref m);
        }
    }
}
