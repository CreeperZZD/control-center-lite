using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using CCLite.Services;
using MessageBox = System.Windows.MessageBox;

namespace CCLite.Views;

public partial class SystemPage : UserControl
{
    private bool _suppress;

    public SystemPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RefreshDisplayStatus();
            _suppress = true;
            WinKeyToggle.IsChecked = GMReg.Get(GMReg.ValWinKey, 0) == 1;
            AutoStartToggle.IsChecked = AutoStart.IsEnabled();
            _suppress = false;
            RefreshDiag();
        };
    }

    private void RefreshDiag()
    {
        var (dds, hsr, ok) = DisplayService.GetCapability();
        var buf = GmWmiBuffer.Read(0x201);
        int skuid = GmWmiService.Execute(buf) ? (int)(buf.A2 & 0xFFFF) : -1;
        DiagText.Text =
            $"Gaming WMI (RW_GMWMI)：{(GmWmiService.Available ? "可用" : "不可用")}　|　" +
            $"机型 SKUID：{skuid}　|　独显直连切换：{(dds ? "支持" : "不支持")}　|　" +
            $"高刷切换：{(hsr ? "支持" : "不支持")}";
    }

    // ---------- 显示模式 ----------

    private void RefreshDisplayStatus()
    {
        _suppress = true;
        var (cur, _, ok) = DisplayService.GetStatus();
        if (ok)
        {
            DispHybrid.IsChecked = cur == DisplayService.Hybrid;
            DispDiscrete.IsChecked = cur == DisplayService.Discrete;
            DispUma.IsChecked = cur == DisplayService.Uma;
            DispHintText.Text = $"当前显示模式：{DisplayService.NameOf(cur)}　—　切换需重启生效（流程与原版一致）";
        }
        else
        {
            DispHintText.Text = "无法读取显示模式状态（EC 通信失败）";
        }
        _suppress = false;
    }

    private void DispCard_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppress || !IsLoaded) return;
        uint target = uint.Parse((string)((RadioButton)sender).Tag);
        var (cur, hdmi, ok) = DisplayService.GetStatus();

        if (!ok) { RefreshDisplayStatus(); return; }
        if (cur == target) return;

        if (target == DisplayService.Uma && hdmi == 1)
        {
            MessageBox.Show("检测到外接显示器，不支持切换到集显输出模式。", "轻控面板",
                MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshDisplayStatus();
            return;
        }

        var answer = MessageBox.Show(
            $"切换到「{DisplayService.NameOf(target)}」需要重启电脑才能生效。\n\n是否继续？（继续后可选择立即重启或稍后手动重启）",
            "切换显示模式", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
        {
            RefreshDisplayStatus();
            return;
        }

        if (!DisplayService.SetMode(target))
        {
            MessageBox.Show("写入失败：EC 通信异常。", "轻控面板", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshDisplayStatus();
            return;
        }

        RefreshDisplayStatus();
        var reboot = MessageBox.Show("显示模式已写入，将在重启后生效。\n\n是否立即重启？", "轻控面板",
            MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (reboot == MessageBoxResult.Yes)
        {
            Process.Start(new ProcessStartInfo("shutdown", "/r /t 3 /c \"轻控面板：显示模式切换，即将重启\"")
            {
                CreateNoWindow = true, UseShellExecute = false
            });
        }
    }

    // ---------- Win 键 ----------

    private void WinKey_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress || !IsLoaded) return;
        bool locked = WinKeyToggle.IsChecked == true;
        if (!DisplayService.SetWinKeyLock(locked))
        {
            MessageBox.Show("设置失败：EC 通信异常。", "轻控面板", MessageBoxButton.OK, MessageBoxImage.Warning);
            _suppress = true;
            WinKeyToggle.IsChecked = !locked;
            _suppress = false;
        }
    }

    // ---------- 开机自启（任务计划程序，最高权限） ----------

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress || !IsLoaded) return;
        bool on = AutoStartToggle.IsChecked == true;
        bool ok = AutoStart.Set(on);
        AutoStartHint.Text = on
            ? (ok ? "已创建开机任务（任务计划程序 → CCLitePanel）" : "创建开机任务失败")
            : (ok ? "通过任务计划程序以最高权限随登录启动（保持风扇曲线与灯效联动后台生效）" : "删除开机任务失败");
    }
}
