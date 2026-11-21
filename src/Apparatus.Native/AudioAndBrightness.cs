using System.Management;
using System.Runtime.InteropServices;
using Apparatus.Core;

namespace Apparatus.Native;

/// <summary>Master volume and mute of the default playback device, through the Windows Core Audio API.</summary>
public static class AudioControl
{
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
    }

    // Only the methods up to GetMute are declared, but in the exact order of the native vtable.
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid EndpointVolumeIid = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    private static IAudioEndpointVolume Open()
    {
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid)!)!;
        Check(enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out var device), "dispositivo de som padrão");
        var iid = EndpointVolumeIid;
        Check(device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var endpoint), "controle de volume");
        return (IAudioEndpointVolume)endpoint;
    }

    private static void Check(int hr, string what)
    {
        if (hr != 0) throw new InvalidOperationException($"Não foi possível acessar o {what} (HRESULT 0x{hr:X8}).");
    }

    /// <summary>Volume from 0 to 100.</summary>
    public static int GetVolume()
    {
        var volume = Open();
        Check(volume.GetMasterVolumeLevelScalar(out float level), "volume");
        return (int)Math.Round(level * 100);
    }

    public static bool GetMute()
    {
        var volume = Open();
        Check(volume.GetMute(out bool mute), "estado de mudo");
        return mute;
    }

    public static void SetVolume(int percent)
    {
        var volume = Open();
        var context = Guid.Empty;
        Check(volume.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, ref context), "volume");
        // Raising the volume while muted should be audible, like the keyboard volume keys do.
        if (percent > 0) volume.SetMute(false, ref context);
    }

    public static void ChangeVolume(int deltaPercent) => SetVolume(Math.Clamp(GetVolume() + deltaPercent, 0, 100));

    public static bool ToggleMute()
    {
        var volume = Open();
        Check(volume.GetMute(out bool mute), "estado de mudo");
        var context = Guid.Empty;
        Check(volume.SetMute(!mute, ref context), "estado de mudo");
        return !mute;
    }
}

/// <summary>
/// Monitor brightness. External monitors answer to DDC/CI (the same thing their physical buttons do);
/// laptop panels answer to WMI. DDC/CI is tried first, on the monitor of the window in front.
/// </summary>
public static unsafe class BrightnessControl
{
    /// <summary>Current brightness 0–100, or null when no monitor reports it.</summary>
    public static int? Get()
    {
        var ddc = WithDdcMonitors(m => ReadDdc(m));
        return ddc ?? ReadWmi();
    }

    public static void Set(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (WithDdcMonitors(m => WriteDdc(m, percent)) is true) return;
        if (WriteWmi(percent)) return;
        throw new NotSupportedException("Nenhum monitor aceitou o controle de brilho (nem por DDC/CI nem pelo Windows). Verifique se o DDC/CI está ligado no menu do monitor.");
    }

    public static void Change(int deltaPercent)
    {
        int current = Get() ?? throw new NotSupportedException("Nenhum monitor informou o brilho atual (DDC/CI e WMI indisponíveis).");
        Set(current + deltaPercent);
    }

    // --------------------------------------------------------------- DDC/CI

    private static T? WithDdcMonitors<T>(Func<PhysicalMonitor, T?> action) where T : struct
    {
        var hwnd = Interop.GetForegroundWindow();
        var monitor = Interop.MonitorFromWindow(hwnd, Interop.MONITOR_DEFAULTTONEAREST);
        if (!Interop.GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out uint count) || count == 0) return null;

        var physical = new PhysicalMonitor[count];
        if (!Interop.GetPhysicalMonitorsFromHMONITOR(monitor, count, physical)) return null;
        try
        {
            foreach (var m in physical)
            {
                if (action(m) is { } result) return result;
            }
            return null;
        }
        finally
        {
            Interop.DestroyPhysicalMonitors(count, physical);
        }
    }

    private static int? ReadDdc(PhysicalMonitor m)
    {
        if (!Interop.GetMonitorBrightness(m.Handle, out uint min, out uint current, out uint max) || max <= min) return null;
        return (int)Math.Round((current - min) * 100.0 / (max - min));
    }

    private static bool? WriteDdc(PhysicalMonitor m, int percent)
    {
        if (!Interop.GetMonitorBrightness(m.Handle, out uint min, out _, out uint max) || max <= min) return null;
        uint value = (uint)Math.Round(min + percent / 100.0 * (max - min));
        return Interop.SetMonitorBrightness(m.Handle, value) ? true : null;
    }

    // ------------------------------------------------------------------ WMI

    private static int? ReadWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject o in searcher.Get()) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            AppLog.Info($"WMI brightness unavailable: {ex.Message}");
        }
        return null;
    }

    private static bool WriteWmi(int percent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
            bool any = false;
            foreach (ManagementObject o in searcher.Get())
            {
                o.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)percent });
                any = true;
            }
            return any;
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            AppLog.Info($"WMI brightness unavailable: {ex.Message}");
            return false;
        }
    }
}
