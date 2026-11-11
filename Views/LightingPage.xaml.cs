using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CCLite.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace CCLite.Views;

public partial class LightingPage : UserControl
{
    private static readonly string[] PresetBrushHex =
    {
        "#FF0000", "#FF7F00", "#FFFF00", "#00FF00", "#00FF7F", "#00FFFF",
        "#0000FF", "#7F00FF", "#FF00FF", "#FF6FA8", "#FFC0CB", "#FFFFFF",
    };

    /// <summary>NLY 机型（SKUID 52）键盘亮度 EC 仅认 0/1/2 三档：0=熄灭档 1=50% 2=100%。</summary>
    public static readonly byte[] BrightLevels = { 0, 1, 2 };

    private uint[] _parts = new uint[5];      // 0=logo 1=trunk 2=A区 3=B区 4=C区
    private string _editingProfile = "";
    private int _activeZone = 0;              // popup 当前编辑：0/1/2=分区，-1=统一颜色
    private bool _suppress;
    private bool _isOn = true;
    private bool _unified;

    private readonly List<(Rectangle Rect, TextBlock Label, int Zone)> _previewKeys = new();   // 预览键盘键格（含文字，便于按亮度切换对比色）
    private readonly List<byte> _comboModes = new();   // 下拉框索引 → 灯效模式（已跳过"关闭"，显示与取值共用）
    /*
     * 分区（用户实测确认）：
     * A区(0)：Esc~F5、`~4、Tab~R、Caps~F、Shift~C、LCtrl/Win/LAlt
     * B区(1)：F6~PrtSc、5~Backspace、T~]、G~'、B~/、Space/RAlt/Fn/RCtrl、整个数字小键盘（到 + 含）
     * C区(2)：ScrLk/Pause、\、Enter、RShift、方向键
     */

    public LightingPage()
    {
        InitializeComponent();

        for (int i = 0; i < PresetBrushHex.Length; i++)
        {
            var btn = (Button)FindName("P" + i);
            btn.Background = MakeBrush(PresetBrushHex[i]);
        }

        foreach (var (mode, name) in LedService.EffectList)
        {
            if (mode == LedService.ModeOff) continue;   // 关闭不是灯效，用总开关
            _comboModes.Add(mode);
            EffectCombo.Items.Add(name);
        }
        EffectCombo.SelectedIndex = 0;

        // 圆形调色盘
        WheelImage.Source = BuildColorWheel(WheelSize);

        var buf = GmWmiBuffer.Read(0x201);
        bool nonRgb = GmWmiService.Execute(buf) && buf.A2 == 48;
        if (nonRgb)
        {
            NoRgbBanner.Visibility = Visibility.Visible;
            LedContentPanel.Visibility = Visibility.Collapsed;
            return;
        }

        BuildKeyboardPreview();
        KeyboardPreview.MouseLeftButtonDown += PreviewRegion_Click;

        Loaded += (_, _) =>
        {
            LoadProfileSelection();
            RefreshMasterToggle();
            // 弹窗常驻：点主窗口任意处或按 Esc 时收起（点击弹窗内部不会触发这里）
            _hostWindow = Window.GetWindow(this);
            if (_hostWindow != null)
            {
                _hostWindow.PreviewKeyDown += Host_PreviewKeyDown;
            }
        };

        // 离开灯光页：灯效自动回到"跟随当前模式"（重放当前模式档案），编辑上下文复位
        App.SystemModeChanged += m => Dispatcher.Invoke(OnModeChangedForPage);
        App.Monitor.ModeChangedByExternal += m => Dispatcher.Invoke(OnModeChangedForPage);

        Unloaded += (_, _) =>
        {
            ClosePopup();
            if (_hostWindow != null)
            {
                _hostWindow.PreviewKeyDown -= Host_PreviewKeyDown;
                _hostWindow = null;
            }
            try
            {
                LedService.ApplyProfileForMode(PowerModeService.GetCurrentMode());
                ChipFollow.IsChecked = true;
            }
            catch { }
        };
    }

    private static Brush MakeBrush(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    private static Color HsvColor(double hue, double satPercent, double value255)
    {
        double h = hue % 360 / 360.0;
        double s = Math.Clamp(satPercent, 0, 100) / 100.0;
        double v = Math.Clamp(value255, 0, 255) / 255.0;
        int i = (int)(h * 6) % 6;
        double f = h * 6 - Math.Floor(h * 6);
        double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
        (double r, double g, double b) = i switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    // ---------- 圆形调色盘 ----------

    private const int WheelSize = 150;   // 与 XAML 中 Image/Grid 尺寸一致，否则手柄位置错位
    private bool _wheelDragging;
    private double _curHue, _curSat = 100;

    /// <summary>生成 HSV 色轮位图：角度=色相，半径=饱和度，中心白。</summary>
    private static System.Windows.Media.Imaging.WriteableBitmap BuildColorWheel(int size)
    {
        var wb = new System.Windows.Media.Imaging.WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        int stride = size * 4;
        var pixels = new byte[stride * size];
        double radius = size / 2.0 - 3;
        double c = size / 2.0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double dx = x - c, dy = y - c;
                double r = Math.Sqrt(dx * dx + dy * dy);
                if (r > radius + 1.5) continue; // 圆外透明
                int i = y * stride + x * 4;
                double sat = Math.Min(1.0, r / radius) * 100;
                double hue = Math.Atan2(dy, dx) * 180 / Math.PI;
                if (hue < 0) hue += 360;
                var col = HsvColor(hue, sat, 255);
                pixels[i] = col.B; pixels[i + 1] = col.G; pixels[i + 2] = col.R;
                pixels[i + 3] = r > radius - 1.5
                    ? (byte)(255 * Math.Clamp((radius + 1.5 - r) / 3.0, 0, 1))
                    : (byte)255;
            }
        }
        wb.WritePixels(new Int32Rect(0, 0, size, size), pixels, stride, 0);
        return wb;
    }

    private void Wheel_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _wheelDragging = true;
        WheelImage.CaptureMouse();
        ApplyWheelPoint(e.GetPosition(WheelImage));
    }

    private void Wheel_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_wheelDragging) return;
        ApplyWheelPoint(e.GetPosition(WheelImage));
    }

    private void Wheel_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _wheelDragging = false;
        WheelImage.ReleaseMouseCapture();
    }

    private void ApplyWheelPoint(Point p)
    {
        double dx = p.X - WheelSize / 2.0, dy = p.Y - WheelSize / 2.0;
        double r = Math.Sqrt(dx * dx + dy * dy);
        double sat = Math.Min(1.0, r / (WheelSize / 2.0 - 3)) * 100;
        double hue = Math.Atan2(dy, dx) * 180 / Math.PI;
        if (hue < 0) hue += 360;
        var color = HsvColor(hue, sat, 255);
        _curHue = hue; _curSat = sat;
        _suppress = true;
        RS.Value = color.R; GS.Value = color.G; BS.Value = color.B;
        _suppress = false;
        UpdatePopupPreview(color);
        UpdateWheelHandle();
        SetColor(color);
    }

    private void UpdateWheelHandle()
    {
        double rad = _curSat / 100.0 * (WheelSize / 2.0 - 3);
        double a = _curHue * Math.PI / 180;
        Canvas.SetLeft(WheelHandle, WheelSize / 2.0 + rad * Math.Cos(a) - 8);   // 手柄 16px，取中心
        Canvas.SetTop(WheelHandle, WheelSize / 2.0 + rad * Math.Sin(a) - 8);
    }

    // ---------- 键盘预览（严格按实拍 G16 24） ----------

    private void BuildKeyboardPreview()
    {
        KeyboardPreview.Children.Clear();
        _previewKeys.Clear();

        const double u = 22.0;          // 单键位宽
        const double key = u - 2.0;     // 键帽净宽
        const double x0 = 6, y0 = 6;

        // 底板：主区 + 导航 + 小键盘 共约 20.6 列；6 主行 + 方向键行
        KeyboardPreview.Children.Add(new Rectangle
        {
            Width = 20.7 * u + 6,
            Height = 7.05 * u + 4,
            RadiusX = 8, RadiusY = 8,
            Fill = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24)),
            Stroke = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)),
        });

        void K(double row, double col, double wUnits, double hUnits, string label, int zone)
        {
            double w = key * wUnits + 2.0 * (wUnits - 1);
            double h = key * hUnits + 2.0 * (hUnits - 1);
            AddPreviewKey(x0 + col * u, y0 + row * u, w, h, label, zone);
        }

        /* 分区（按用户实测）：
           A：到 F5 / 4 / R / F / C / 左Alt 为止
           B：到 PrtSc / + / ] / ' / / / 右Ctrl 为止（含整个导航区与小键盘）
           C：方向键                                                */
        const int A = 0, B = 1, C = 2;

        // 第 0 行：Esc F1-F12 | Insert PrtSc | Delete Home End PgUp PgDn(C)
        K(0, 0, 1, 1, "Esc", A);
        K(0, 1, 1, 1, "F1", A); K(0, 2, 1, 1, "F2", A); K(0, 3, 1, 1, "F3", A);
        K(0, 4, 1, 1, "F4", A); K(0, 5, 1, 1, "F5", A);
        K(0, 6, 1, 1, "F6", B); K(0, 7, 1, 1, "F7", B); K(0, 8, 1, 1, "F8", B);
        K(0, 9, 1, 1, "F9", B); K(0, 10, 1, 1, "F10", B); K(0, 11, 1, 1, "F11", B); K(0, 12, 1, 1, "F12", B);
        K(0, 13, 1, 1, "Ins", B); K(0, 14, 1, 1, "PrtSc", B);
        K(0, 15, 1, 1, "Del", C);
        K(0, 16.5, 1, 1, "Home", C); K(0, 17.5, 1, 1, "End", C);
        K(0, 18.5, 1, 1, "PgUp", C); K(0, 19.5, 1, 1, "PgDn", C);

        // 第 1 行：` 1-0 - = | Backspace | 小键盘首行
        K(1, 0, 1, 1, "`", A);
        K(1, 1, 1, 1, "1", A); K(1, 2, 1, 1, "2", A); K(1, 3, 1, 1, "3", A); K(1, 4, 1, 1, "4", A);
        K(1, 5, 1, 1, "5", B); K(1, 6, 1, 1, "6", B); K(1, 7, 1, 1, "7", B);
        K(1, 8, 1, 1, "8", B); K(1, 9, 1, 1, "9", B); K(1, 10, 1, 1, "0", B);
        K(1, 11, 1, 1, "-", B); K(1, 12, 1, 1, "=", B);
        K(1, 13, 2, 1, "Back", C);
        K(1, 16.5, 1, 1, "NumLk", C); K(1, 17.5, 1, 1, "/", C);
        K(1, 18.5, 1, 1, "*", C); K(1, 19.5, 1, 1, "-", C);

        // 第 2 行：Tab Q-P [ ] | \ | 小键盘 789+
        K(2, 0, 1.5, 1, "Tab", A);
        K(2, 1.5, 1, 1, "Q", A); K(2, 2.5, 1, 1, "W", A);
        K(2, 3.5, 1, 1, "E", A); K(2, 4.5, 1, 1, "R", A);
        K(2, 5.5, 1, 1, "T", B); K(2, 6.5, 1, 1, "Y", B); K(2, 7.5, 1, 1, "U", B);
        K(2, 8.5, 1, 1, "I", B); K(2, 9.5, 1, 1, "O", B); K(2, 10.5, 1, 1, "P", B);
        K(2, 11.5, 1, 1, "[", B); K(2, 12.5, 1, 1, "]", B);
        K(2, 13.5, 1.5, 1, "\\", C);
        K(2, 16.5, 1, 1, "7", C); K(2, 17.5, 1, 1, "8", C);
        K(2, 18.5, 1, 1, "9", C); K(2, 19.5, 1, 2, "+", C);

        // 第 3 行：Caps A-L ; ' | Enter | 小键盘 456
        K(3, 0, 1.75, 1, "Caps", A);
        K(3, 1.75, 1, 1, "A", A); K(3, 2.75, 1, 1, "S", A);
        K(3, 3.75, 1, 1, "D", A); K(3, 4.75, 1, 1, "F", A);
        K(3, 5.75, 1, 1, "G", B); K(3, 6.75, 1, 1, "H", B); K(3, 7.75, 1, 1, "J", B);
        K(3, 8.75, 1, 1, "K", B); K(3, 9.75, 1, 1, "L", B);
        K(3, 10.75, 1, 1, ";", B); K(3, 11.75, 1, 1, "'", B);
        K(3, 12.75, 2.25, 1, "Enter", C);
        K(3, 16.5, 1, 1, "4", C); K(3, 17.5, 1, 1, "5", C); K(3, 18.5, 1, 1, "6", C);

        // 第 4 行：Shift Z-M , . / | 右Shift | 小键盘 123 与竖排 Enter
        K(4, 0, 2.25, 1, "Shift", A);
        K(4, 2.25, 1, 1, "Z", A); K(4, 3.25, 1, 1, "X", A); K(4, 4.25, 1, 1, "C", A);
        K(4, 5.25, 1, 1, "V", B); K(4, 6.25, 1, 1, "B", B); K(4, 7.25, 1, 1, "N", B);
        K(4, 8.25, 1, 1, "M", B); K(4, 9.25, 1, 1, ",", B); K(4, 10.25, 1, 1, ".", B);
        K(4, 11.25, 1, 1, "/", B);
        K(4, 12.25, 2.75, 1, "Shift", C);
        K(4, 16.5, 1, 1, "1", C); K(4, 17.5, 1, 1, "2", C); K(4, 18.5, 1, 1, "3", C);
        K(4, 19.5, 1, 2, "Enter", C);

        // 第 5 行：Ctrl Fn Win Alt Space Alt Fn Ctrl | ↑ | 小键盘 0 .
        K(5, 0, 1.25, 1, "Ctrl", A); K(5, 1.25, 1.25, 1, "Fn", A);
        K(5, 2.5, 1.25, 1, "Win", A); K(5, 3.75, 1.25, 1, "Alt", A);
        K(5, 5, 6.25, 1, "Space", B);
        K(5, 11.25, 1.25, 1, "Alt", B); K(5, 12.5, 1.25, 1, "Ctrl", B);
        K(5, 14.3, 1, 1, "\u2191", C);
        K(5, 16.5, 2, 1, "0", C); K(5, 18.5, 1, 1, ".", C);

        // 第 6 行：方向键 ← ↓ →（C 区）
        K(6, 13.3, 1, 1, "\u2190", C); K(6, 14.3, 1, 1, "\u2193", C); K(6, 15.3, 1, 1, "\u2192", C);
    }

    private void AddPreviewKey(double x, double y, double w, double h, string label, int zone)
    {
        var rect = new Rectangle
        {
            Width = w, Height = h, RadiusX = 3, RadiusY = 3,
            Fill = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Stroke = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x45)),
            StrokeThickness = 0.6,
            Tag = zone,
            IsHitTestVisible = false, // 点击统一由画布就近判定分区
        };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        KeyboardPreview.Children.Add(rect);

        TextBlock tb = null;
        if (!string.IsNullOrEmpty(label))
        {
            tb = new TextBlock
            {
                Text = label,
                FontSize = 8.5,
                Foreground = new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(tb, x + 3.0);
            Canvas.SetTop(tb, y + 3.0);
            KeyboardPreview.Children.Add(tb);
        }
        _previewKeys.Add((rect, tb, zone));
    }

    /// <summary>点击预览任意位置 → 就近判定 A/B/C 分区 → 自动切常亮（防呼吸/流光闪烁影响调色）→ 打开调色盘。</summary>
    private void PreviewRegion_Click(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(KeyboardPreview);
        Rectangle best = null;
        int bestZone = 0;
        double bestDist = double.MaxValue;
        foreach (var (rect, _, zone) in _previewKeys)
        {
            double cx = Canvas.GetLeft(rect) + rect.Width / 2;
            double cy = Canvas.GetTop(rect) + rect.Height / 2;
            double d = (cx - pos.X) * (cx - pos.X) + (cy - pos.Y) * (cy - pos.Y);
            if (d < bestDist) { bestDist = d; best = rect; bestZone = zone; }
        }
        if (best == null) return;

        int editZone = _unified ? -1 : bestZone;
        SwitchToStaticAndOpen(editZone, best);
    }

    private void SwitchToStaticAndOpen(int zone, FrameworkElement anchor)
    {
        void MakeStatic(int i)
        {
            if (LedService.GetMode(_parts[2 + i]) == LedService.ModeStatic) return;
            _parts[2 + i] = LedService.MakeData(LedService.ModeStatic,
                LedService.GetBrightness(_parts[2 + i]), LedService.GetColor(_parts[2 + i]));
            LedService.WriteZone(LedService.KeyboardSlots[i], _parts[2 + i]);
        }

        if (zone < 0)
        {
            for (int i = 0; i < 3; i++) MakeStatic(i);
            LedService.SaveKeyboardToProfile(_editingProfile, _parts);
        }
        else
        {
            MakeStatic(zone);
            LedService.SaveKeyboardToProfile(_editingProfile, _parts);
        }
        LedService.WriteKeyboardZones(_parts);   // 编辑始终实时写键盘（所见即所得）
        RefreshZoneUI();
        OpenColorPopup(zone, anchor);
    }

    /// <summary>自检专用：只改预览配色（写内存数据 + 重绘），不碰 EC、不碰注册表。</summary>
    public void DevPaintPreview(Color a, Color b, Color c)
    {
        _parts[2] = LedService.MakeData(LedService.ModeStatic, 2, a);
        _parts[3] = LedService.MakeData(LedService.ModeStatic, 2, b);
        _parts[4] = LedService.MakeData(LedService.ModeStatic, 2, c);
        _suppress = true;          // 防止下方 UI 同步反向触发写 EC / 存档
        RefreshZoneUI();
        _suppress = false;
        PreviewEffectText.Text = "自检预览：A=蓝 / B=粉 / C=青";
    }

    private void UpdatePreview()
    {
        var colors = new Color[3];
        var modes = new byte[3];
        for (int i = 0; i < 3; i++)
        {
            colors[i] = LedService.GetColor(_parts[2 + i]);
            modes[i] = LedService.GetMode(_parts[2 + i]);
        }

        foreach (var (rect, label, zone) in _previewKeys)
        {
            var mode = modes[zone];
            var color = colors[zone];
            bool lit = mode != 0;
            rect.Fill = lit ? new SolidColorBrush(color) : new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
            rect.Stroke = lit
                ? new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF))
                : new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x45));
            if (label != null)
            {
                // 亮键用深色字、暗键用浅色字，保证字母始终可见
                double lum = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B;
                bool bright = lit && lum > 110;
                label.Foreground = new SolidColorBrush(bright
                    ? Color.FromArgb(0xCC, 0x00, 0x00, 0x00)
                    : Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));
            }
        }
        PreviewEffectText.Text = $"当前编辑档案：{_editingProfile}";
    }

    // ---------- 档案选择 ----------

    private void Chip_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        LoadProfileSelection();
        // 切换档案即把该档案灯效上键盘实时预览（亮度消毒为 NLY 三档）
        LedService.WriteKeyboardZones(_parts);
    }

    private void LoadProfileSelection()
    {
        int chipTag = -1;
        if (ChipPerf.IsChecked == true) chipTag = 0;
        else if (ChipGaming.IsChecked == true) chipTag = 1;
        else if (ChipOffice.IsChecked == true) chipTag = 2;
        else if (ChipCustom.IsChecked == true) chipTag = 3;

        string profile = chipTag switch
        {
            0 => GMReg.ValHighPerf,
            1 => GMReg.ValGame,
            2 => GMReg.ValPowerSaving,
            3 => GMReg.ValCustom,
            _ => LedService.GetCustomOverride() == 1
                ? GMReg.ValCustom   // 自定义接管开启时，"跟随当前模式"=自定义
                : LedService.ProfileForMode(PowerModeService.GetCurrentMode()).Profile,
        };
        _editingProfile = profile;
        ProfileHintText.Text = $"正在编辑档案：{profile}　—　编辑会实时写入键盘并保存，与原版灯光页互通";

        _suppress = true;
        _parts = LedService.LoadProfile(profile);
        _unified = GMReg.Get("UnifiedKBColor", 0) == 1;
        UnifiedToggle.IsChecked = _unified;
        RefreshZoneUI();
        RefreshUnifiedUI();
        RefreshMasterToggle();   // 开关跟随当前选择的档案
        SyncCustomOverrideUI();
        _suppress = false;
    }

    /// <summary>自定义接管开关：仅当选中"自定义"档案时显示在预览右下角。</summary>
    private void SyncCustomOverrideUI()
    {
        CustomOverrideHost.Visibility = ChipCustom.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _suppress = true;
        CustomOverrideToggle.IsChecked = LedService.GetCustomOverride() == 1;
        _suppress = false;
    }

    private void CustomOverride_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppress) return;
        bool on = CustomOverrideToggle.IsChecked == true;
        LedService.SetCustomOverride(on);
        LedService.FxLog("custom override -> " + (on ? "ON" : "OFF"));
        if (on)
            LedService.ApplyProfile(GMReg.ValCustom, 3);   // 立即应用自定义灯效
        else
            LedService.ApplyProfileForMode(PowerModeService.GetCurrentMode());   // 关闭后回到按模式联动
    }

    // ---------- 统一颜色开关（记住开启前的三分区颜色） ----------

    private void Unified_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppress) return;
        bool on = UnifiedToggle.IsChecked == true;
        GMReg.Set("UnifiedKBColor", on ? 1u : 0u);
        _unified = on;
        if (on)
        {
            // 备份三分区当前颜色，然后以 C 区数据为统一基准
            GMReg.SetStr("UnifiedBackup", $"{_parts[2]:x};{_parts[3]:x};{_parts[4]:x}");
            var baseData = _parts[4];
            _parts[2] = baseData;
            _parts[3] = baseData;
            for (int i = 0; i < 3; i++) LedService.WriteZone(LedService.KeyboardSlots[i], baseData);
            LedService.SaveKeyboardToProfile(_editingProfile, _parts);
        }
        else
        {
            // 恢复开启前的三分区设置
            var backup = GMReg.GetStr("UnifiedBackup", "");
            if (!string.IsNullOrWhiteSpace(backup))
            {
                var seg = backup.Split(';');
                for (int i = 0; i < 3; i++)
                {
                    if (i < seg.Length && uint.TryParse(seg[i].Trim(), System.Globalization.NumberStyles.HexNumber, null, out var v))
                        _parts[2 + i] = v;
                    LedService.WriteZone(LedService.KeyboardSlots[i], _parts[2 + i]);
                }
                LedService.SaveKeyboardToProfile(_editingProfile, _parts);
            }
        }
        RefreshUnifiedUI();
        RefreshZoneUI();
    }

    private void RefreshUnifiedUI()
    {
        var c = LedService.GetColor(_parts[4]);
        UnifiedSwatch.Background = new SolidColorBrush(c);
        UnifiedSwatch.Visibility = _unified ? Visibility.Visible : Visibility.Collapsed;
        UnifiedHintText.Text = _unified
            ? "统一颜色已开启：点击右侧色块即可整体调色（分区单独设置已屏蔽）"
            : "开启后三分区使用同一颜色，关闭时恢复开启前各分区的颜色";
        var op = _unified ? 0.35 : 1.0;
        ZoneRowsHost.Opacity = op;
        ZoneRowsHost.IsHitTestVisible = !_unified;
    }

    // ---------- UI 刷新 ----------

    private void RefreshZoneUI()
    {
        for (int i = 0; i < 3; i++)
        {
            var data = _parts[2 + i];
            var swatch = (Button)FindName(i == 0 ? "SwatchA" : i == 1 ? "SwatchB" : "SwatchC");
            var hexText = (TextBlock)FindName(i == 0 ? "HexA" : i == 1 ? "HexB" : "HexC");
            var c = LedService.GetColor(data);
            swatch.Background = new SolidColorBrush(c);
            hexText.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}　{EffectName(LedService.GetMode(data))}　亮度{LedService.BrightnessText(LedService.GetBrightness(data))}";
        }

        var mode = LedService.GetMode(_parts[4]);
        if (mode != LedService.ModeOff)   // 关灯状态（总开关）不当作灯效显示
        {
            _suppress = true;
            var idx = _comboModes.IndexOf(mode);
            if (idx >= 0) EffectCombo.SelectedIndex = idx;
            _suppress = false;
        }

        // NLY 三档亮度
        byte bri = LedService.GetBrightness(_parts[4]);
        _suppress = true;
        if (bri <= 0) BriOff.IsChecked = true;
        else if (bri == 1) BriMid.IsChecked = true;
        else BriFull.IsChecked = true;
        _suppress = false;

        SyncPanelEffectChips(mode);
        UpdatePreview();
    }

    private static string EffectName(byte mode)
    {
        foreach (var (m, n) in LedService.EffectList) if (m == mode) return n;
        return "未知";
    }

    // ---------- 应用（写 EC + 存档案） ----------

    private void ApplyZone(int zone)
    {
        if (_unified)
        {
            var unified = _parts[4];
            _parts[2] = unified;
            _parts[3] = unified;
            for (int i = 0; i < 3; i++) LedService.WriteZone(LedService.KeyboardSlots[i], unified);
            LedService.SaveKeyboardToProfile(_editingProfile, _parts);
            return;
        }
        var data = _parts[2 + zone];
        LedService.WriteZone(LedService.KeyboardSlots[zone], data);   // 编辑始终实时写键盘（所见即所得）
        LedService.SaveKeyboardToProfile(_editingProfile, _parts);
        LedService.FxLog("ApplyZone " + zone + " data=" + data.ToString("X"));
    }

    private void ApplyEffect(byte mode)
    {
        LedService.FxLog("ApplyEffect mode=" + mode);
        for (int i = 0; i < 3; i++)
        {
            _parts[2 + i] = LedService.MakeData(mode, LedService.GetBrightness(_parts[2 + i]), LedService.GetColor(_parts[2 + i]));
            LedService.WriteZone(LedService.KeyboardSlots[i], _parts[2 + i]);
        }
        LedService.SaveKeyboardToProfile(_editingProfile, _parts);
        RefreshZoneUI();
    }

    private void ApplyBrightness(byte brightness)
    {
        // NLY 机型安全钳位：只允许 0/1/2，越界值会导致 EC 熄灯
        if (brightness > 2) brightness = 2;
        for (int i = 0; i < 3; i++)
        {
            _parts[2 + i] = LedService.MakeData(LedService.GetMode(_parts[2 + i]), brightness, LedService.GetColor(_parts[2 + i]));
            LedService.WriteZone(LedService.KeyboardSlots[i], _parts[2 + i]);
        }
        LedService.SaveKeyboardToProfile(_editingProfile, _parts);
        GMReg.Set(GMReg.ValAlpha, brightness);
    }

    // ---------- 事件 ----------

    private void Effect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppress || EffectCombo.SelectedIndex < 0) return;
        ApplyEffect(_comboModes[EffectCombo.SelectedIndex]);
    }

    private void BriLevel_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppress) return;
        ApplyBrightness((byte)int.Parse((string)((RadioButton)sender).Tag));
    }

    private void MasterToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppress) return;
        bool on = MasterToggle.IsChecked == true;
        _isOn = on;
        LedService.SetProfileOn(_editingProfile, on);
        LedService.FxLog("toggle " + _editingProfile + " -> " + (on ? "ON" : "OFF"));
        if (on)
        {
            // 开 = 应用该档案下方配置的灯效
            LedService.WriteKeyboardZones(_parts);
            RefreshZoneUI();
        }
        else
        {
            // 关 = 键盘熄灭；该档案配置保留，下方仍可继续编辑（编辑仍实时写键盘）
            for (int i = 0; i < 3; i++)
                LedService.WriteZone(LedService.KeyboardSlots[i], (uint)(_parts[2 + i] & 0x00FFFFFF));
        }
    }

    // ---------- 调色盘 Popup ----------

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        var btn = (Button)sender;
        OpenColorPopup(int.Parse((string)btn.Tag), btn);
    }

    private Window _hostWindow;

    /// <summary>关闭调色盘。</summary>
    private void ClosePopup()
    {
        ColorPopup.IsOpen = false;
    }

    private void Host_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ColorPopup.IsOpen)
        {
            ClosePopup();
            e.Handled = true;
        }
    }

    private void ColorPopupClose_Click(object sender, RoutedEventArgs e)
    {
        ClosePopup();
    }

    /// <summary>自检用：切到自定义档案（触发接管开关显示）。</summary>
    public void DevSelectCustomChip()
    {
        ChipCustom.IsChecked = true;
    }

    /// <summary>自检用：直接打开调色面板。</summary>
    public void DevOpenPanel()
    {
        OpenColorPopup(0, KeyboardPreview);
    }

    private void PanelEffect_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppress) return;
        var mode = (byte)int.Parse((string)((RadioButton)sender).Tag);
        if (mode == LedService.ModeOff) return;   // 关闭不是灯效
        ApplyEffect(mode);
    }

    /// <summary>把面板里的灯效胶囊同步到当前档案的灯效。</summary>
    private void SyncPanelEffectChips(byte mode)
    {
        _suppress = true;
        PnlStatic.IsChecked = mode == 1;
        PnlBreath.IsChecked = mode == 3;
        PnlCycle.IsChecked = mode == 6;
        PnlAmbi.IsChecked = mode == 7;
        _suppress = false;
    }

    private System.Windows.Threading.DispatcherTimer _openTimer;

    private void OpenTimer_Tick(object sender, EventArgs e)
    {
        _openTimer.Stop();
        _openTimer.Tick -= OpenTimer_Tick;
        ColorPopup.IsOpen = true;
    }

    private void OpenColorPopup(int zone, FrameworkElement anchor)
    {
        _activeZone = zone;

        var data = _activeZone < 0 ? _parts[4] : _parts[2 + _activeZone];
        var c = LedService.GetColor(data);
        _suppress = true;
        RS.Value = c.R; GS.Value = c.G; BS.Value = c.B;
        SyncHsvFromRgb(c);
        _suppress = false;
        UpdatePopupPreview(c);
        ColorPopup.PlacementTarget = anchor;
        // 关键：StaysOpen=False 的弹窗会把“开窗前那次点击”的 MouseUp 判定为点外部而立刻关闭。
        // Input 优先级的延迟不够（仍可能插在 Down 与 Up 之间），必须用真实时间定时器
        // 等整段点击序列（Down→Up）彻底结束后再打开。
        _openTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _openTimer.Stop();
        _openTimer.Tick += OpenTimer_Tick;
        _openTimer.Start();
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        var color = (Color)ColorConverter.ConvertFromString((string)((Button)sender).Tag);
        SetColor(color);
        _suppress = true;
        RS.Value = color.R; GS.Value = color.G; BS.Value = color.B;
        SyncHsvFromRgb(color);
        _suppress = false;
        UpdatePopupPreview(color);
    }

    private void RgbSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ColorPopup.IsOpen || _suppress) return;
        var c = Color.FromRgb((byte)RS.Value, (byte)GS.Value, (byte)BS.Value);
        SyncHsvFromRgb(c);
        UpdatePopupPreview(c);
        SetColor(c);
    }

    private void SyncHsvFromRgb(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        double hue = 0;
        if (delta > 0)
        {
            if (max == r) hue = 60 * (((g - b) / delta) % 6);
            else if (max == g) hue = 60 * ((b - r) / delta + 2);
            else hue = 60 * ((r - g) / delta + 4);
        }
        if (hue < 0) hue += 360;
        double sat = max <= 0 ? 0 : delta / max * 100;
        _curHue = hue;
        _curSat = sat;
        UpdateWheelHandle();
    }

    private void SetColor(Color c)
    {
        if (_activeZone < 0)
        {
            // 统一颜色：三分区同色
            var bri = LedService.GetBrightness(_parts[4]);
            var mode = LedService.GetMode(_parts[4]);
            var data = LedService.MakeData(mode, bri, c);
            _parts[2] = data; _parts[3] = data; _parts[4] = data;
            for (int i = 0; i < 3; i++) LedService.WriteZone(LedService.KeyboardSlots[i], data);
            LedService.SaveKeyboardToProfile(_editingProfile, _parts);
            RefreshZoneUI();
            RefreshUnifiedUI();
        }
        else
        {
            _parts[2 + _activeZone] = LedService.MakeData(
                LedService.GetMode(_parts[2 + _activeZone]),
                LedService.GetBrightness(_parts[2 + _activeZone]), c);
            ApplyZone(_activeZone);
            RefreshZoneUI();
        }
    }

    private void UpdatePopupPreview(Color c)
    {
        PopupPreview.Fill = new SolidColorBrush(c);
        PopupHexText.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        RV.Text = ((int)RS.Value).ToString();
        GV.Text = ((int)GS.Value).ToString();
        BV.Text = ((int)BS.Value).ToString();
    }

    // ---------- Hex 输入 ----------

    private void BtnApplyHex_Click(object sender, RoutedEventArgs e) => ApplyHexInput();

    private void HexInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyHexInput();
    }

    private void HexInput_LostFocus(object sender, RoutedEventArgs e) { }

    private void ApplyHexInput()
    {
        var text = HexInput.Text.Trim().TrimStart('#');
        if (text.Length == 6 && System.Text.RegularExpressions.Regex.IsMatch(text, "^[0-9a-fA-F]{6}$"))
        {
            var color = Color.FromRgb(
                Convert.ToByte(text.Substring(0, 2), 16),
                Convert.ToByte(text.Substring(2, 2), 16),
                Convert.ToByte(text.Substring(4, 2), 16));
            SetColor(color);
            _suppress = true;
            RS.Value = color.R; GS.Value = color.G; BS.Value = color.B;
            SyncHsvFromRgb(color);
            _suppress = false;
            UpdatePopupPreview(color);
        }
        else
        {
            HexInput.Text = "";
        }
    }

    /// <summary>系统模式变化（任意来源）：刷新本模式开关；编辑目标为"跟随当前模式"时同时重载档案。</summary>
    private void OnModeChangedForPage()
    {
        if (!IsLoaded) return;
        if (ChipFollow.IsChecked == true) LoadProfileSelection();
        RefreshMasterToggle();
    }

    private void RefreshMasterToggle()
    {
        _suppress = true;
        // 开关跟随当前选择的灯效档案（点档案胶囊即切换显示对应开关状态）
        _isOn = LedService.GetProfileOn(_editingProfile) == 1;
        MasterToggle.IsChecked = _isOn;
        _suppress = false;
    }
}
