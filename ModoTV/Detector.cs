namespace ModoTV;

public class TvCandidate
{
    public MonitorInfo Monitor;
    public int Score;
    public List<string> Reasons = new();
    public bool LooksLikeTv => Score >= 4;
    public string Brand => Detector.BrandOf(Monitor);
}

// Adivinha qual tela é a TV. O usuário sempre confirma antes de salvar.
public static class Detector
{
    // Códigos PNP de fabricante (3 primeiras letras do ID do EDID).
    static readonly Dictionary<string, string> Brands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AOC"] = "AOC", ["GSM"] = "LG", ["SAM"] = "Samsung", ["SEC"] = "Samsung", ["DEL"] = "Dell", ["AUS"] = "ASUS",
        ["ACI"] = "ASUS", ["BNQ"] = "BenQ", ["ACR"] = "Acer", ["MSI"] = "MSI", ["HPN"] = "HP", ["HWP"] = "HP",
        ["LEN"] = "Lenovo", ["GBT"] = "Gigabyte", ["VSC"] = "ViewSonic", ["PHL"] = "Philips", ["TCL"] = "TCL",
        ["SNY"] = "Sony", ["HEC"] = "Hisense", ["HIS"] = "Hisense", ["VIZ"] = "Vizio", ["SHP"] = "Sharp",
        ["MEI"] = "Panasonic", ["TOS"] = "Toshiba", ["SKY"] = "Skyworth", ["XMI"] = "Xiaomi", ["AOS"] = "AOC",
    };

    // Marcas que praticamente só fazem TVs.
    static readonly HashSet<string> TvOnlyBrands = new(StringComparer.OrdinalIgnoreCase)
    { "TCL", "SNY", "HEC", "HIS", "VIZ", "SHP", "MEI", "TOS", "SKY", "XMI" };

    public static string BrandOf(MonitorInfo m) =>
        m.Id.Length >= 3 && Brands.TryGetValue(m.Id[..3], out var b) ? b : "";

    public static List<TvCandidate> Rank(List<MonitorInfo> monitors)
    {
        var list = new List<TvCandidate>();
        foreach (var m in monitors.Where(m => m.Available))
        {
            var c = new TvCandidate { Monitor = m };
            if (m.Output == 5) { c.Score += 2; c.Reasons.Add("ligada no HDMI"); }
            if (System.Text.RegularExpressions.Regex.IsMatch(m.Name ?? "", @"\bTV\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            { c.Score += 4; c.Reasons.Add("nome de TV"); }
            if (m.Inches >= 40) { c.Score += 4; c.Reasons.Add($"{m.Inches:0}\""); }
            else if (m.Inches >= 32) { c.Score += 2; c.Reasons.Add($"{m.Inches:0}\""); }
            if (m.Id.Length >= 3 && TvOnlyBrands.Contains(m.Id[..3])) { c.Score += 3; c.Reasons.Add("marca de TV"); }
            if (m.Output == 0x80000000) c.Score -= 10; // tela de notebook
            list.Add(c);
        }
        return list.OrderByDescending(c => c.Score).ThenByDescending(c => c.Monitor.Inches).ToList();
    }

    // Nome mais apresentável: "LG ULTRAGEAR" -> "LG Ultragear", "25G4S" -> "AOC 25G4S".
    public static string Pretty(MonitorInfo m)
    {
        string brand = BrandOf(m);
        string name = m.Name ?? m.Id;
        if (!string.IsNullOrEmpty(brand) && name.StartsWith(brand + " ", StringComparison.OrdinalIgnoreCase))
            name = name[(brand.Length + 1)..];
        // Palavras só em maiúsculas e sem números viram "Título": "ULTRAGEAR" -> "Ultragear".
        if (name.Length > 3 && name == name.ToUpperInvariant() && name.Any(char.IsLetter) && !name.Any(char.IsDigit))
            name = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());
        return string.IsNullOrEmpty(brand) || name.StartsWith(brand, StringComparison.OrdinalIgnoreCase) ? name : $"{brand} {name}";
    }

    // Saída de áudio da TV: a placa de vídeo dá ao áudio HDMI o mesmo nome do EDID.
    // Procura também saídas desligadas, porque a TV pode estar apagada no modo mesa.
    public static string FindTvAudio(MonitorInfo tv)
    {
        try
        {
            var dev = Audio.GetOutputs(includeInactive: true)
                .FirstOrDefault(d => !string.IsNullOrEmpty(tv.Name) && d.Name.StartsWith(tv.Name + " (", StringComparison.OrdinalIgnoreCase));
            return dev == null ? null : dev.Name[..dev.Name.IndexOf(" (", StringComparison.Ordinal)];
        }
        catch { return null; }
    }
}
