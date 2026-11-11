using System;
using System.Windows.Media;

namespace CCLite.Services;

/// <summary>
/// 键盘 RGB 灯光控制（本机尾部灯条为反光贴纸、无独立灯珠，只控制键盘三区）。
/// EC 灯效数据 32 位：[灯效模式4bit][亮度4bit][R8][G8][B8]。
/// EC 槽位：LED1(键盘A区)=5、LED2(B区)=4、LED3(C区)=3（与原版 LEDCtrl.REG_LED 一致）。
/// 灯效模式：0=关闭 1=常亮 3=呼吸 6=多彩循环 7=流光溢彩。
/// </summary>
public static class LedService
{
    public const byte SlotLed1 = 5;   // 键盘A区
    public const byte SlotLed2 = 4;   // 键盘B区
    public const byte SlotLed3 = 3;   // 键盘C区
    public static readonly byte[] KeyboardSlots = { SlotLed1, SlotLed2, SlotLed3 };

    public const byte ModeOff = 0;
    public const byte ModeStatic = 1;
    public const byte ModeBreathing = 3;
    public const byte ModeColorCycle = 6;
    public const byte ModeAmblilight = 7;   // 流光溢彩（仅键盘）

    public static readonly (byte Mode, string Name)[] EffectList =
    {
        (ModeOff, "关闭"),
        (ModeStatic, "常亮"),
        (ModeBreathing, "呼吸"),
        (ModeColorCycle, "多彩循环"),
        (ModeAmblilight, "流光溢彩"),
    };

    public static uint MakeData(int mode, int brightness, Color c)
        => ((uint)mode << 28) | ((uint)(brightness & 0xF) << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    public static byte GetMode(uint data) => (byte)((data >> 28) & 0xF);
    public static byte GetBrightness(uint data) => (byte)((data >> 24) & 0xF);
    public static Color GetColor(uint data) => Color.FromRgb((byte)((data >> 16) & 0xFF), (byte)((data >> 8) & 0xFF), (byte)(data & 0xFF));

    /// <summary>写单个 EC 灯效槽位。</summary>
    public static bool WriteZone(byte slot, uint data)
    {
        var buf = GmWmiBuffer.Write(0x100);
        buf.A2 = slot;
        buf.A3 = data;
        return GmWmiService.Execute(buf);
    }

    /// <summary>把 32 位灯效数据写入键盘三区（同一份数据）。</summary>
    public static bool WriteAllKeyboard(uint data)
    {
        bool ok = true;
        foreach (var slot in KeyboardSlots) ok &= WriteZone(slot, data);
        return ok;
    }

    // ---------- 灯效档案（与原版共用注册表，5 段分号分隔的十六进制：logo;trunk;led1;led2;led3） ----------

    /// <summary>读取档案并解析为 5 段 uint（下标 0=logo 1=trunk 2=LED1 3=LED2 4=LED3）。</summary>
    public static uint[] LoadProfile(string valueName)
    {
        var result = new uint[5];
        var raw = GMReg.GetStr(valueName, "");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var parts = raw.Split(';');
            for (int i = 0; i < 5 && i < parts.Length; i++)
            {
                try { result[i] = Convert.ToUInt32(parts[i].Trim(), 16); } catch { }
            }
        }
        return result;
    }

    /// <summary>把键盘三区（下标 2/3/4）写回档案，保留 logo/trunk 两段不动。</summary>
    public static void SaveKeyboardToProfile(string valueName, uint[] parts)
    {
        var raw = GMReg.GetStr(valueName, "");
        var seg = (raw ?? "").Split(';');
        var arr = new string[5];
        for (int i = 0; i < 5; i++)
            arr[i] = i < seg.Length && uint.TryParse(seg[i].Trim(), System.Globalization.NumberStyles.HexNumber, null, out var v)
                ? v.ToString("x") : (i < parts.Length ? parts[i].ToString("x") : "0");
        arr[2] = parts[2].ToString("x");
        arr[3] = parts[3].ToString("x");
        arr[4] = parts[4].ToString("x");
        GMReg.SetStr(valueName, string.Join(";", arr));
    }

    // ---------- 按灯效档案的开关（每个档案独立记忆；关=该档案不点亮） ----------

    /// <summary>读取档案的灯效开关（0=熄灭 1=点亮）。首次访问从旧的按模式值迁移。</summary>
    public static byte GetProfileOn(string profileValueName)
    {
        if (GMReg.Get("LedOnMigrated", 0) != 1)
        {
            // 迁移：旧 LedOnMode0/1/2（0狂暴→HighPerformance 1游戏→Game 2办公→PowerSaving）
            var map = new (uint Mode, string Val)[]
            {
                (0, GMReg.ValHighPerf), (1, GMReg.ValGame), (2, GMReg.ValPowerSaving),
            };
            foreach (var (mode, val) in map)
            {
                uint v = GMReg.Get("LedOnMode" + mode, 255);
                if (v == 255) v = GMReg.Get(GMReg.ValLedWizard, 1) == 0 ? 0u : 1u;
                GMReg.Set("LedOn_" + val, v == 0 ? 0u : 1u);
            }
            GMReg.Set("LedOn_" + GMReg.ValCustom, 1u);
            GMReg.Set("LedOnMigrated", 1);
        }
        return (byte)Math.Clamp(GMReg.Get("LedOn_" + profileValueName, 1), 0, 1);
    }

    public static void SetProfileOn(string profileValueName, bool on)
    {
        GMReg.Set("LedOn_" + profileValueName, on ? 1u : 0u);
        GMReg.Set(GMReg.ValLedWizard, on ? 1u : 0u);   // 兼容原版的镜像
    }

    /// <summary>
    /// 把档案数据写入键盘三区。本机为 NLY 机型（SKUID 52）：键盘亮度 EC 仅接受 0/1/2 三档，
    /// sanitizeBrightness=true 时档案里的其他亮度值一律消毒到 2，避免无效值导致整键盘熄灭。
    /// </summary>
    public static void WriteKeyboardZones(uint[] parts5, bool sanitizeBrightness = true)
    {
        for (int i = 0; i < 3; i++)
        {
            var data = parts5[2 + i];
            if (sanitizeBrightness)
            {
                byte bri = GetBrightness(data);
                if (bri > 2) data = MakeData(GetMode(data), 2, GetColor(data));
            }
            WriteZone(KeyboardSlots[i], data);
        }
    }

    /// <summary>把当前系统模式对应的灯效档案重放到 EC（修复原版"切模式灯效不变"的缺陷）。</summary>
    public static void ApplyProfileForMode(uint sysMode)
    {
        var (profile, moduleId) = ProfileForMode(sysMode);
        ApplyProfile(profile, moduleId);
    }

    /// <summary>应用指定档案：开关开=写入键盘；关=写熄灭（颜色记忆保留在档案里）。</summary>
    public static void ApplyProfile(string profileValueName, uint moduleId)
    {
        var parts = LoadProfile(profileValueName);
        bool on = GetProfileOn(profileValueName) == 1;
        for (int i = 0; i < 3; i++)
        {
            var data = parts[2 + i];
            byte bri = GetBrightness(data);
            if (bri > 2) data = MakeData(GetMode(data), 2, GetColor(data));
            if (!on) data &= 0x00FFFFFF;   // 开关为关 → 熄灭（保留颜色记忆）
            WriteZone(KeyboardSlots[i], data);
        }
        GMReg.Set(GMReg.ValLedModule, moduleId);
        GMReg.Set(GMReg.ValLedWizard, on ? 1u : 0u);
    }

    /// <summary>NLY 亮度档显示文本。</summary>
    public static string BrightnessText(byte bri) => bri switch
    {
        0 => "0%",
        1 => "50%",
        2 => "100%",
        _ => $"{bri * 100 / 15}%",
    };

    /// <summary>系统模式(0=狂暴 1=游戏 2=办公) → (注册表灯效档案名, 灯效模块号)。</summary>
    public static (string Profile, uint ModuleId) ProfileForMode(uint sysMode) => sysMode switch
    {
        0 => (GMReg.ValHighPerf, 1u),
        1 => (GMReg.ValGame, 0u),
        _ => (GMReg.ValPowerSaving, 2u),
    };

    /// <summary>当前激活灯效模块(0-3) → 注册表档案名。</summary>
    public static string ProfileOfModule(uint moduleId) => moduleId switch
    {
        0 => GMReg.ValGame,
        1 => GMReg.ValHighPerf,
        2 => GMReg.ValPowerSaving,
        _ => GMReg.ValCustom,
    };
}
