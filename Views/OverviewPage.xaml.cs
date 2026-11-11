using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CCLite.Services;
using MessageBox = System.Windows.MessageBox;

namespace CCLite.Views;

public partial class OverviewPage : UserControl
{
    private bool _loading;
    private bool _avReady;
    private DiskInfo[] _lastDisks = Array.Empty<DiskInfo>();

    public OverviewPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RefreshModeSelection();
            LoadConfig();
            InitAudioVisual();
            OnSnapshot(App.Monitor.PollOnce());
            App.Monitor.SnapshotUpdated += OnSnapshot;
            App.Monitor.ModeChangedByExternal += OnExternalModeChanged;
            App.SystemModeChanged += OnSystemModeChanged;
        };
        Unloaded += (_, _) =>
        {
            App.Monitor.SnapshotUpdated -= OnSnapshot;
            App.Monitor.ModeChangedByExternal -= OnExternalModeChanged;
            App.SystemModeChanged -= OnSystemModeChanged;
        };
    }

    private void OnExternalModeChanged(uint mode) => Dispatcher.Invoke(() => { RefreshModeSelection(); SyncBrightnessUI(); });
    private void OnSystemModeChanged(uint mode) => Dispatcher.Invoke(() => { RefreshModeSelection(); SyncBrightnessUI(); });

    /// <summary>模式切换后把亮度滑条同步为实际屏幕亮度。</summary>
    private void SyncBrightnessUI()
    {
        if (!_avReady || !BrightnessSlider.IsEnabled) return;
        int bri = BrightnessService.Get();
        if (bri < 0) return;
        BrightnessSlider.Value = bri;
        BrightnessText.Text = bri + "%";
    }

    // ---------- 本机配置（可编辑，存注册表；其他电脑用户可自行修改） ----------

    private static readonly (string Key, string Default)[] CfgFields =
    {
        ("CfgModel", "七彩虹 G16 24 笔记本"),
        ("CfgScreen", "16英寸 2560×1600（2.5K · 16:10）"),
        ("CfgCpu", "英特尔® 酷睿™ i7-13650HX（14核 / 16线程）"),
        ("CfgGpu", "NVIDIA® GeForce RTX™ 4070 Laptop GPU（8GB GDDR6）"),
        ("CfgRam", "32GB DDR5 4800MHz"),
    };

    private void LoadConfig()
    {
        var keys = new[] { "CfgModelT", "CfgScreenT", "CfgCpuT", "CfgGpuT", "CfgRamT" };
        for (int i = 0; i < CfgFields.Length; i++)
        {
            var v = GMReg.GetStr(CfgFields[i].Key, CfgFields[i].Default);
            if (string.IsNullOrWhiteSpace(v)) v = CfgFields[i].Default;
            var t = (TextBlock)FindName(keys[i]);
            var box = (TextBox)FindName(CfgFields[i].Key);
            if (t != null) t.Text = v;
            if (box != null) box.Text = v;
        }
    }

    private void CfgEdit_Click(object sender, RoutedEventArgs e)
    {
        var keys = new[] { "CfgModelT", "CfgScreenT", "CfgCpuT", "CfgGpuT", "CfgRamT" };
        for (int i = 0; i < CfgFields.Length; i++)
        {
            ((TextBlock)FindName(keys[i])).Visibility = Visibility.Collapsed;
            ((TextBox)FindName(CfgFields[i].Key)).Visibility = Visibility.Visible;
        }
        BtnCfgEdit.Visibility = Visibility.Collapsed;
        BtnCfgSave.Visibility = Visibility.Visible;
        BtnCfgCancel.Visibility = Visibility.Visible;
    }

    private void CfgSave_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (key, _) in CfgFields)
        {
            var v = ((TextBox)FindName(key)).Text.Trim();
            if (v == "") v = CfgFields.First(f => f.Key == key).Default;
            GMReg.SetStr(key, v);
        }
        ExitCfgEdit();
        LoadConfig();
    }

    private void CfgCancel_Click(object sender, RoutedEventArgs e)
    {
        ExitCfgEdit();
        LoadConfig();
    }

    private void ExitCfgEdit()
    {
        var keys = new[] { "CfgModelT", "CfgScreenT", "CfgCpuT", "CfgGpuT", "CfgRamT" };
        for (int i = 0; i < CfgFields.Length; i++)
        {
            ((TextBlock)FindName(keys[i])).Visibility = Visibility.Visible;
            ((TextBox)FindName(CfgFields[i].Key)).Visibility = Visibility.Collapsed;
        }
        BtnCfgEdit.Visibility = Visibility.Visible;
        BtnCfgSave.Visibility = Visibility.Collapsed;
        BtnCfgCancel.Visibility = Visibility.Collapsed;
    }

    // ---------- 屏幕亮度 / 系统音量 ----------

    private void InitAudioVisual()
    {
        _avReady = false;

        int bri = BrightnessService.Get();
        if (bri >= 0)
        {
            BrightnessSlider.IsEnabled = true;
            BrightnessSlider.Value = bri;
            BrightnessText.Text = bri + "%";
        }
        else
        {
            BrightnessSlider.IsEnabled = false;
            BrightnessText.Text = "不可用";
        }

        if (AudioService.Available)
        {
            int vol = AudioService.GetVolumePercent();
            if (vol >= 0) VolumeSlider.Value = vol;
            VolumeText.Text = VolumeSlider.Value.ToString("0") + "%";
            MuteToggle.IsChecked = AudioService.IsMuted();
        }
        else
        {
            VolumeSlider.IsEnabled = false;
            MuteToggle.IsEnabled = false;
            VolumeText.Text = "不可用";
        }

        var hint = (BrightnessSlider.IsEnabled ? "" : "外接显示器亮度不支持系统调节（需 DDC/CI）。")
                 + (VolumeSlider.IsEnabled ? "" : "未找到默认音频输出设备。");
        AvHintText.Text = hint;
        AvHintText.Visibility = hint == "" ? Visibility.Collapsed : Visibility.Visible;   // 无提示不留空黑框
        _avReady = true;
    }

    private void Brightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessText == null) return;
        BrightnessText.Text = ((int)e.NewValue) + "%";
        if (!_avReady || !BrightnessSlider.IsEnabled) return;
        BrightnessService.Set((int)e.NewValue);
        BrightnessService.SaveForMode(PowerModeService.GetCurrentMode(), (int)e.NewValue);   // 记忆到当前系统模式
    }

    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeText == null) return;
        VolumeText.Text = ((int)e.NewValue) + "%";
        if (!_avReady || !VolumeSlider.IsEnabled) return;
        AudioService.SetVolumePercent((int)e.NewValue);
    }

    private void Mute_Changed(object sender, RoutedEventArgs e)
    {
        if (!_avReady || !MuteToggle.IsEnabled) return;
        AudioService.SetMute(MuteToggle.IsChecked == true);
    }

    private void RefreshModeSelection()
    {
        _loading = true;
        var mode = PowerModeService.GetCurrentMode();
        CardOffice.IsChecked = mode == PowerModeService.ModeOffice;
        CardGaming.IsChecked = mode == PowerModeService.ModeGaming;
        CardPerf.IsChecked = mode == PowerModeService.ModePerformance;
        _loading = false;
    }

    private void ModeCard_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        var card = (RadioButton)sender;
        uint mode = uint.Parse((string)card.Tag);

        if (mode != PowerModeService.ModeOffice && !App.Monitor.PollOnce().AcOnline)
        {
            RefreshModeSelection();
            MessageBox.Show("电池供电下无法切换到游戏/狂暴模式（与原版行为一致），请接通电源后再试。",
                "控制中心", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!App.SwitchMode(mode))
        {
            MessageBox.Show("切换失败：EC 通信异常。", "控制中心", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshModeSelection();
        }
    }

    private void OnSnapshot(MonitorSnapshot s)
    {
        Dispatcher.Invoke(() =>
        {
            if (s.Ok)
            {
                CpuTempText.Text = $"{s.CpuTemp}°C";
                CpuLoadText.Text = $"占用 {s.CpuLoad}%";
                CpuFanText.Text = $"风扇 {s.CpuRpm} RPM";
                GpuTempText.Text = $"{s.GpuTemp}°C";
                GpuFanText.Text = $"风扇 {s.GpuRpm} RPM";
                BatteryWarnBanner.Visibility = s.AcOnline ? Visibility.Collapsed : Visibility.Visible;
                CardGaming.IsEnabled = s.AcOnline;
                CardPerf.IsEnabled = s.AcOnline;
            }
            RamBar.Value = s.RamUsedPercent;
            RamDetailText.Text = s.RamDetail;
            RamPctText.Text = $"{s.RamUsedPercent:0}%";
            RenderDisks(s.Disks);
        });
    }

    private void RenderDisks(DiskInfo[] disks)
    {
        if (disks == null) return;
        bool sameAsLast = disks.Length == _lastDisks.Length;
        if (sameAsLast)
        {
            for (int i = 0; i < disks.Length && sameAsLast; i++)
                sameAsLast = disks[i].Label == _lastDisks[i].Label;
        }
        _lastDisks = disks;
        if (sameAsLast && DiskHost.Children.Count == disks.Length)
        {
            // 结构未变，只更新数值
            for (int i = 0; i < disks.Length; i++) UpdateDiskRow(DiskHost.Children[i], disks[i]);
            return;
        }

        DiskHost.Children.Clear();
        foreach (var d in disks) DiskHost.Children.Add(BuildDiskRow(d));
    }

    private static UIElement BuildDiskRow(DiskInfo d)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 2) };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBlock { Text = d.Label, FontSize = 13, Foreground = (Brush)Application.Current.FindResource("TextBrush"), VerticalAlignment = VerticalAlignment.Center };
        var detail = new TextBlock { Text = d.Detail, FontSize = 12, Foreground = (Brush)Application.Current.FindResource("TextSecondaryBrush") };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(detail, 1);
        header.Children.Add(name);
        header.Children.Add(detail);

        var bar = new ProgressBar
        {
            Style = (Style)Application.Current.FindResource("ModernProgress"),
            Maximum = 100, Margin = new Thickness(0, 7, 0, 0), Value = d.Pct
        };
        var pct = new TextBlock
        {
            Text = $"{d.Pct:0}%", FontSize = 12,
            Foreground = (Brush)Application.Current.FindResource("TextSecondaryBrush"),
            Margin = new Thickness(0, 5, 0, 0)
        };
        panel.Children.Add(header);
        panel.Children.Add(bar);
        panel.Children.Add(pct);
        panel.Tag = (bar, pct, detail);
        return panel;
    }

    private static void UpdateDiskRow(UIElement element, DiskInfo d)
    {
        if (element is StackPanel p && p.Tag is (ProgressBar bar, TextBlock pct, TextBlock detail))
        {
            bar.Value = d.Pct;
            pct.Text = $"{d.Pct:0}%";
            detail.Text = d.Detail;
        }
    }
}
