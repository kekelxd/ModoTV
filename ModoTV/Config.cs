using System.IO;
using System.Text.Json;

namespace ModoTV;

public class Config
{
    // ---------- TV (preenchido pela tela de configuração) ----------
    // IDs de hardware da TV, tirados do EDID (ex.: "XXX8000"). Vazio = ainda não configurado.
    public List<string> TvMonitorIds { get; set; } = new();
    public string TvName { get; set; } = "";      // nome do EDID, ex.: "Smart TV Pro"
    public string TvBrand { get; set; } = "";     // marca exibida no desenho, ex.: "TCL"
    // Trecho do nome da saída de áudio da TV.
    public List<string> TvAudioNames { get; set; } = new();
    // Nome mostrado no painel.
    public string DisplayName { get; set; } = "";

    // ---------- Preferências ----------
    public bool SwitchAudio { get; set; } = true;
    public bool ControllerEnabled { get; set; } = true;
    // Quanto tempo segurar o atalho do controle para trocar.
    public int ControllerHoldMs { get; set; } = 1500;
    // Botões do atalho (combinação de Pad). Padrão: Select/View/Create + Start/Menu/Options.
    public Pad ControllerChord { get; set; } = PadNames.DefaultChord;
    // Resolução da TV no modo TV: "auto" (não mexe) ou "3840x2160@60".
    public string TvPreset { get; set; } = "auto";
    // Modos que a TV ofereceu da última vez que esteve ativa (ex.: "2560x1440@120").
    public List<string> TvModes { get; set; } = new();

    // ---------- Estado da mesa (salvo sozinho ao entrar no modo TV) ----------
    public Dictionary<string, Point> DeskLayout { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string DeskAudioId { get; set; }

    public bool IsConfigured => TvMonitorIds.Count > 0;

    // Fica sempre na pasta do usuário, assim o .exe pode morar em qualquer lugar.
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModoTV");
    public static string FilePath => Path.Combine(Dir, "config.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath), Json) ?? new Config();
                cfg.DeskLayout = new Dictionary<string, Point>(cfg.DeskLayout ?? new(), StringComparer.OrdinalIgnoreCase);
                cfg.TvMonitorIds ??= new(); cfg.TvAudioNames ??= new(); cfg.TvModes ??= new();
                if (PadNames.Count(cfg.ControllerChord) < 2) cfg.ControllerChord = PadNames.DefaultChord;
                // Nomes antigos (v1.2) das resoluções.
                if (cfg.TvPreset == "4K60") cfg.TvPreset = "3840x2160@60";
                if (cfg.TvPreset == "1440p120") cfg.TvPreset = "2560x1440@120";
                return cfg;
            }
        }
        catch { /* arquivo corrompido: volta ao padrão */ }
        return new Config();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }

    public bool IsTv(string monitorId) => TvMonitorIds.Any(t => string.Equals(t, monitorId, StringComparison.OrdinalIgnoreCase));

    // ---------- Resoluções ----------

    public static bool TryParseMode(string s, out (int W, int H, int Hz) mode)
    {
        mode = default;
        var m = System.Text.RegularExpressions.Regex.Match(s ?? "", @"^(\d+)x(\d+)@(\d+)$");
        if (!m.Success) return false;
        mode = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
        return true;
    }

    public static string ModeKey(int w, int h, int hz) => $"{w}x{h}@{hz}";

    public static string ShortRes(int w, int h) => h switch
    {
        2160 => "4K", 1440 => "1440p", 1080 => "1080p", 720 => "720p", _ => $"{w}x{h}",
    };

    // Até duas opções: a mais nítida e a mais fluida (se for diferente).
    public List<string> SuggestedPresets()
    {
        var modes = TvModes.Select(k => TryParseMode(k, out var m) ? m : default)
                           .Where(m => m.W > 0 && m.Hz >= 50 && m.W * 9 == m.H * 16) // só 16:9, como as TVs
                           .ToList();
        if (modes.Count == 0) return new();
        var sharp = modes.OrderByDescending(m => m.W * m.H).ThenByDescending(m => m.Hz).First();
        var list = new List<string> { ModeKey(sharp.W, sharp.H, sharp.Hz) };
        var smooth = modes.Where(m => m.Hz > sharp.Hz).OrderByDescending(m => m.Hz).ThenByDescending(m => m.W * m.H).FirstOrDefault();
        if (smooth.W > 0)
        {
            // Entre as frequências altas, fica com a maior resolução que ainda passa de 100 Hz.
            var best = modes.Where(m => m.Hz >= Math.Min(smooth.Hz, 100) && m.Hz > sharp.Hz)
                            .OrderByDescending(m => m.W * m.H).ThenByDescending(m => m.Hz).First();
            list.Add(ModeKey(best.W, best.H, best.Hz));
        }
        return list;
    }
}
