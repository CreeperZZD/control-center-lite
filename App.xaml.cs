using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CCLite.Services;

namespace CCLite;

public partial class App : Application
{
    public static MonitorService Monitor { get; private set; }
    public static FanService Fan { get; private set; }

    /// <summary>系统模式被切换（托盘/页面发起后广播，UI 刷新用）。</summary>
    public static event Action<uint> SystemModeChanged;

    private Mutex _mutex;
    private System.Windows.Forms.NotifyIcon _tray;
    private System.Windows.Forms.ContextMenuStrip _menu;
    private MainWindow _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例
        _mutex = new Mutex(true, "CCLite_SingleInstance_8829", out var isNew);
        if (!isNew)
        {
            System.Windows.MessageBox.Show("控制中心已在运行。", "控制中心",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        bool startInTray = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("Dispatcher", args.Exception);
            System.Windows.MessageBox.Show($"发生错误：{args.Exception.Message}", "控制中心");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("AppDomain", args.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("Task", args.Exception);
            args.SetObserved();
        };

        // 加载界面：先渲染 Splash，再把初始化挪到后台优先级执行（否则 Splash 不会绘制）
        var splash = new SplashWindow();
        if (!startInTray)
        {
            splash.Show();
            splash.UpdateLayout();
        }

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            new Action(() =>
        {
            // 机型检查：必须存在广达 Gaming WMI 接口
            if (!GmWmiService.EnsureInit())
            {
                if (splash.IsLoaded) splash.Close();
                System.Windows.MessageBox.Show(
                    "未检测到 Gaming WMI 接口（root\\wmi\\RW_GMWMI）。\n" +
                    "本面板仅适用于广达模具机型（隐星G16 / 神舟T8 等），请确认：\n" +
                    "1. 以管理员身份运行；\n2. 原版 Control Center 已安装（BIOS ACPI 接口随其启用）。",
                    "控制中心", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown();
                return;
            }

            Monitor = new MonitorService();
            Fan = new FanService();

            _mainWindow = new MainWindow();
            MainWindow = _mainWindow;

            InitTrayIcon();

            Monitor.Start();
            Fan.RestoreFromRegistry();

            // 与原版一致：亮度/灯效按系统模式记忆，启动时恢复当前模式
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var m = PowerModeService.GetCurrentMode();
                    LedService.ApplyProfileForMode(m);
                    BrightnessService.ApplyForMode(m);
                }
                catch { }
            });

            if (startInTray)
            {
                Monitor.SetFastPolling(false);   // 隐藏状态：不轮询硬件，仅低频检查模式变化
            }
            else
            {
                _mainWindow.Show();
                splash.Finish();   // 推到 100%
            }

            // 让 100% 状态停留一瞬再收起加载窗
            var st = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
            st.Tick += (_, _) => { st.Stop(); if (splash.IsLoaded) splash.Close(); };
            st.Start();

            // 自检开关：--shot 渲染灯光页到 PNG 后退出（开发验证用）
            if (e.Args.Any(a => a.Equals("--shot", StringComparison.OrdinalIgnoreCase)))
            {
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
                t.Tick += (_, _) =>
                {
                    t.Stop();
                    try
                    {
                        _mainWindow.ShowLighting();
                        // 等页面淡入动画（240ms）完成后再截图，否则截到透明帧
                        var t2 = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                        t2.Tick += (_, _) => { t2.Stop(); RenderShot(); };
                        t2.Start();
                        return;
                    }
                    catch (Exception ex) { LogCrash("Shot", ex); }
                    ReallyExit();
                };
                t.Start();
            }
        }));

        // 退出
    }

    private void RenderShot()
    {
        try
        {
                    _mainWindow.DevPaintPreview();
                    _mainWindow.DevOpenPanel();
                    // 让 WPF 渲染线程产出一帧新的合成帧，否则 RenderTargetBitmap 会拿到旧帧
                    _mainWindow.UpdateLayout();
                    Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                    Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                    var w = (int)_mainWindow.ActualWidth;
                    var h = (int)_mainWindow.ActualHeight;
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96,
                        System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(_mainWindow);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    var dir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CCLite");
                    System.IO.Directory.CreateDirectory(dir);
                    using (var fs = System.IO.File.Create(System.IO.Path.Combine(dir, "shot.png")))
                        enc.Save(fs);
        }
        catch (Exception ex) { LogCrash("Shot", ex); }
        // 保留窗口 12 秒，供外部屏幕截图验证弹窗；期间窗口显示灯光页+调色盘
        var keep = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        keep.Tick += (_, _) => { keep.Stop(); ReallyExit(); };
        keep.Start();
    }

    // ---------- 系统模式 ----------

    /// <summary>统一的模式切换入口（写 EC + 电源计划 + 灯效联动 + 广播事件）。</summary>
    public static bool SwitchMode(uint mode)
    {
        if (!PowerModeService.SetMode(mode)) return false;
        Monitor?.MarkInternalModeSet();
        // 切模式 = 恢复该模式记忆的屏幕亮度（与原版一致）
        System.Threading.Tasks.Task.Run(() =>
        {
            try { BrightnessService.ApplyForMode(mode); } catch { }
        });
        SystemModeChanged?.Invoke(mode);
        return true;
    }

    // ---------- 托盘 ----------

    private void InitTrayIcon()
    {
        _tray = new System.Windows.Forms.NotifyIcon { Text = "控制中心" };

        // 图标：优先内嵌资源，失败则从 exe 自身提取（防止资源流加载失败导致托盘图标空白）
        try
        {
            var sri = GetResourceStream(new Uri("pack://application:,,,/assets/tray.ico"));
            _tray.Icon = new System.Drawing.Icon(sri.Stream);
        }
        catch
        {
            try { _tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath); }
            catch { _tray.Icon = System.Drawing.SystemIcons.Application; }
        }

        _menu = new System.Windows.Forms.ContextMenuStrip();
        BuildTrayMenu(_menu);                       // 启动即建好全部菜单项（首次右键零延迟）
        _tray.ContextMenuStrip = _menu;
        _menu.Opening += (_, _) => RefreshTrayMenuStates();   // 展开前只刷新勾选状态（毫秒级）
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowMain();
        };
        _tray.DoubleClick += (_, _) => ShowMain();
        _tray.Visible = true;   // 图标设好后再显示

        // Win11：把图标提升为任务栏常驻（等价于 设置→个性化→任务栏→其他系统托盘图标 打开）
        // explorer 可能在图标添加后延迟登记 NotifyIconSettings 条目，因此多次重试
        System.Threading.Tasks.Task.Run(() =>
        {
            int[] delays = { 2500, 15000, 60000, 180000, 300000 };
            foreach (var d in delays)
            {
                System.Threading.Thread.Sleep(d);
                if (PromoteTrayIcon()) break;
            }
        });
    }

    /// <summary>在 NotifyIconSettings 中找到本程序条目并设置 IsPromoted=1。返回是否找到条目。</summary>
    private static bool PromoteTrayIcon()
    {
        try
        {
            using var baseKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", true);
            if (baseKey == null) return false;
            foreach (var name in baseKey.GetSubKeyNames())
            {
                using var k = baseKey.OpenSubKey(name, true);
                var exe = Convert.ToString(k?.GetValue("ExecutablePath"));
                if (!string.IsNullOrEmpty(exe) && string.Equals(exe, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                {
                    k.SetValue("IsPromoted", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>托盘菜单"固定到任务栏"：先尝试提升；若 explorer 尚未登记条目，重启 explorer 强制登记。</summary>
    private void PinTrayIconToTaskbar()
    {
        if (PromoteTrayIcon())
        {
            TrayToast("已设置图标常驻任务栏");
            return;
        }
        var answer = System.Windows.MessageBox.Show(
            "系统尚未登记本程序的托盘图标（需要一次资源管理器刷新）。\n\n" +
            "是否立即重启资源管理器？桌面和任务栏会闪一下，几秒后恢复，不影响正在打开的程序。",
            "固定托盘图标", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.OK) return;

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd", "/c taskkill /f /im explorer.exe & start explorer.exe")
        {
            CreateNoWindow = true, UseShellExecute = false
        });

        // explorer 重启后图标会重新注册，多次重试提升
        System.Threading.Tasks.Task.Run(() =>
        {
            for (int i = 0; i < 10; i++)
            {
                System.Threading.Thread.Sleep(3000);
                if (PromoteTrayIcon()) break;
            }
        });
    }

    private System.Windows.Forms.ToolStripMenuItem _miModeOffice, _miModeGaming, _miModePerf;
    private System.Windows.Forms.ToolStripMenuItem _miBriOff, _miBriMid, _miBriFull;
    private System.Windows.Forms.ToolStripMenuItem _miLedOn, _miWinKey, _miAutoStart;
    private bool _autoStartCached;

    private void BuildTrayMenu(System.Windows.Forms.ContextMenuStrip menu)
    {
        // 系统模式
        var modeItem = new System.Windows.Forms.ToolStripMenuItem("系统模式");
        _miModeOffice = AddRadioMenuItem(modeItem, "办公模式", () => SwitchModeSafe(PowerModeService.ModeOffice));
        _miModeGaming = AddRadioMenuItem(modeItem, "游戏模式", () => SwitchModeSafe(PowerModeService.ModeGaming));
        _miModePerf = AddRadioMenuItem(modeItem, "狂暴模式", () => SwitchModeSafe(PowerModeService.ModePerformance));
        menu.Items.Add(modeItem);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        // 键盘亮度三档
        var briItem = new System.Windows.Forms.ToolStripMenuItem("键盘亮度");
        _miBriOff = AddRadioMenuItem(briItem, "熄灭", () => TraySetBrightness(0));
        _miBriMid = AddRadioMenuItem(briItem, "50%", () => TraySetBrightness(1));
        _miBriFull = AddRadioMenuItem(briItem, "100%", () => TraySetBrightness(2));
        menu.Items.Add(briItem);

        // 灯效开关（当前系统模式对应档案）
        _miLedOn = new System.Windows.Forms.ToolStripMenuItem("灯效开关");
        _miLedOn.Click += (_, _) => TrayToggleMaster(_miLedOn.Checked != true);
        menu.Items.Add(_miLedOn);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        // Win 键锁定
        _miWinKey = new System.Windows.Forms.ToolStripMenuItem("Win 键锁定");
        _miWinKey.Click += (_, _) =>
        {
            if (!DisplayService.SetWinKeyLock(_miWinKey.Checked != true))
                TrayToast("设置失败：EC 通信异常");
        };
        menu.Items.Add(_miWinKey);

        // 开机自启
        _miAutoStart = new System.Windows.Forms.ToolStripMenuItem("开机自启");
        _miAutoStart.Click += (_, _) =>
        {
            if (!AutoStart.Set(_miAutoStart.Checked != true)) TrayToast("设置开机自启失败");
        };
        menu.Items.Add(_miAutoStart);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("把图标固定到任务栏", null, (_, _) => PinTrayIconToTaskbar());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("打开 面板", null, (_, _) => ShowMain());
        menu.Items.Add("退出", null, (_, _) => ReallyExit());
    }

    /// <summary>只刷新勾选状态（不重建菜单，保证首次右键也流畅）。</summary>
    private void RefreshTrayMenuStates()
    {
        var mode = PowerModeService.GetCurrentMode();
        _miModeOffice.Checked = mode == PowerModeService.ModeOffice;
        _miModeGaming.Checked = mode == PowerModeService.ModeGaming;
        _miModePerf.Checked = mode == PowerModeService.ModePerformance;

        var bri = CurrentBriLevelIndex();
        _miBriOff.Checked = bri == 0;
        _miBriMid.Checked = bri == 1;
        _miBriFull.Checked = bri == 2;

        _miLedOn.Checked = GMReg.Get(GMReg.ValLedWizard, 1) == 1;
        _miWinKey.Checked = GMReg.Get(GMReg.ValWinKey, 0) == 1;
        _miAutoStart.Checked = _autoStartCached;
    }

    /// <summary>后台刷新开机自启缓存（schtasks 查询较慢，不放在右键路径上）。</summary>
    private void RefreshAutoStartCache()
    {
        System.Threading.Tasks.Task.Run(() => _autoStartCached = AutoStart.IsEnabled());
    }

    private static System.Windows.Forms.ToolStripMenuItem AddRadioMenuItem(System.Windows.Forms.ToolStripMenuItem parent, string text, Action onClick)
    {
        var it = new System.Windows.Forms.ToolStripMenuItem(text);
        it.Click += (_, _) => onClick();
        parent.DropDownItems.Add(it);
        return it;
    }

    private void SwitchModeSafe(uint mode)
    {
        if (mode != PowerModeService.ModeOffice && !Monitor.PollOnce().AcOnline)
        {
            TrayToast("电池供电下无法切换到游戏/狂暴模式");
            return;
        }
        if (!SwitchMode(mode)) TrayToast("切换失败：EC 通信异常");
    }

    // ---------- 托盘灯光操作 ----------

    private static (uint Module, uint[] Parts) CurrentLedProfile()
    {
        var moduleId = Math.Clamp(GMReg.Get(GMReg.ValLedModule, 0), 0, 3);
        var profile = LedService.ProfileOfModule(moduleId);
        return (moduleId, LedService.LoadProfile(profile));
    }

    /// <summary>当前键盘亮度档位：0=熄灭 1=50% 2=100%（NLY 机型 EC 仅认 0/1/2）。</summary>
    private static int CurrentBriLevelIndex()
    {
        var (_, parts) = CurrentLedProfile();
        byte bri = LedService.GetBrightness(parts[4]);
        if (GMReg.Get(GMReg.ValLedWizard, 1) == 0) return 0;
        return bri switch { 0 => 0, 1 => 1, _ => 2 };
    }

    private void TraySetBrightness(int level)
    {
        byte bri = level switch { 0 => 0, 1 => 1, _ => 2 };
        var (moduleId, parts) = CurrentLedProfile();
        var profile = LedService.ProfileOfModule(moduleId);
        for (int i = 0; i < 3; i++)
        {
            parts[2 + i] = LedService.MakeData(LedService.GetMode(parts[2 + i]), bri, LedService.GetColor(parts[2 + i]));
            LedService.WriteZone(LedService.KeyboardSlots[i], parts[2 + i]);
        }
        LedService.SaveKeyboardToProfile(profile, parts);
        GMReg.Set(GMReg.ValAlpha, bri);
        if (level > 0 && GMReg.Get(GMReg.ValLedWizard, 1) == 0)
            GMReg.Set(GMReg.ValLedWizard, 1);   // 调亮度视为开灯
    }

    private void TrayToggleMaster(bool on)
    {
        var curMode = PowerModeService.GetCurrentMode();
        var (profile, moduleId) = LedService.ProfileForMode(curMode);
        LedService.SetProfileOn(profile, on);
        LedService.ApplyProfile(profile, moduleId);   // 开=应用档案 关=熄灭
        System.Threading.Tasks.Task.Run(() => _autoStartCached = AutoStart.IsEnabled());
    }

    private void TrayToast(string msg)
    {
        if (_tray != null) _tray.ShowBalloonTip(2500, "控制中心", msg, System.Windows.Forms.ToolTipIcon.Info);
    }

    // ---------- 窗口 / 生命周期 ----------

    private void ShowMain()
    {
        Monitor?.SetFastPolling(true);
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    /// <summary>主窗口关闭按钮 → 缩到托盘（与原版行为一致），退出需走托盘菜单。</summary>
    public void RequestHideToTray()
    {
        _mainWindow.Hide();
        MinimizeMode();
    }

    /// <summary>低功耗模式：隐藏到托盘 / 最小化到任务栏时降低轮询频率并修剪内存。</summary>
    public void MinimizeMode()
    {
        Monitor?.SetFastPolling(false);
        TrimWorkingSet();
    }

    public void RestoreMode()
    {
        Monitor?.SetFastPolling(true);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProc, IntPtr min, IntPtr max);

    private static void LogCrash(string source, Exception ex)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CCLite");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {source}: {ex}\r\n\r\n");
        }
        catch { }
    }

    private static void TrimWorkingSet()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1));
        }
        catch { }
    }

    public void ReallyExit()
    {
        if (_tray != null) _tray.Visible = false;
        if (_mainWindow != null) _mainWindow.AllowClose = true;
        if (Fan != null && Fan.IsEnabled)
        {
            try { Fan.Enable(false); } catch { } // 退出前恢复 EC 自动调速
        }
        Monitor?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.ReleaseMutex();
        base.OnExit(e);
    }
}
