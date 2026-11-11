using System;
using System.Management;
using System.Runtime.InteropServices;

namespace CCLite.Services;

/// <summary>内建屏幕亮度（WMI root\wmi WmiMonitorBrightnessMethods）。</summary>
public static class BrightnessService
{
    public static int Get()
    {
        try
        {
            using var s = new ManagementObjectSearcher("root\\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject o in s.Get())
                return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        return -1;
    }

    /// <summary>按系统模式记忆的亮度注册表键（0=狂暴 1=游戏 2=办公）。</summary>
    private static string SlotFor(uint mode) => "BrightnessMode" + Math.Clamp(mode, 0, 2);

    /// <summary>
    /// 应用某系统模式记忆的亮度。无记忆值时把当前亮度登记为该模式基线（不改变屏幕）。
    /// 返回实际亮度值；WMI 不可用返回 -1。
    /// </summary>
    public static int ApplyForMode(uint mode)
    {
        int current = Get();
        if (current < 0) return -1;
        string slot = SlotFor(mode);
        var stored = GMReg.Get(slot, uint.MaxValue);
        if (stored == uint.MaxValue)
        {
            GMReg.Set(slot, (uint)current);      // 首次：以当前亮度为该模式基线
            return current;
        }
        int v = (int)Math.Clamp(stored, 0, 100);
        if (v != current) Set(v);
        return v;
    }

    /// <summary>把当前亮度保存为指定系统模式的记忆值。</summary>
    public static void SaveForMode(uint mode, int percent)
    {
        if (percent < 0) return;
        GMReg.Set(SlotFor(mode), (uint)Math.Clamp(percent, 0, 100));
    }

    public static bool Set(int percent)
    {
        try
        {
            percent = Math.Clamp(percent, 0, 100);
            using var s = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            bool ok = false;
            foreach (ManagementObject o in s.Get())
            {
                o.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)percent });
                ok = true;
            }
            return ok;
        }
        catch { return false; }
    }
}

/// <summary>系统主音量 / 静音（CoreAudio IAudioEndpointVolume）。</summary>
public static class AudioService
{
    private static IAudioEndpointVolume _volume;
    private static bool _tried;

    private static bool Ensure()
    {
        if (_tried) return _volume != null;
        _tried = true;
        try
        {
            var enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
            enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 0 /*eConsole*/, out var dev);
            var iid = typeof(IAudioEndpointVolume).GUID;
            dev.Activate(ref iid, 1 /*CLSCTX_ALL*/, IntPtr.Zero, out var obj);
            _volume = (IAudioEndpointVolume)obj;
            return _volume != null;
        }
        catch { _volume = null; return false; }
    }

    public static bool Available => Ensure();

    public static int GetVolumePercent()
    {
        if (!Ensure()) return -1;
        try { _volume.GetMasterVolumeLevelScalar(out float v); return (int)Math.Round(v * 100); }
        catch { return -1; }
    }

    public static void SetVolumePercent(int percent)
    {
        if (!Ensure()) return;
        try { _volume.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, Guid.Empty); }
        catch { }
    }

    public static bool IsMuted()
    {
        if (!Ensure()) return false;
        try { _volume.GetMute(out bool m); return m; } catch { return false; }
    }

    public static void SetMute(bool mute)
    {
        if (!Ensure()) return;
        try { _volume.SetMute(mute, Guid.Empty); } catch { }
    }
}

// ---------------- CoreAudio COM 互操作（最小实现） ----------------

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject
{
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object iface);
}

[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    int RegisterControlChangeNotify(IntPtr notify);
    int UnregisterControlChangeNotify(IntPtr notify);
    int GetChannelCount(out uint count);
    int SetMasterVolumeLevel(float level, Guid ctx);
    int SetMasterVolumeLevelScalar(float level, Guid ctx);
    int GetMasterVolumeLevel(out float level);
    int GetMasterVolumeLevelScalar(out float level);
    int SetChannelVolumeLevel(uint ch, float level, Guid ctx);
    int SetChannelVolumeLevelScalar(uint ch, float level, Guid ctx);
    int GetChannelVolumeLevel(uint ch, out float level);
    int GetChannelVolumeLevelScalar(uint ch, out float level);
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, Guid ctx);
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}
