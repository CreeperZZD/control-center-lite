using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CCLite.Services;
using CCLite.Views;

namespace CCLite;

public partial class MainWindow : Window
{
    private OverviewPage _overviewPage;
    private LightingPage _lightingPage;
    private FanPage _fanPage;
    private SystemPage _systemPage;
    private UserControl _currentPage;

    public MainWindow()
    {
        InitializeComponent();

        _overviewPage = new OverviewPage();
        _lightingPage = new LightingPage();
        _fanPage = new FanPage();
        _systemPage = new SystemPage();

        // 首次定位指示器（无动画）
        Nav_Checked(null, null);
        MoveNavIndicator(animated: false);

        // 最小化到任务栏时也进入低功耗模式
        StateChanged += (_, _) =>
        {
            var app = Application.Current as App;
            if (WindowState == WindowState.Minimized) app?.MinimizeMode();
            else app?.RestoreMode();
        };
    }

    private UserControl PageForChecked()
    {
        if (NavOverview.IsChecked == true) return _overviewPage;
        if (NavLighting.IsChecked == true) return _lightingPage;
        if (NavFan.IsChecked == true) return _fanPage;
        if (NavSystem.IsChecked == true) return _systemPage;
        return _overviewPage;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageHost == null) return;
        var page = PageForChecked();
        MoveNavIndicator(animated: true);

        if (ReferenceEquals(page, _currentPage)) return;
        _currentPage = page;
        PageHost.Content = page;

        // 页面切入过渡：淡入 + 轻微右移复位
        var tt = new TranslateTransform(26, 0);
        page.RenderTransform = tt;
        page.Opacity = 0;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var slide = new DoubleAnimation(26, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        page.BeginAnimation(OpacityProperty, fade);
        tt.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    /// <summary>蓝色指示条滑动到选中导航项的位置。</summary>
    private void MoveNavIndicator(bool animated)
    {
        RadioButton target =
            NavOverview.IsChecked == true ? NavOverview :
            NavLighting.IsChecked == true ? NavLighting :
            NavFan.IsChecked == true ? NavFan :
            NavSystem;

        if (target == null) return;
        // 计算 target 相对指示层画布的 Y 偏移
        var offset = target.TransformToVisual(NavIndicatorLayer).Transform(new Point(0, 0));
        double top = offset.Y + 2; // NavButton 自身 Margin 8,2 → 指示器与按钮对齐

        if (!animated || NavIndicatorShift == null)
        {
            NavIndicatorShift.Y = top;
            return;
        }
        var anim = new DoubleAnimation(top, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        NavIndicatorShift.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    /// <summary>自检用：直接切到灯光页。</summary>
    public void ShowLighting()
    {
        NavLighting.IsChecked = true;
    }

    /// <summary>自检用：选中自定义档案。</summary>
    public void DevSelectCustomChip()
    {
        _lightingPage.DevSelectCustomChip();
    }

    /// <summary>自检用：打开调色面板。</summary>
    public void DevOpenPanel()
    {
        _lightingPage.DevOpenPanel();
    }

    /// <summary>自检用：给灯光页预览上三色（仅预览）。</summary>
    public void DevPaintPreview()
    {
        _lightingPage.DevPaintPreview(
            System.Windows.Media.Color.FromRgb(0x00, 0x40, 0xFF),
            System.Windows.Media.Color.FromRgb(0xFF, 0x00, 0x80),
            System.Windows.Media.Color.FromRgb(0x00, 0xE0, 0xC0));
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        (Application.Current as App)?.RequestHideToTray();
    }

    /// <summary>真正退出时置 true，跳过"关闭即隐藏"的拦截。</summary>
    public bool AllowClose { get; set; }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // 点 X / Alt+F4 = 缩到托盘；真正退出走托盘菜单（ReallyExit 会先置 AllowClose）
        if (AllowClose) return;
        e.Cancel = true;
        (Application.Current as App)?.RequestHideToTray();
    }
}
