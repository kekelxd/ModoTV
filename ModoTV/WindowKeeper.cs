using System.Runtime.InteropServices;
using System.Text;

namespace ModoTV;

// Ao ir para a TV, o Windows joga todas as janelas para ela e, na volta, deixa tudo no monitor
// principal. Aqui guardamos a posição de cada janela antes e devolvemos cada uma ao seu lugar.
public static class WindowKeeper
{
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct Pt { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct Placement { public int length, flags, showCmd; public Pt min, max; public Rect normal; }

    delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr p);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref Placement p);
    [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr h, ref Placement p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);

    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, DWMWA_CLOAKED = 14;
    const int SW_SHOWMINIMIZED = 2, SW_SHOWMAXIMIZED = 3, WPF_ASYNCWINDOWPLACEMENT = 4;

    record Saved(IntPtr Handle, Placement Placement, Rect Bounds);
    static List<Saved> saved = new();

    // Janelas de aplicativos de verdade: visíveis, com título, sem dono e não escondidas pelo Windows.
    static List<Saved> Capture()
    {
        var list = new List<Saved>();
        uint self = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindowTextLength(h) == 0) return true;
            if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            if (GetWindow(h, 4) != IntPtr.Zero) return true; // GW_OWNER: diálogos e janelas filhas
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == self) return true;
            var p = new Placement { length = Marshal.SizeOf<Placement>() };
            if (!GetWindowPlacement(h, ref p) || !GetWindowRect(h, out var r)) return true;
            list.Add(new Saved(h, p, r));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static void Remember()
    {
        try { saved = Capture(); Log.Write($"Posição de {saved.Count} janelas guardada."); }
        catch (Exception ex) { Log.Write("Falha ao guardar janelas: " + ex.Message); }
    }

    // Devolve as janelas que saíram do lugar. Roda duas vezes porque o Windows ainda
    // reorganiza janelas por um instante depois que os monitores voltam.
    public static void Restore()
    {
        var list = saved;
        if (list.Count == 0) return;
        var moved = new HashSet<IntPtr>();
        foreach (var delay in new[] { 900, 1600 })
        {
            Thread.Sleep(delay);
            foreach (var s in list)
            {
                try
                {
                    if (!IsWindow(s.Handle)) continue;
                    var now = new Placement { length = Marshal.SizeOf<Placement>() };
                    if (!GetWindowPlacement(s.Handle, ref now) || !GetWindowRect(s.Handle, out var r)) continue;
                    bool same = now.showCmd == s.Placement.showCmd
                                && r.L == s.Bounds.L && r.T == s.Bounds.T && r.R == s.Bounds.R && r.B == s.Bounds.B;
                    if (same) continue;
                    var p = s.Placement;
                    p.flags |= WPF_ASYNCWINDOWPLACEMENT; // não trava se o app estiver ocupado
                    // Sem roubar o foco: normal e minimizada voltam sem ativar a janela.
                    if (p.showCmd == 1) p.showCmd = 4;                 // SW_SHOWNOACTIVATE
                    else if (p.showCmd == SW_SHOWMINIMIZED) p.showCmd = 7; // SW_SHOWMINNOACTIVE
                    if (p.showCmd == SW_SHOWMAXIMIZED && now.showCmd == SW_SHOWMAXIMIZED)
                    {
                        // Maximizada em outro monitor: restaura no lugar certo e maximiza de novo lá.
                        var normal = p; normal.showCmd = 4; // SW_SHOWNOACTIVATE
                        SetWindowPlacement(s.Handle, ref normal);
                    }
                    SetWindowPlacement(s.Handle, ref p);
                    moved.Add(s.Handle);
                }
                catch { /* janela fechou no meio */ }
            }
        }
        Log.Write($"Janelas recolocadas no lugar: {moved.Count} de {list.Count} (as outras já estavam certas).");
        saved = new();
    }
}
