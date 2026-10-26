using System.Runtime.InteropServices;

namespace ModoTV;

static class Program
{
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    // Sem argumentos: fica na bandeja. Com argumento: executa e sai
    //   ModoTV tv | mesa | toggle | status | teste  (teste = TV por 10 s e volta)
    [STAThread]
    static int Main(string[] args)
    {
        var cfg = Config.Load();

        // Aberto pela atualização: espera a versão antiga fechar e segue como app normal.
        bool updated = false;
        if (args.Length >= 2 && args[0] == "--after-update")
        {
            try { if (int.TryParse(args[1], out int pid)) System.Diagnostics.Process.GetProcessById(pid).WaitForExit(15_000); } catch { }
            args = Array.Empty<string>();
            updated = true;
        }

        if (args.Length > 0)
        {
            AttachConsole(-1);
            var sw = new Switcher(cfg);
            switch (args[0].ToLowerInvariant())
            {
                case "tv": return Print(sw.ToTv());
                case "mesa": return Print(sw.ToDesk());
                case "toggle": return Print(sw.Toggle());
                case "status":
                    Console.WriteLine(sw.Status());
                    using (var pads = new ControllerWatcher())
                    {
                        Thread.Sleep(1500);
                        Console.WriteLine($"Controle: {pads.ConnectedName ?? "nenhum"} (atalho: {pads.ChordLabel})");
                    }
                    return 0;
                case "preview":
                    bool tv = args.Length > 2 && args[2] == "tv";
                    TrayPopup.RenderPreview(new PopupState
                    {
                        TvMode = tv, TvAvailable = true, DeskActive = tv ? 0 : 2,
                        DeskNames = "AOC 25G4S + LG Ultragear", TvResolution = tv ? "1440p · 120 Hz" : "HDMI", TvPreset = "2560x1440@120",
                        Presets = new() { "3840x2160@60", "2560x1440@120" }, Brand = "TCL",
                        Title = "TV do quarto", Subtitle = "TCL · Smart TV Pro · 52\"",
                        AudioName = tv ? "Smart TV Pro" : "Realtek", SwitchAudio = true, Autostart = true,
                        ControllerEnabled = true, PadName = "8BitDo Ultimate 2", ChordLabel = "Select + Start",
                        UpdateVersion = args.Length > 3 ? args[3] : null,
                    }, args[1]);
                    return 0;
                case "preview-setup":
                    // Simula a primeira execução (sem TV salva) com as telas reais deste PC.
                    var setupCfg = new Config();
                    var watcher = new ControllerWatcher();
                    Thread.Sleep(1500); // dá tempo de achar o controle
                    var win = new SetupWindow(setupCfg, new Switcher(setupCfg), watcher, () => false, _ => { });
                    TrayPopup.Snapshot(win, win.Root, args[1]);
                    return 0;
                case "diag":
                    // Pergunta ao Windows se aceitaria ir para a TV e voltar, sem mudar nada na tela.
                    var mons = Display.GetMonitors();
                    var tvMon = mons.FirstOrDefault(m => cfg.IsTv(m.Id) && m.Available);
                    if (tvMon == null) { Console.WriteLine("TV não disponível"); return 1; }
                    Console.WriteLine("Só a TV:   " + Display.Apply(new[] { tvMon.Id }, null, validateOnly: true));
                    var deskIds = mons.Where(m => m.Available && !cfg.IsTv(m.Id)).Select(m => m.Id).ToList();
                    Console.WriteLine("Só a mesa: " + Display.Apply(deskIds, null, validateOnly: true));
                    return 0;
                case "diag-ligar":
                    // Liga a TV como tela estendida pelo GDI (os monitores continuam ligados).
                    var tvM = Display.GetMonitors().FirstOrDefault(m => cfg.IsTv(m.Id) && m.Available);
                    if (tvM == null) { Console.WriteLine("TV não disponível"); return 1; }
                    Console.WriteLine(Display.TryAttach(tvM.Id));
                    Thread.Sleep(500);
                    Console.WriteLine(sw.Status());
                    return 0;
                case "atualizar":
                    // Procura e instala a versão nova pela linha de comando.
                    var info = Updater.CheckAsync().GetAwaiter().GetResult();
                    if (info == null) { Console.WriteLine($"Já está na versão mais recente ({Updater.Current})."); return 0; }
                    Console.WriteLine($"Atualizando {Updater.Current} → {info.Version} ({info.AssetName})…");
                    Updater.InstallAsync(info).GetAwaiter().GetResult();
                    Console.WriteLine("Pronto.");
                    return 0;
                case "configurar":
                    // Abre só a tela de configuração (sem a bandeja).
                    new SetupWindow(cfg, sw, new ControllerWatcher(), () => false, _ => { }).ShowDialog();
                    return 0;
                case "icone":
                    // Salva o ícone da bandeja (normal e com aviso) como PNG, para conferir.
                    Console.WriteLine($"Tamanho pedido pela bandeja: {SystemInformation.SmallIconSize}");
                    using (var ic = AppIcon.Tray()) { Console.WriteLine($"Ícone: {ic.Width}x{ic.Height}"); ic.ToBitmap().Save(args[1] + "-normal.png"); }
                    using (var ib = AppIcon.TrayWithBadge()) ib.ToBitmap().Save(args[1] + "-aviso.png");
                    return 0;
                case "teste":
                    var r = sw.ToTv();
                    Print(r);
                    if (!r.Ok) return 1;
                    Thread.Sleep(10_000);
                    return Print(sw.ToDesk());
                default:
                    Console.WriteLine("Uso: ModoTV [tv | mesa | toggle | status | teste]");
                    return 2;
            }
        }

        using var mutex = new Mutex(true, "ModoTV.SingleInstance", out bool first);
        if (!first) return 0;
        Log.Write($"ModoTV {typeof(Program).Assembly.GetName().Version} iniciado.");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Erro fatal: " + e.ExceptionObject);
        Application.ThreadException += (_, e) => Log.Write("Erro na interface: " + e.Exception);
        Updater.CleanupOld();
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(cfg, updated));
        return 0;
    }

    static int Print(Result r)
    {
        Console.WriteLine(r.Message);
        return r.Ok ? 0 : 1;
    }
}
