using System;
using System.Windows.Threading;

namespace CCLite.Services;

/// <summary>
/// 风扇控制：本机只有 CPU/GPU 两个风扇（SYS 风扇接口存在但无扇，恒传 0xFF 走自动）。
/// WMI 0x0205: 写转速百分比 a2=CPU a3=GPU a4=SYS(0xFF=自动)；读=默认风扇表。
/// WMI 0x0206: 写自定义风扇开关 a2=0/1；读 a2=当前状态。
/// 曲线：固定温度点 30/40/.../100°C，8 档百分比；注册表 UserFan{1..3}="cpu;gpu"（逗号分隔8值）。
/// </summary>
public class FanService
{
    public static readonly int[] TempPoints = { 30, 40, 50, 60, 70, 80, 90, 100 };

    private readonly DispatcherTimer _timer;

    public bool IsEnabled { get; private set; }
    public int PresetIndex { get; private set; } = 0; // 0-2 → UserFan1..3

    /// <summary>当前 CPU/GPU 风扇目标百分比（应用曲线后更新）。</summary>
    public int LastCpuPercent { get; private set; }
    public int LastGpuPercent { get; private set; }

    public FanService()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => ApplyCurve();
    }

    /// <summary>从注册表读取曲线（"cpu×8;gpu×8"）。</summary>
    public (int[] Cpu, int[] Gpu) LoadCurve(int preset)
    {
        var cpu = new int[8]; var gpu = new int[8];
        for (int i = 0; i < 8; i++) { cpu[i] = 30; gpu[i] = 30; }
        var raw = GMReg.GetStr("UserFan" + (preset + 1), "");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var parts = raw.Split(';');
            Fill(parts, 0, cpu);
            Fill(parts, 1, gpu);
        }
        return (cpu, gpu);
    }

    private static void Fill(string[] parts, int idx, int[] dst)
    {
        if (idx >= parts.Length) return;
        var vals = parts[idx].Split(',');
        for (int i = 0; i < dst.Length && i < vals.Length; i++)
        {
            if (int.TryParse(vals[i].Trim(), out var v))
                dst[i] = Math.Clamp(v, 0, 100);
        }
    }

    public void SaveCurve(int preset, int[] cpu, int[] gpu)
    {
        string Join(int[] a) => string.Join(",", a);
        GMReg.SetStr("UserFan" + (preset + 1), $"{Join(cpu)};{Join(gpu)}");
    }

    public void SetPreset(int preset)
    {
        PresetIndex = Math.Clamp(preset, 0, 2);
        GMReg.Set(GMReg.ValFanControlSelect, (uint)PresetIndex);
    }

    /// <summary>读 EC 当前自定义风扇状态（0=自动 1=自定义）。</summary>
    public int GetEcStatus()
    {
        var buf = GmWmiBuffer.Read(0x206);
        return GmWmiService.Execute(buf) ? (int)(buf.A2 & 0xFF) : -1;
    }

    public void Enable(bool on)
    {
        var buf = GmWmiBuffer.Write(0x206);
        buf.A2 = on ? 1u : 0u;
        GmWmiService.Execute(buf);
        IsEnabled = on;
        GMReg.Set(GMReg.ValFanControlStatus, on ? 1u : 0u);
        if (on)
        {
            ApplyCurve();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            // 恢复 EC 自动调速：三个值全 0xFF
            SetSpeeds(0xFF, 0xFF, 0xFF);
        }
    }

    /// <summary>写转速百分比；SYS 恒 0xFF（本机无第三风扇）。</summary>
    public void SetSpeeds(int cpuPercent, int gpuPercent, int sysPercent = 0xFF)
    {
        var buf = GmWmiBuffer.Write(0x205);
        buf.A2 = (uint)cpuPercent;
        buf.A3 = (uint)gpuPercent;
        buf.A4 = (uint)sysPercent;
        GmWmiService.Execute(buf);
    }

    /// <summary>按当前温度查曲线档位并下发（与原版 MonitorAndControlFan 的分档逻辑一致）。</summary>
    public void ApplyCurve()
    {
        if (!IsEnabled) return;
        var (cpu, gpu) = LoadCurve(PresetIndex);
        var buf = GmWmiBuffer.Read(0x200);
        if (!GmWmiService.Execute(buf)) return;
        int cpuTemp = (int)(buf.A2 & 0xFF);
        int gpuTemp = (int)(buf.A3 & 0xFF);
        int cpuPct = PercentFor(cpu, cpuTemp);
        int gpuPct = PercentFor(gpu, gpuTemp);
        LastCpuPercent = cpuPct;
        LastGpuPercent = gpuPct;
        SetSpeeds(cpuPct, gpuPct, 0xFF);
    }

    /// <summary>温度落在 [T[m], T[m]+10) 区间取第 m 档（与原版一致）。</summary>
    public static int PercentFor(int[] curve, int temp)
    {
        for (int m = 0; m < TempPoints.Length; m++)
        {
            if (temp >= TempPoints[m] && temp - TempPoints[m] < 10)
                return curve[m];
        }
        return temp >= TempPoints[7] ? curve[7] : curve[0];
    }

    /// <summary>启动时恢复上次状态（若注册表记录为启用且 EC 同意）。</summary>
    public void RestoreFromRegistry()
    {
        PresetIndex = (int)Math.Clamp(GMReg.Get(GMReg.ValFanControlSelect, 0), 0, 2);
        if (GMReg.Get(GMReg.ValFanControlStatus, 0) == 1)
        {
            Enable(true);
        }
    }
}
