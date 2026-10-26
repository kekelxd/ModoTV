namespace ModoTV;

public record Result(bool Ok, string Message);

public class Switcher
{
    readonly Config cfg;
    readonly object gate = new();

    public Switcher(Config cfg) { this.cfg = cfg; }

    public bool IsTvMode()
    {
        if (!cfg.IsConfigured) return false;
        var active = Display.GetMonitors().Where(m => m.Active).ToList();
        return active.Count > 0 && active.All(m => cfg.IsTv(m.Id));
    }

    public Result Toggle()
    {
        bool tv;
        try { tv = IsTvMode(); }
        catch (Exception ex) { return new(false, "Não consegui ler as telas: " + ex.Message); }
        return tv ? ToDesk() : ToTv();
    }

    public Result ToTv()
    {
        lock (gate)
        {
            try
            {
                if (!cfg.IsConfigured) return new(false, "Configure sua TV primeiro: clique no ícone do Modo TV.");
                var mons = Display.GetMonitors();
                var tv = mons.FirstOrDefault(m => cfg.IsTv(m.Id) && m.Available);
                if (tv == null) return new(false, "TV não detectada. Ligue a TV e tente de novo.");

                bool alreadyTv = mons.Where(m => m.Active).All(m => cfg.IsTv(m.Id));
                if (!alreadyTv) RememberDesk(mons);

                Display.Apply(new[] { tv.Id }, new Dictionary<string, Point> { [tv.Id] = Point.Empty });
                RememberTvModes(tv.Id);
                string res = ApplyPreset(tv.Id);

                string audio = "";
                if (cfg.SwitchAudio)
                {
                    var dev = Audio.SetDefaultMatching(IsTvAudio, 8000);
                    audio = dev != null ? $" Som: {dev.Name}." : " (Saída de áudio da TV não encontrada.)";
                }
                return new(true, $"Modo TV ativo{res}.{audio}");
            }
            catch (Exception ex) { return new(false, ex.Message); }
        }
    }

    // Aplica a resolução escolhida (só tem efeito com a TV ligada no modo TV).
    public Result ApplyTvPreset()
    {
        lock (gate)
        {
            try
            {
                var tv = Display.GetMonitors().FirstOrDefault(m => m.Active && cfg.IsTv(m.Id));
                if (tv == null) return new(true, "Resolução salva. Ela é aplicada ao entrar no modo TV.");
                return new(true, "TV" + ApplyPreset(tv.Id) + ".");
            }
            catch (Exception ex) { return new(false, ex.Message); }
        }
    }

    string ApplyPreset(string tvId)
    {
        if (!Config.TryParseMode(cfg.TvPreset, out var p)) return ""; // "auto": deixa como o Windows escolher
        string label = $"{Config.ShortRes(p.W, p.H)} · {p.Hz} Hz";
        Thread.Sleep(300);
        try
        {
            return Display.SetMode(tvId, p.W, p.H, p.Hz) ? $" em {label}" : $" (a TV não ofereceu {label})";
        }
        catch (Exception ex) { return $" ({ex.Message})"; }
    }

    // Guarda as resoluções que a TV oferece, para o painel mostrar as opções certas.
    void RememberTvModes(string tvId)
    {
        try
        {
            var modes = Display.GetModes(tvId);
            if (modes.Count == 0 || modes.ToHashSet().SetEquals(cfg.TvModes)) return;
            cfg.TvModes = modes.OrderBy(m => m).ToList();
            cfg.Save();
        }
        catch { }
    }

    bool IsTvAudio(Audio.Device d)
    {
        var names = cfg.TvAudioNames.Count > 0 ? cfg.TvAudioNames : new List<string> { cfg.TvName };
        return names.Any(n => !string.IsNullOrEmpty(n) && d.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
    }

    // Usado pela tela de configuração: TV por alguns segundos e volta.
    public Result Test(int seconds)
    {
        var r = ToTv();
        if (!r.Ok) return r;
        Thread.Sleep(seconds * 1000);
        var back = ToDesk();
        return back.Ok ? new(true, r.Message) : back;
    }

    public Result ToDesk()
    {
        lock (gate)
        {
            try
            {
                var mons = Display.GetMonitors();
                var ids = cfg.DeskLayout.Keys
                    .Where(id => mons.Any(m => m.Available && string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (ids.Count == 0)
                    ids = mons.Where(m => m.Available && !cfg.IsTv(m.Id)).Select(m => m.Id).ToList();
                if (ids.Count == 0) return new(false, "Nenhum monitor da mesa conectado.");

                Display.Apply(ids, cfg.DeskLayout);

                string audio = "";
                if (cfg.SwitchAudio && !string.IsNullOrEmpty(cfg.DeskAudioId))
                {
                    var dev = Audio.SetDefaultMatching(d => d.Id == cfg.DeskAudioId, 5000);
                    audio = dev != null ? $" Som: {dev.Name}." : "";
                }
                return new(true, $"Modo mesa ativo.{audio}");
            }
            catch (Exception ex) { return new(false, ex.Message); }
        }
    }

    // Guarda a disposição atual dos monitores (sem a TV) e o áudio, para a volta.
    public void RememberDesk() { lock (gate) RememberDesk(Display.GetMonitors()); }

    void RememberDesk(List<MonitorInfo> mons)
    {
        var desk = mons.Where(m => m.Active && !cfg.IsTv(m.Id)).ToList();
        if (desk.Count > 0)
        {
            // A principal fica em (0,0); se ela era a TV, a mais à esquerda assume.
            var anchor = desk.FirstOrDefault(m => m.Primary) ?? desk.OrderBy(m => m.X).ThenBy(m => m.Y).First();
            cfg.DeskLayout = desk.ToDictionary(m => m.Id, m => new Point(m.X - anchor.X, m.Y - anchor.Y),
                StringComparer.OrdinalIgnoreCase);
        }
        var current = Audio.GetDefault();
        if (current != null && !IsTvAudio(current))
            cfg.DeskAudioId = current.Id;
        cfg.Save();
    }

    public string Status()
    {
        var lines = new List<string> { "Telas:" };
        foreach (var m in Display.GetMonitors().OrderByDescending(m => m.Active).ThenBy(m => m.X))
        {
            string state = m.Active ? $"ativa {m.Width}x{m.Height} em ({m.X},{m.Y}){(m.Primary ? " PRINCIPAL" : "")}"
                         : m.Available ? "conectada, desligada" : "desconectada";
            lines.Add($"  {m.Id,-9} {m.Name,-14} {(cfg.IsTv(m.Id) ? "[TV] " : "     ")}{m.Connection,-11} {(m.Inches > 0 ? m.Inches + "\"" : "?"),-4} {state}");
        }
        var best = Detector.Rank(Display.GetMonitors()).FirstOrDefault();
        if (best != null)
            lines.Add($"Detecção: {Detector.Pretty(best.Monitor)} (pontos {best.Score}: {string.Join(", ", best.Reasons)}){(best.LooksLikeTv ? "" : " — incerto")}");
        var def = Audio.GetDefault();
        lines.Add("Áudio:");
        foreach (var d in Audio.GetOutputs())
            lines.Add($"  {(d.Id == def?.Id ? "*" : " ")} {d.Name}");
        lines.Add($"Modo atual: {(IsTvMode() ? "TV" : "mesa")}");
        return string.Join(Environment.NewLine, lines);
    }
}
