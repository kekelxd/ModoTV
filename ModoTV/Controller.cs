using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ModoTV;

// Botões num formato comum a todos os controles. Gravado em Config.ControllerChord.
[Flags]
public enum Pad : uint
{
    None = 0,
    A = 1 << 0, B = 1 << 1, X = 1 << 2, Y = 1 << 3,
    LB = 1 << 4, RB = 1 << 5, LT = 1 << 6, RT = 1 << 7,
    Back = 1 << 8, Start = 1 << 9, LS = 1 << 10, RS = 1 << 11,
    Up = 1 << 12, Down = 1 << 13, Left = 1 << 14, Right = 1 << 15,
    Guide = 1 << 16, Touch = 1 << 17,
}

public enum PadFamily { Xbox, EightBitDo, PlayStation }

public static class PadNames
{
    public const Pad DefaultChord = Pad.Back | Pad.Start;

    static readonly (Pad b, string xbox, string bitdo, string ps)[] Names =
    {
        (Pad.LB, "LB", "LB", "L1"), (Pad.RB, "RB", "RB", "R1"), (Pad.LT, "LT", "LT", "L2"), (Pad.RT, "RT", "RT", "R2"),
        (Pad.Back, "View", "Select", "Create"), (Pad.Start, "Menu", "Start", "Options"), (Pad.Guide, "Xbox", "Home", "PS"),
        (Pad.Touch, "Touchpad", "Touchpad", "Touchpad"),
        (Pad.A, "A", "A", "✕"), (Pad.B, "B", "B", "○"), (Pad.X, "X", "X", "□"), (Pad.Y, "Y", "Y", "△"),
        (Pad.LS, "L3", "L3", "L3"), (Pad.RS, "R3", "R3", "R3"),
        (Pad.Up, "↑", "↑", "↑"), (Pad.Down, "↓", "↓", "↓"), (Pad.Left, "←", "←", "←"), (Pad.Right, "→", "→", "→"),
    };

    // "LB + RB + Y", com os nomes que aparecem no controle de cada marca.
    public static string Format(Pad chord, PadFamily family)
    {
        var parts = Names.Where(n => chord.HasFlag(n.b) && n.b != Pad.None)
            .Select(n => family switch { PadFamily.PlayStation => n.ps, PadFamily.EightBitDo => n.bitdo, _ => n.xbox });
        return string.Join(" + ", parts);
    }

    public static int Count(Pad p) => System.Numerics.BitOperations.PopCount((uint)p);
}

// Fica de olho nos controles em segundo plano e dispara `Triggered` quando o atalho é segurado.
// Lê controles XInput (Xbox, 8BitDo no modo X-input) e PlayStation direto pelo HID.
public sealed class ControllerWatcher : IDisposable
{
    public event Action Triggered;
    public int HoldMs { get; set; } = 1500;
    public bool Enabled { get; set; } = true;
    public Pad Chord { get; set; } = PadNames.DefaultChord;

    // ---------- Gravação de um atalho novo ----------
    public const int RecordHoldMs = 2000;
    // Durante a gravação: botões segurados agora e progresso de 0 a 1.
    public event Action<Pad, double> RecordProgress;
    public event Action<Pad> Recorded;
    bool recording;
    Pad recPads;
    string recDevice;
    DateTime recSince, recLastNotify;

    public void StartRecording()
    {
        lock (gate) { recording = true; recPads = Pad.None; recDevice = null; }
    }

    public void CancelRecording() { lock (gate) recording = false; }

    // Nome do controle conectado agora (ou null), para mostrar no painel.
    public string ConnectedName
    {
        get
        {
            lock (gate)
            {
                if (padNames.Count > 0) return padNames.Values.First();
                return xinputConnected.Count > 0 ? (xinputBrand ?? "Controle Xbox") : null;
            }
        }
    }

    public PadFamily Family
    {
        get
        {
            lock (gate)
            {
                if (padNames.Count > 0) return PadFamily.PlayStation;
                return xinputConnected.Count > 0 && xinputBrand == null ? PadFamily.Xbox : PadFamily.EightBitDo;
            }
        }
    }

    // Como o atalho aparece escrito para o controle conectado.
    public string ChordLabel => PadNames.Format(Chord, Family);

    // XInput não diz a marca; descobrimos pelo fabricante USB na varredura HID.
    volatile string xinputBrand;
    const ushort EIGHTBITDO = 0x2DC8;
    static readonly Dictionary<ushort, string> EightBitDoModels = new()
    {
        [0x310B] = "8BitDo Ultimate 2", [0x3106] = "8BitDo Ultimate", [0x3109] = "8BitDo Ultimate", [0x3011] = "8BitDo Ultimate",
    };
    readonly Dictionary<string, string> padNames = new();
    readonly HashSet<int> xinputConnected = new();

    readonly Dictionary<string, Thread> hidReaders = new();
    readonly Dictionary<string, DateTime?> holdSince = new();
    readonly HashSet<string> fired = new();
    readonly object gate = new();
    readonly System.Threading.Timer scanTimer, xinputTimer;
    volatile bool disposed;

    public ControllerWatcher()
    {
        scanTimer = new System.Threading.Timer(_ => ScanHid(), null, 0, 3000);
        xinputTimer = new System.Threading.Timer(_ => PollXInput(), null, 0, 50);
    }

    public void Dispose()
    {
        disposed = true;
        scanTimer.Dispose();
        xinputTimer.Dispose();
    }

    void Report(string device, Pad pressed)
    {
        bool fire = false;
        Pad done = Pad.None;
        (Pad pads, double progress)? notify = null;
        lock (gate)
        {
            if (recording)
            {
                // Gravando: vale a combinação de 2+ botões segurada parada por 2 s.
                if (recDevice != null && recDevice != device) return;
                var now = DateTime.UtcNow;
                if (PadNames.Count(pressed) < 2)
                {
                    if (recPads != Pad.None) notify = (pressed, 0);
                    recPads = Pad.None; recDevice = null;
                }
                else if (pressed != recPads)
                {
                    recPads = pressed; recDevice = device; recSince = now;
                    notify = (pressed, 0);
                }
                else
                {
                    double p = (now - recSince).TotalMilliseconds / RecordHoldMs;
                    if (p >= 1) { recording = false; done = pressed; fired.Add(device); }
                    else if ((now - recLastNotify).TotalMilliseconds > 60) notify = (pressed, p);
                }
                if (notify != null) recLastNotify = now;
            }
            else
            {
                bool chordDown = Chord != Pad.None && (pressed & Chord) == Chord;
                if (!chordDown || !Enabled)
                {
                    holdSince[device] = null;
                    fired.Remove(device);
                    return;
                }
                holdSince.TryGetValue(device, out var since);
                if (since == null) holdSince[device] = DateTime.UtcNow;
                else if (!fired.Contains(device) && (DateTime.UtcNow - since.Value).TotalMilliseconds >= HoldMs)
                {
                    fired.Add(device);
                    fire = true;
                }
            }
        }
        if (notify is { } n) RecordProgress?.Invoke(n.pads, n.progress);
        if (done != Pad.None) Recorded?.Invoke(done);
        if (fire) Triggered?.Invoke();
    }

    // ---------- Xbox (XInput) ----------

    [StructLayout(LayoutKind.Sequential)]
    struct XInputState { public uint packet; public ushort buttons; public byte lt, rt; public short lx, ly, rx, ry; }

    [DllImport("xinput1_4.dll")] static extern int XInputGetState(int index, out XInputState state);

    static readonly (ushort bit, Pad pad)[] XInputMap =
    {
        (0x0001, Pad.Up), (0x0002, Pad.Down), (0x0004, Pad.Left), (0x0008, Pad.Right),
        (0x0010, Pad.Start), (0x0020, Pad.Back), (0x0040, Pad.LS), (0x0080, Pad.RS),
        (0x0100, Pad.LB), (0x0200, Pad.RB), (0x1000, Pad.A), (0x2000, Pad.B), (0x4000, Pad.X), (0x8000, Pad.Y),
    };

    void PollXInput()
    {
        if (disposed) return;
        for (int i = 0; i < 4; i++)
        {
            try
            {
                bool connected = XInputGetState(i, out var s) == 0;
                lock (gate) { if (connected) xinputConnected.Add(i); else xinputConnected.Remove(i); }
                var pads = Pad.None;
                if (connected)
                {
                    foreach (var (bit, pad) in XInputMap) if ((s.buttons & bit) != 0) pads |= pad;
                    if (s.lt > 128) pads |= Pad.LT;
                    if (s.rt > 128) pads |= Pad.RT;
                }
                Report("xinput" + i, pads);
            }
            catch (DllNotFoundException) { return; }
        }
    }

    // ---------- PlayStation (HID direto, funciona com ou sem Steam aberta) ----------

    const ushort SONY = 0x054C;
    static readonly HashSet<ushort> DualSense = new() { 0x0CE6, 0x0DF2 };
    static readonly HashSet<ushort> DualShock4 = new() { 0x05C4, 0x09CC, 0x0BA0 };

    [StructLayout(LayoutKind.Sequential)]
    struct SpDeviceInterfaceData { public int cbSize; public Guid classGuid; public int flags; public IntPtr reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct HiddAttributes { public int size; public ushort vendorId, productId, version; }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HiddAttributes attr);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, byte[] caps);
    [DllImport("setupapi.dll")] static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, int flags);
    [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, int index, ref SpDeviceInterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SpDeviceInterfaceData data, IntPtr detail, int size, out int required, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    const uint GENERIC_READ = 0x80000000, FILE_SHARE_RW = 3, OPEN_EXISTING = 3;

    static IEnumerable<string> HidPaths()
    {
        HidD_GetHidGuid(out var guid);
        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12); // PRESENT | DEVICEINTERFACE
        if (set == new IntPtr(-1)) yield break;
        try
        {
            for (int i = 0; ; i++)
            {
                var data = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data)) break;
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetail(set, ref data, buf, size, out _, IntPtr.Zero))
                        yield return Marshal.PtrToStringUni(buf + 4);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    void ScanHid()
    {
        if (disposed) return;
        try
        {
            string brand = null;
            foreach (var path in HidPaths())
            {
                bool reading;
                lock (gate) reading = hidReaders.ContainsKey(path);
                using (var probe = CreateFile(path, 0, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                {
                    if (probe.IsInvalid) continue;
                    var attr = new HiddAttributes { size = Marshal.SizeOf<HiddAttributes>() };
                    if (!HidD_GetAttributes(probe, ref attr)) continue;
                    if (attr.vendorId == EIGHTBITDO)
                        brand ??= EightBitDoModels.TryGetValue(attr.productId, out var model) ? model : "8BitDo";
                    if (reading || attr.vendorId != SONY) continue;
                    bool ds5 = DualSense.Contains(attr.productId);
                    if (!ds5 && !DualShock4.Contains(attr.productId)) continue;
                    var t = new Thread(() => ReadSony(path, ds5)) { IsBackground = true, Name = "HID " + path };
                    lock (gate) { hidReaders[path] = t; padNames[path] = ds5 ? "DualSense" : "DualShock 4"; }
                    t.Start();
                }
            }
            xinputBrand = brand;
        }
        catch { /* tenta de novo na próxima varredura */ }
    }

    void ReadSony(string path, bool dualSense)
    {
        try
        {
            using var h = CreateFile(path, GENERIC_READ, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) return;
            int len = 64;
            if (HidD_GetPreparsedData(h, out var pre))
            {
                var caps = new byte[64];
                if (HidP_GetCaps(pre, caps) == 0x110000) len = BitConverter.ToUInt16(caps, 4); // HIDP_STATUS_SUCCESS
                HidD_FreePreparsedData(pre);
            }
            bool usb = len == 64;
            using var fs = new FileStream(h, FileAccess.Read, 0, false);
            var buf = new byte[len];
            while (!disposed)
            {
                int n = fs.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                Report(path, SonyButtons(buf, n, dualSense, usb));
            }
        }
        catch { /* controle desconectado */ }
        finally
        {
            lock (gate) { hidReaders.Remove(path); padNames.Remove(path); holdSince.Remove(path); fired.Remove(path); }
        }
    }

    // Os três bytes de botões (cruz+formas, ombros+Create/Options, PS+touchpad) ficam em
    // posições diferentes conforme o modelo e a conexão.
    static Pad SonyButtons(byte[] b, int n, bool dualSense, bool usb)
    {
        int at;
        if (dualSense)
        {
            if (usb && b[0] == 0x01) at = 8;
            else if (b[0] == 0x31) at = 9;      // Bluetooth, modo completo (o que a Steam ativa)
            else if (b[0] == 0x01) at = 5;      // Bluetooth, modo simples
            else return Pad.None;
        }
        else
        {
            if (b[0] == 0x01) at = 5;           // USB ou Bluetooth simples
            else if (b[0] == 0x11) at = 7;      // Bluetooth completo
            else return Pad.None;
        }
        if (at + 2 >= n) return Pad.None;
        byte face = b[at], shoulders = b[at + 1], system = b[at + 2];

        var p = Pad.None;
        switch (face & 0x0F) // direcional: 0 = cima, sentido horário, 8 = solto
        {
            case 0: p |= Pad.Up; break;
            case 1: p |= Pad.Up | Pad.Right; break;
            case 2: p |= Pad.Right; break;
            case 3: p |= Pad.Down | Pad.Right; break;
            case 4: p |= Pad.Down; break;
            case 5: p |= Pad.Down | Pad.Left; break;
            case 6: p |= Pad.Left; break;
            case 7: p |= Pad.Up | Pad.Left; break;
        }
        if ((face & 0x10) != 0) p |= Pad.X;      // quadrado
        if ((face & 0x20) != 0) p |= Pad.A;      // xis
        if ((face & 0x40) != 0) p |= Pad.B;      // bola
        if ((face & 0x80) != 0) p |= Pad.Y;      // triângulo
        if ((shoulders & 0x01) != 0) p |= Pad.LB;
        if ((shoulders & 0x02) != 0) p |= Pad.RB;
        if ((shoulders & 0x04) != 0) p |= Pad.LT;
        if ((shoulders & 0x08) != 0) p |= Pad.RT;
        if ((shoulders & 0x10) != 0) p |= Pad.Back;  // Create / Share
        if ((shoulders & 0x20) != 0) p |= Pad.Start; // Options
        if ((shoulders & 0x40) != 0) p |= Pad.LS;
        if ((shoulders & 0x80) != 0) p |= Pad.RS;
        if ((system & 0x01) != 0) p |= Pad.Guide;
        if ((system & 0x02) != 0) p |= Pad.Touch;
        return p;
    }
}
