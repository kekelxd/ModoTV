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
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(cfg));
        return 0;
    }

    static int Print(Result r)
    {
        Console.WriteLine(r.Message);
        return r.Ok ? 0 : 1;
    }
}
