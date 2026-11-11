using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace CCLite.Services;

/// <summary>一块磁盘的占用信息。</summary>
public struct DiskInfo
{
    public string Label;    // "硬盘 (C:)"
    public double Pct;      // 0-100
    public string Detail;   // "512/1024 GB"
}

/// <summary>一次硬件监控快照。</summary>
public struct MonitorSnapshot
{
    public int CpuTemp;
    public int GpuTemp;
    public int SysTemp;
    public int CpuRpm;
    public int GpuRpm;
    public int CpuLoad;          // 0-100
    public float RamUsedPercent; // 0-100
    public string RamDetail;     // "18.6/31.8 GB"
    public DiskInfo[] Disks;     // 所有接入磁盘
    public bool AcOnline;
    public bool Ok;
}

/// <summary>
/// 后台轮询（2s）：EC 温度/转速 + 系统 CPU/内存/磁盘 + 电源状态；
/// 同时观察注册表 ModeBeforeDC —— 原版 CC / Fn 热键切换模式时触发灯效联动（外围修复原版缺陷）。
/// </summary>
public class MonitorService : IDisposable
{
    private Timer _timer;
    private volatile bool _polling;
    private volatile bool _visible = true;
    private uint _lastMode;

    /// <summary>内部发起的模式切换时间戳（3 秒内不再触发联动，避免重复）。</summary>
    private DateTime _internalModeSetUtc = DateTime.MinValue;

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    private PerformanceCounter _cpuCounter;

    public event Action<MonitorSnapshot> SnapshotUpdated;
    public event Action<uint> ModeChangedByExternal;

    public MonitorService()
    {
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _cpuCounter.NextValue(); // 首次采样无效
        }
        catch { _cpuCounter = null; }
    }

    /// <summary>标记"这次模式变更由本面板自己发起"，观察器跳过。</summary>
    public void MarkInternalModeSet() => _internalModeSetUtc = DateTime.UtcNow;

    public void Start()
    {
        _lastMode = PowerModeService.GetCurrentMode();
        _timer = new Timer(_ =>
        {
            if (_polling) return;
            _polling = true;
            try
            {
                // 仅在窗口可见时读取硬件（WMI 调用是主要开销）；
                // 隐藏到托盘/最小化时只做一次廉价的注册表比较，保证灯效联动仍生效
                if (_visible)
                {
                    var snap = PollOnce();
                    SnapshotUpdated?.Invoke(snap);
                }
                WatchExternalModeChange();
            }
            catch { }
            finally { _polling = false; }
        }, null, 500, 1000);
    }

    /// <summary>窗口可见=实时刷新；隐藏=停止硬件轮询，仅低频检查系统模式变化（几乎零开销）。</summary>
    public void SetFastPolling(bool visible)
    {
        _visible = visible;
        _timer?.Change(visible ? 300 : 5000, visible ? 1000 : 8000);
    }

    public MonitorSnapshot PollOnce()
    {
        var snap = new MonitorSnapshot();
        var buf = GmWmiBuffer.Read(0x200);
        if (GmWmiService.Execute(buf))
        {
            snap.CpuTemp = (int)(buf.A2 & 0xFF);
            snap.GpuTemp = (int)(buf.A3 & 0xFF);
            snap.CpuRpm = (int)(buf.A4 & 0xFFFF);
            snap.GpuRpm = (int)(buf.A5 & 0xFFFF);
            snap.Ok = true;
        }
        var buf2 = GmWmiBuffer.Read(0x207);
        if (GmWmiService.Execute(buf2))
        {
            snap.SysTemp = (int)(buf2.A2 & 0xFF);
        }
        snap.AcOnline = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus ==
                        System.Windows.Forms.PowerLineStatus.Online;

        try { snap.CpuLoad = _cpuCounter == null ? 0 : (int)Math.Min(100, _cpuCounter.NextValue()); } catch { }

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            snap.RamUsedPercent = mem.dwMemoryLoad;
            snap.RamDetail = $"{(mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0:0.0}/{mem.ullTotalPhys / 1073741824.0:0.0} GB";
        }

        // 所有接入磁盘（固定硬盘 + 移动硬盘/U盘，排除光驱等）
        try
        {
            var disks = new List<DiskInfo>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable) continue;
                    if (d.TotalSize <= 0) continue;
                    double total = d.TotalSize, free = d.AvailableFreeSpace;
                    var type = d.DriveType == DriveType.Fixed ? "硬盘" : "移动盘";
                    disks.Add(new DiskInfo
                    {
                        Label = $"{type} ({d.Name})",
                        Pct = (total - free) / total * 100,
                        Detail = $"{(total - free) / 1073741824.0:0}/{total / 1073741824.0:0} GB",
                    });
                }
                catch { }
            }
            snap.Disks = disks.ToArray();
        }
        catch { snap.Disks = Array.Empty<DiskInfo>(); }
        return snap;
    }

    /// <summary>检测注册表 ModeBeforeDC 被外部（原版 CC / Fn 热键 / powercfg）改变 → 联动灯效。</summary>
    private void WatchExternalModeChange()
    {
        var mode = PowerModeService.GetCurrentMode();
        if (mode != _lastMode)
        {
            _lastMode = mode;
            if ((DateTime.UtcNow - _internalModeSetUtc).TotalSeconds > 3)
            {
                // 外部切换 → 重放该模式灯效（这就是原版缺的那一步）
                try
                {
                    LedService.ApplyProfileForMode(mode);
                    BrightnessService.ApplyForMode(mode);   // 外部切模式同样恢复该模式亮度
                }
                catch { }
                ModeChangedByExternal?.Invoke(mode);
            }
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _cpuCounter?.Dispose();
    }
}
