using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace ModoTV;

// Atualização pelas releases do GitHub: compara a versão da última release com a deste .exe,
// baixa o arquivo certo (leve ou portátil), troca o .exe e reabre o app.
public static class Updater
{
    const string Repo = "kekelxd/ModoTV";
    const string Api = $"https://api.github.com/repos/{Repo}/releases/latest";

    public record Info(Version Version, string Tag, string AssetUrl, string AssetName, string PageUrl);

    static readonly HttpClient http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"ModoTV/{Current}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static Version Current
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    static string ExePath => Environment.ProcessPath;

    // A versão portátil leva o .NET junto e passa de 60 MB; a leve tem menos de 1 MB.
    public static bool IsPortable
    {
        get { try { return new FileInfo(ExePath).Length > 20_000_000; } catch { return false; } }
    }

    // Devolve a release nova, ou null se já está na mais recente (ou sem internet).
    public static async Task<Info> CheckAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync(Api));
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var pr) && pr.GetBoolean()) return null;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
            latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
            if (latest <= Current) return null;

            string wanted = IsPortable ? "ModoTV-portatil.exe" : "ModoTV.exe";
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                if (!string.Equals(a.GetProperty("name").GetString(), wanted, StringComparison.OrdinalIgnoreCase)) continue;
                return new Info(latest, tag, a.GetProperty("browser_download_url").GetString(), wanted,
                                root.GetProperty("html_url").GetString());
            }
            Log.Write($"Release {tag} não tem o arquivo {wanted}.");
        }
        catch (Exception ex) { Log.Write("Não consegui verificar atualizações: " + ex.Message); }
        return null;
    }

    // Baixa e troca o .exe. O Windows deixa renomear um .exe em uso, então o atual vira ".old"
    // e o novo assume o nome. Depois o app novo é aberto e espera este fechar.
    public static async Task InstallAsync(Info info)
    {
        string exe = ExePath, tmp = exe + ".new", old = exe + ".old";
        Log.Write($"Baixando {info.AssetName} da versão {info.Version}…");
        using (var resp = await http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(tmp);
            await resp.Content.CopyToAsync(fs);
        }

        // Confere se veio um executável de verdade antes de trocar.
        var head = new byte[2];
        await using (var check = File.OpenRead(tmp)) { await check.ReadAsync(head); if (check.Length < 100_000) head[0] = 0; }
        if (head[0] != (byte)'M' || head[1] != (byte)'Z')
        {
            File.Delete(tmp);
            throw new InvalidOperationException("O arquivo baixado não é um programa válido.");
        }

        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try { File.Move(tmp, exe); }
        catch { File.Move(old, exe); throw; } // devolve o antigo se algo der errado

        Process.Start(new ProcessStartInfo(exe, $"--after-update {Environment.ProcessId}") { UseShellExecute = false });
        Log.Write($"Atualizado para {info.Version}; reabrindo.");
    }

    // Chamado na inicialização: apaga o .exe antigo que ficou da atualização.
    public static void CleanupOld()
    {
        try { var old = ExePath + ".old"; if (File.Exists(old)) File.Delete(old); } catch { }
    }
}
