using System;
using System.Diagnostics;

namespace CCLite.Services;

/// <summary>开机自启：任务计划程序最高权限（WMI 写入需要管理员，Run 键不够）。</summary>
public static class AutoStart
{
    public const string TaskName = "CCLitePanel";

    private static bool RunSchtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks", args)
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true
            });
            p.WaitForExit(8000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    public static bool IsEnabled() => RunSchtasks($"/Query /TN {TaskName}");

    public static bool Set(bool on)
    {
        if (on)
        {
            var exe = Environment.ProcessPath;
            // --tray：开机启动只驻留托盘，不弹出主界面
            return RunSchtasks($"/Create /TN {TaskName} /TR \"\\\"{exe}\\\" --tray\" /SC ONLOGON /RL HIGHEST /F");
        }
        return RunSchtasks($"/Delete /TN {TaskName} /F");
    }
}
