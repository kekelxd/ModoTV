using System.Runtime.InteropServices;

namespace ModoTV;

// Troca o dispositivo de saída de áudio padrão (o mesmo que clicar no ícone de som do Windows).
public static class Audio
{
    const int eRender = 0, eConsole = 0, eMultimedia = 1, DEVICE_STATE_ACTIVE = 1;

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] class PolicyConfigClient { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    // Interface não documentada, usada por todas as ferramentas de troca de áudio desde o Windows 7.
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int def, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int def, IntPtr defPeriod, IntPtr minPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr pointer; }

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pv);

    static PropertyKey FriendlyNameKey = new() { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };

    public record Device(string Id, string Name);

    public static List<Device> GetOutputs(bool includeInactive = false)
    {
        var list = new List<Device>();
        var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        if (en.EnumAudioEndpoints(eRender, includeInactive ? 0xF : DEVICE_STATE_ACTIVE, out var col) != 0) return list;
        col.GetCount(out int n);
        for (int i = 0; i < n; i++)
        {
            if (col.Item(i, out var dev) != 0) continue;
            dev.GetId(out string id);
            list.Add(new Device(id, NameOf(dev)));
        }
        return list;
    }

    static string NameOf(IMMDevice dev)
    {
        if (dev.OpenPropertyStore(0, out var store) != 0) return "";
        var key = FriendlyNameKey;
        if (store.GetValue(ref key, out var pv) != 0) return "";
        string name = pv.vt == 31 ? Marshal.PtrToStringUni(pv.pointer) : "";
        PropVariantClear(ref pv);
        return name ?? "";
    }

    public static Device GetDefault()
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        if (en.GetDefaultAudioEndpoint(eRender, eMultimedia, out var dev) != 0) return null;
        dev.GetId(out string id);
        return new Device(id, NameOf(dev));
    }

    static void SetDefault(string id)
    {
        var pc = (IPolicyConfig)new PolicyConfigClient();
        pc.SetDefaultEndpoint(id, eConsole);
        pc.SetDefaultEndpoint(id, eMultimedia);
    }

    // O áudio HDMI da TV só aparece alguns segundos depois que a tela liga, por isso as tentativas.
    public static Device SetDefaultMatching(Func<Device, bool> match, int timeoutMs)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var dev = GetOutputs().FirstOrDefault(match);
            if (dev != null) { SetDefault(dev.Id); return dev; }
            if (DateTime.UtcNow > until) return null;
            Thread.Sleep(500);
        }
    }
}
