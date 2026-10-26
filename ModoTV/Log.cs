using System.IO;

namespace ModoTV;

// Registro curto de eventos em %LOCALAPPDATA%\ModoTV\modotv.log, para diagnosticar problemas.
// Escreve só quando algo acontece (troca, atalho, erro) e se limita a ~200 KB.
public static class Log
{
    static readonly object gate = new();
    static string FilePath => Path.Combine(Config.Dir, "modotv.log");

    public static void Write(string message)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Config.Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 200_000)
                {
                    var keep = File.ReadAllLines(FilePath).TakeLast(500);
                    File.WriteAllLines(FilePath, keep);
                }
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch { /* log nunca pode derrubar o app */ }
    }
}
