using System;

namespace CCLite.Services;

/// <summary>
/// 显示输出模式（WMI 0x0203）：1=混合输出 2=独显直连 3=集显输出。
/// 写入后需重启生效（与原版流程一致：确认 → 写 EC → 提示重启）。
/// </summary>
public static class DisplayService
{
    public const uint Hybrid = 1;
    public const uint Discrete = 2;
    public const uint Uma = 3;

    public static string NameOf(uint m) => m switch
    {
        Discrete => "独显直连",
        Uma => "集显输出",
        _ => "混合输出",
    };

    /// <summary>读当前显示模式（a2），HDMI 状态（a3）。</summary>
    public static (uint Current, uint Hdmi, bool Ok) GetStatus()
    {
        var buf = GmWmiBuffer.Read(0x203);
        if (!GmWmiService.Execute(buf)) return (0, 0, false);
        return (buf.A2, buf.A3, true);
    }

    /// <summary>写目标显示模式（重启后生效）。</summary>
    public static bool SetMode(uint dspMode)
    {
        var buf = GmWmiBuffer.Write(0x203);
        buf.A2 = dspMode;
        return GmWmiService.Execute(buf);
    }

    /// <summary>Win 键锁定（WMI 0x0200 a6=1 锁定 / 0 解锁）。</summary>
    public static bool SetWinKeyLock(bool locked)
    {
        var buf = GmWmiBuffer.Write(0x200);
        buf.A6 = locked ? 1u : 0u;
        if (!GmWmiService.Execute(buf)) return false;
        GMReg.Set(GMReg.ValWinKey, locked ? 1u : 0u);
        return true;
    }

    /// <summary>读取硬件能力：是否支持独显直连切换（a6==2）、是否支持高刷切换（a4==2）。</summary>
    public static (bool DdsSupport, bool HsrSupport, bool Ok) GetCapability()
    {
        var buf = GmWmiBuffer.Read(0x202);
        if (!GmWmiService.Execute(buf)) return (false, false, false);
        return (buf.A6 == 2, buf.A4 == 2, true);
    }
}
