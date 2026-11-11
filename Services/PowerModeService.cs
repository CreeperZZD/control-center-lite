using System;
using System.Runtime.InteropServices;

namespace CCLite.Services;

/// <summary>
/// 系统模式（0=狂暴 1=游戏 2=办公）：
/// 1) WMI 0x0300 写入 EC；2) 激活对应 Windows 电源计划；3) 更新注册表；
/// 4) 重放该模式对应的键盘灯效档案（修复原版切模式灯效不生效的缺陷）。
/// </summary>
public static class PowerModeService
{
    public const uint ModePerformance = 0; // 狂暴
    public const uint ModeGaming = 1;      // 游戏
    public const uint ModeOffice = 2;      // 办公

    // 原版预导入的三个电源计划 GUID（MyPowerPlanBase）
    private static readonly Guid GuidPerformance = new("a9932169-f1fb-4dff-9034-fe1c4ca1886e");
    private static readonly Guid GuidGaming = new("653fbb7f-248b-41d0-aa49-03fabbfd5e8e");
    private static readonly Guid GuidOffice = new("5da85d85-eb0c-4710-a805-6da8bbcf1f02");

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr UserRootPowerKey, ref Guid SchemeGuid);

    public static string NameOf(uint mode) => mode switch
    {
        ModePerformance => "狂暴模式",
        ModeGaming => "游戏模式",
        _ => "办公模式",
    };

    private static Guid GuidOf(uint mode) => mode switch
    {
        ModePerformance => GuidPerformance,
        ModeGaming => GuidGaming,
        _ => GuidOffice,
    };

    /// <summary>
    /// 切换系统模式（完整链路，与原版一致 + 灯效联动修复）。
    /// 内部已写注册表 ModeBeforeDC，MonitorService 的模式观察会跳过这次变更。
    /// </summary>
    public static bool SetMode(uint mode)
    {
        var buf = GmWmiBuffer.Write(0x300);
        buf.A2 = mode;
        if (!GmWmiService.Execute(buf)) return false;

        // 激活对应 Windows 电源计划（原版 MyScheme setter 的行为）
        try
        {
            var guid = GuidOf(mode);
            PowerSetActiveScheme(IntPtr.Zero, ref guid);
        }
        catch { }

        // 注册表与原版对齐
        GMReg.Set(GMReg.ValModeBeforeDC, mode);
        GMReg.Set(GMReg.ValLedModule, LedService.ProfileForMode(mode).ModuleId);

        // 灯效联动（原版缺失的关键一步）
        LedService.ApplyProfileForMode(mode);
        return true;
    }

    /// <summary>读取注册表中的当前系统模式。</summary>
    public static uint GetCurrentMode()
    {
        var v = GMReg.Get(GMReg.ValModeBeforeDC, 2);
        return v > 2 ? 2 : v;
    }
}
