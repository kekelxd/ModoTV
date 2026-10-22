using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ModoTV;

public class MonitorInfo
{
    public string Id;          // ID de hardware do EDID, ex.: "AOCB420"
    public string Name;        // Nome amigável, ex.: "25G4S"
    public bool Available;     // Conectado (cabo + monitor respondendo)
    public bool Active;        // Faz parte da área de trabalho
    public int X, Y, Width, Height, Hz;
    public bool Primary => Active && X == 0 && Y == 0;
    public string Instance;    // usado para achar o EDID no registro
    public uint Output;        // tipo de conexão (DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY)
    public double Inches;      // diagonal informada pelo EDID (0 = não informou)

    public string Connection => Output switch
    {
        0 => "VGA", 4 => "DVI", 5 => "HDMI", 10 => "DisplayPort", 11 => "DisplayPort",
        0x80000000 => "Tela interna", _ => "Outra",
    };
}

// Liga/desliga telas e define posições pela API CCD do Windows
// (QueryDisplayConfig/SetDisplayConfig), a mesma usada pelas Configurações de Vídeo.
public static class Display
{
    const uint QDC_ALL_PATHS = 0x1, QDC_ONLY_ACTIVE_PATHS = 0x2;
    const uint SDC_TOPOLOGY_SUPPLIED = 0x10, SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20, SDC_APPLY = 0x80,
               SDC_SAVE_TO_DATABASE = 0x200, SDC_ALLOW_CHANGES = 0x400, SDC_ALLOW_PATH_ORDER_CHANGES = 0x2000;
    const uint PATH_ACTIVE = 0x1;
    const uint IDX_INVALID = 0xFFFFFFFF;
    const uint MODE_SOURCE = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct LUID { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    struct PathSource { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    struct PathTarget
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology;
        public uint rotation; public uint scaling; public uint refreshNum, refreshDen;
        public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PathInfo { public PathSource source; public PathTarget target; public uint flags; }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    struct ModeInfo
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(16)] public uint width;
        [FieldOffset(20)] public uint height;
        [FieldOffset(24)] public uint pixelFormat;
        [FieldOffset(28)] public int x;
        [FieldOffset(32)] public int y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct TargetDeviceName
    {
        public uint type; public uint size; public LUID adapterId; public uint id;
        public uint flags; public uint outputTechnology; public ushort edidManufactureId; public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string friendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string devicePath;
    }

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, [Out] ModeInfo[] modes, IntPtr topologyId);
    [DllImport("user32.dll")] static extern int SetDisplayConfig(uint numPaths, PathInfo[] paths, uint numModes, ModeInfo[] modes, uint flags);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref TargetDeviceName request);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName request);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SourceDeviceName
    {
        public uint type; public uint size; public LUID adapterId; public uint id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string gdiName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string deviceName;
        public short specVersion, driverVersion, size, driverExtra;
        public int fields, positionX, positionY, displayOrientation, displayFixedOutput;
        public short color, duplex, yResolution, ttOption, collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string formName;
        public short logPixels;
        public int bitsPerPel, pelsWidth, pelsHeight, displayFlags, displayFrequency;
        public int icmMethod, icmIntent, mediaType, ditherType, reserved1, reserved2, panningWidth, panningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string device, int mode, ref DevMode dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string device, ref DevMode dm, IntPtr hwnd, uint flags, IntPtr param);

    static (PathInfo[] paths, ModeInfo[] modes) Query(uint flags)
    {
        for (int attempt = 0; ; attempt++)
        {
            Check(GetDisplayConfigBufferSizes(flags, out uint np, out uint nm), "GetDisplayConfigBufferSizes");
            var paths = new PathInfo[np];
            var modes = new ModeInfo[nm];
            int r = QueryDisplayConfig(flags, ref np, paths, ref nm, modes, IntPtr.Zero);
            if (r == 122 && attempt < 3) continue; // ERROR_INSUFFICIENT_BUFFER: configuração mudou no meio
            Check(r, "QueryDisplayConfig");
            Array.Resize(ref paths, (int)np);
            Array.Resize(ref modes, (int)nm);
            return (paths, modes);
        }
    }

    static void Check(int result, string what)
    {
        if (result != 0) throw new InvalidOperationException($"{what} falhou (erro {result}).");
    }

    static (string id, string name, string instance) TargetIdentity(PathTarget t)
    {
        var req = new TargetDeviceName { type = 2, adapterId = t.adapterId, id = t.id };
        req.size = (uint)Marshal.SizeOf<TargetDeviceName>();
        if (DisplayConfigGetDeviceInfo(ref req) != 0 || string.IsNullOrEmpty(req.devicePath)) return (null, null, null);
        // \\?\DISPLAY#AOCB420#5&1057eb04&a&UID41217#{...}
        var parts = req.devicePath.Split('#');
        string id = parts.Length > 1 ? parts[1].ToUpperInvariant() : req.devicePath;
        string instance = parts.Length > 2 ? parts[2] : null;
        return (id, string.IsNullOrEmpty(req.friendlyName) ? id : req.friendlyName, instance);
    }

    // Diagonal em polegadas a partir do EDID (bytes 21 e 22 = largura e altura em cm).
    static double ReadInches(string id, string instance)
    {
        if (id == null || instance == null) return 0;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{id}\{instance}\Device Parameters");
            if (k?.GetValue("EDID") is not byte[] edid || edid.Length < 23) return 0;
            int w = edid[21], h = edid[22];
            if (w == 0 || h == 0) return 0; // 0 = tamanho variável (projetor) ou não informado
            return Math.Round(Math.Sqrt(w * w + h * h) / 2.54);
        }
        catch { return 0; }
    }

    static string Key(PathTarget t) => $"{t.adapterId.Low}:{t.adapterId.High}:{t.id}";
    static string SourceKey(PathSource s) => $"{s.adapterId.Low}:{s.adapterId.High}:{s.id}";

    public static List<MonitorInfo> GetMonitors()
    {
        var (paths, modes) = Query(QDC_ALL_PATHS);
        var byTarget = new Dictionary<string, MonitorInfo>();
        foreach (var p in paths)
        {
            string key = Key(p.target);
            if (!byTarget.TryGetValue(key, out var m))
            {
                var (id, name, instance) = TargetIdentity(p.target);
                if (id == null) continue;
                m = new MonitorInfo { Id = id, Name = name, Instance = instance, Output = p.target.outputTechnology };
                byTarget[key] = m;
            }
            if (p.target.targetAvailable != 0) m.Available = true;
            if ((p.flags & PATH_ACTIVE) != 0)
            {
                m.Active = true;
                if (p.source.modeInfoIdx < modes.Length)
                {
                    var sm = modes[p.source.modeInfoIdx];
                    m.X = sm.x; m.Y = sm.y; m.Width = (int)sm.width; m.Height = (int)sm.height;
                    if (p.target.refreshDen != 0) m.Hz = (int)Math.Round((double)p.target.refreshNum / p.target.refreshDen);
                }
            }
        }
        // O mesmo monitor pode aparecer em mais de uma saída; fica com a entrada mais "viva".
        var list = byTarget.Values
            .GroupBy(m => m.Id)
            .Select(g => g.OrderByDescending(m => m.Active).ThenByDescending(m => m.Available).First())
            .ToList();
        foreach (var m in list) m.Inches = ReadInches(m.Id, m.Instance);
        return list;
    }

    // Deixa ativas exatamente as telas em `ids` e posiciona cada uma conforme `positions`.
    // A tela na posição (0,0) vira a principal.
    public static void Apply(IList<string> ids, IDictionary<string, Point> positions)
    {
        var (paths, modes) = Query(QDC_ALL_PATHS);
        var identity = new Dictionary<string, string>();
        string IdOf(PathTarget t)
        {
            string k = Key(t);
            if (!identity.TryGetValue(k, out var id)) identity[k] = id = TargetIdentity(t).id;
            return id;
        }

        var wanted = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        var selected = new List<PathInfo>();
        var usedSources = new HashSet<string>();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) Telas já ativas que devem continuar: mantém o caminho e o modo atuais.
        foreach (var p in paths)
        {
            if ((p.flags & PATH_ACTIVE) == 0) continue;
            string id = IdOf(p.target);
            if (id == null || !wanted.Contains(id) || done.Contains(id)) continue;
            selected.Add(p);
            usedSources.Add(SourceKey(p.source));
            done.Add(id);
        }

        // 2) Telas que precisam ser ligadas: escolhe uma saída de vídeo livre.
        foreach (var id in wanted)
        {
            if (done.Contains(id)) continue;
            bool found = false;
            foreach (var p in paths)
            {
                if (p.target.targetAvailable == 0) continue;
                if (!string.Equals(IdOf(p.target), id, StringComparison.OrdinalIgnoreCase)) continue;
                if (usedSources.Contains(SourceKey(p.source))) continue;
                var np = p;
                np.flags = PATH_ACTIVE;
                np.source.modeInfoIdx = IDX_INVALID;
                np.target.modeInfoIdx = IDX_INVALID;
                selected.Add(np);
                usedSources.Add(SourceKey(p.source));
                found = true;
                break;
            }
            if (!found) throw new InvalidOperationException($"A tela {id} não está conectada ou não tem saída livre.");
        }

        // Só os modos usados pelos caminhos escolhidos, com os índices remapeados.
        var newModes = new List<ModeInfo>();
        var remap = new Dictionary<uint, uint>();
        uint Remap(uint idx)
        {
            if (idx == IDX_INVALID || idx >= modes.Length) return IDX_INVALID;
            if (!remap.TryGetValue(idx, out var n)) { n = (uint)newModes.Count; newModes.Add(modes[idx]); remap[idx] = n; }
            return n;
        }
        var arr = selected.ToArray();
        for (int i = 0; i < arr.Length; i++)
        {
            arr[i].source.modeInfoIdx = Remap(arr[i].source.modeInfoIdx);
            arr[i].target.modeInfoIdx = Remap(arr[i].target.modeInfoIdx);
        }

        int r = SetDisplayConfig((uint)arr.Length, arr, (uint)newModes.Count, newModes.ToArray(),
            SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE);
        if (r != 0)
        {
            // Plano B: deixa o Windows escolher os modos a partir do banco de configurações dele.
            for (int i = 0; i < arr.Length; i++) { arr[i].source.modeInfoIdx = IDX_INVALID; arr[i].target.modeInfoIdx = IDX_INVALID; }
            int r2 = SetDisplayConfig((uint)arr.Length, arr, 0, null, SDC_APPLY | SDC_TOPOLOGY_SUPPLIED | SDC_ALLOW_PATH_ORDER_CHANGES);
            if (r2 != 0) throw new InvalidOperationException($"O Windows recusou a troca de telas (erros {r} e {r2}).");
        }

        Thread.Sleep(400);
        Reposition(positions);
    }

    static void Reposition(IDictionary<string, Point> positions)
    {
        var (paths, modes) = Query(QDC_ONLY_ACTIVE_PATHS);
        var original = (ModeInfo[])modes.Clone();
        var placed = new HashSet<uint>();
        var unplaced = new List<uint>();

        foreach (var p in paths)
        {
            uint idx = p.source.modeInfoIdx;
            if (idx >= modes.Length || modes[idx].infoType != MODE_SOURCE || !placed.Add(idx)) continue;
            string id = TargetIdentity(p.target).id;
            if (id != null && positions != null && positions.TryGetValue(id, out var pos))
            { modes[idx].x = pos.X; modes[idx].y = pos.Y; }
            else unplaced.Add(idx);
        }

        // Telas sem posição salva vão para a direita das demais.
        var settled = placed.Where(i => !unplaced.Contains(i)).ToList();
        foreach (var idx in unplaced)
        {
            modes[idx].x = settled.Select(i => modes[i].x + (int)modes[i].width).DefaultIfEmpty(0).Max();
            modes[idx].y = 0;
            settled.Add(idx);
        }

        // Garante uma tela em (0,0), que é a principal.
        if (!placed.Any(i => modes[i].x == 0 && modes[i].y == 0) && placed.Count > 0)
        {
            var first = placed.First();
            int dx = modes[first].x, dy = modes[first].y;
            foreach (var i in placed) { modes[i].x -= dx; modes[i].y -= dy; }
        }

        bool changed = placed.Any(i => modes[i].x != original[i].x || modes[i].y != original[i].y);
        if (!changed) return;
        Check(SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes,
            SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE), "SetDisplayConfig (posições)");
    }

    // Muda resolução e frequência de uma tela ativa. Retorna false se ela não oferece esse modo.
    static string GdiName(string monitorId)
    {
        var (paths, _) = Query(QDC_ONLY_ACTIVE_PATHS);
        foreach (var p in paths)
        {
            if (!string.Equals(TargetIdentity(p.target).id, monitorId, StringComparison.OrdinalIgnoreCase)) continue;
            var req = new SourceDeviceName { type = 1, adapterId = p.source.adapterId, id = p.source.id };
            req.size = (uint)Marshal.SizeOf<SourceDeviceName>();
            if (DisplayConfigGetDeviceInfo(ref req) == 0) return req.gdiName;
        }
        return null;
    }

    // Modos progressivos que uma tela ativa oferece, no formato "LxA@Hz".
    public static List<string> GetModes(string monitorId)
    {
        var result = new HashSet<string>();
        string gdi = GdiName(monitorId);
        if (gdi == null) return new();
        for (int i = 0; ; i++)
        {
            var dm = new DevMode { size = (short)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettings(gdi, i, ref dm)) break;
            if ((dm.displayFlags & 0x2) != 0 || dm.displayFrequency <= 1) continue; // entrelaçado ou "padrão do hardware"
            result.Add(Config.ModeKey(dm.pelsWidth, dm.pelsHeight, dm.displayFrequency));
        }
        return result.ToList();
    }

    public static bool SetMode(string monitorId, int width, int height, int hz)
    {
        string gdi = GdiName(monitorId);
        if (gdi == null) throw new InvalidOperationException($"A tela {monitorId} não está ativa.");

        var current = new DevMode { size = (short)Marshal.SizeOf<DevMode>() };
        if (EnumDisplaySettings(gdi, -1, ref current) && current.pelsWidth == width && current.pelsHeight == height
            && Math.Abs(current.displayFrequency - hz) <= 1) return true;

        DevMode? best = null;
        for (int i = 0; ; i++)
        {
            var dm = new DevMode { size = (short)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettings(gdi, i, ref dm)) break;
            if (dm.pelsWidth != width || dm.pelsHeight != height || Math.Abs(dm.displayFrequency - hz) > 1) continue;
            if (best == null || dm.bitsPerPel > best.Value.bitsPerPel || dm.displayFrequency == hz) best = dm;
        }
        if (best == null) return false;

        var target = best.Value;
        target.fields = 0x40000 | 0x80000 | 0x100000 | 0x400000; // BITSPERPEL | PELSWIDTH | PELSHEIGHT | DISPLAYFREQUENCY
        int r = ChangeDisplaySettingsEx(gdi, ref target, IntPtr.Zero, 0x1 /* CDS_UPDATEREGISTRY */, IntPtr.Zero);
        if (r != 0) throw new InvalidOperationException($"O Windows recusou {width}x{height} a {hz} Hz (erro {r}).");
        return true;
    }
}
