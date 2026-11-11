using System;
using Microsoft.Win32;

namespace CCLite.Services;

/// <summary>
/// 与原版 ControlCenter 共用的配置注册表：HKLM\SOFTWARE\WOW6432Node\NL5\ControlCenter。
/// 灯效档案（Game/HighPerformance/PowerSaving/Custom）与原版双向互通。
/// </summary>
public static class GMReg
{
    private const string KeyPath = @"SOFTWARE\WOW6432Node\NL5\ControlCenter";

    public const string ValGame = "Game";                 // 灯效档案：游戏(模块0)
    public const string ValHighPerf = "HighPerformance";  // 灯效档案：狂暴(模块1)
    public const string ValPowerSaving = "PowerSaving";   // 灯效档案：办公(模块2)
    public const string ValCustom = "Custom";             // 灯效档案：自定义(模块3)
    public const string ValLedModule = "LEDModule";       // 当前激活灯效模块 0-3
    public const string ValModeBeforeDC = "ModeBeforeDC"; // 当前系统模式 0=狂暴 1=游戏 2=办公
    public const string ValWinKey = "WinKey";             // Win键锁定 0/1
    public const string ValLedWizard = "LEDWizard";       // 灯光总开关 0/1
    public const string ValAlpha = "Alpha";               // 键盘亮度
    public const string ValFanControlStatus = "FanControlStatus"; // 自定义风扇开关 0/1
    public const string ValFanControlSelect = "FanControlSelect"; // 当前风扇预设 0-2
    public const string ValLanguage = "Language";
    public const string ValControlCenterClose = "ControlCenterClose";

    public static uint Get(string name, uint fallback = 0)
    {
        try
        {
            var v = Registry.LocalMachine.OpenSubKey(KeyPath)?.GetValue(name);
            return v == null ? fallback : Convert.ToUInt32(v);
        }
        catch { return fallback; }
    }

    public static void Set(string name, uint value)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(KeyPath);
            key?.SetValue(name, value, RegistryValueKind.DWord);
        }
        catch { }
    }

    public static string GetStr(string name, string fallback = "")
    {
        try
        {
            var v = Registry.LocalMachine.OpenSubKey(KeyPath)?.GetValue(name);
            return v == null ? fallback : Convert.ToString(v);
        }
        catch { return fallback; }
    }

    public static void SetStr(string name, string value)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(KeyPath);
            key?.SetValue(name, value, RegistryValueKind.String);
        }
        catch { }
    }
}
