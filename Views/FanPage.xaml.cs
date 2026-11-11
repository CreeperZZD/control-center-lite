using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CCLite.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace CCLite.Views;

public partial class FanPage : UserControl
{
    private int[] _cpu = new int[8];
    private int[] _gpu = new int[8];
    private bool _suppress;

    public FanPage()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            _suppress = true;
            PresetCombo.SelectedIndex = (int)Math.Clamp(GMReg.Get(GMReg.ValFanControlSelect, 0), 0, 2);
            LoadCurves();
            CustomToggle.IsChecked = GMReg.Get(GMReg.ValFanControlStatus, 0) == 1;
            _suppress = false;
            App.Monitor.SnapshotUpdated += OnSnapshot;
        };
        Unloaded += (_, _) => App.Monitor.SnapshotUpdated -= OnSnapshot;
        CurveCanvas.SizeChanged += (_, _) => DrawCurves();
    }

    private void LoadCurves()
    {
        (_cpu, _gpu) = App.Fan.LoadCurve(PresetCombo.SelectedIndex);
        BuildSliders();
        DrawCurves();
    }

    private void BuildSliders()
    {
        BuildSliderHost(CpuSliderHost, _cpu);
        BuildSliderHost(GpuSliderHost, _gpu);
    }

    private void BuildSliderHost(StackPanel host, int[] curve)
    {
        host.Children.Clear();
        for (int i = 0; i < FanService.TempPoints.Length; i++)
        {
            int idx = i;
            var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });

            var lbl = new TextBlock { Text = $"{FanService.TempPoints[i]}°C", VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Foreground = (Brush)FindResource("TextSecondaryBrush") };
            var slider = new Slider
            {
                Minimum = 0, Maximum = 100, Value = curve[i], TickFrequency = 5,
                IsSnapToTickEnabled = true, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)FindResource("ModernSlider")
            };
            var val = new TextBlock { Text = $"{curve[i]}%", VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Foreground = (Brush)FindResource("TextBrush"), HorizontalAlignment = HorizontalAlignment.Right };

            slider.ValueChanged += (_, _) =>
            {
                curve[idx] = (int)slider.Value;
                val.Text = $"{curve[idx]}%";
                if (_suppress) return;
                App.Fan.SaveCurve(PresetCombo.SelectedIndex, _cpu, _gpu);
                DrawCurves();
                if (App.Fan.IsEnabled) App.Fan.SetSpeeds(_cpu[PercentIndexForCpu()], _gpu[PercentIndexForGpu()], 0xFF);
            };

            Grid.SetColumn(lbl, 0); Grid.SetColumn(slider, 1); Grid.SetColumn(val, 2);
            row.Children.Add(lbl); row.Children.Add(slider); row.Children.Add(val);
            host.Children.Add(row);
        }
    }

    private int PercentIndexForCpu() => IndexForTemp(CurrentCpuTemp);
    private int PercentIndexForGpu() => IndexForTemp(CurrentGpuTemp);

    private static int IndexForTemp(int temp)
    {
        for (int m = 0; m < FanService.TempPoints.Length; m++)
            if (temp >= FanService.TempPoints[m] && temp - FanService.TempPoints[m] < 10) return m;
        return temp >= FanService.TempPoints[7] ? 7 : 0;
    }

    private int CurrentCpuTemp, CurrentGpuTemp;

    private void OnSnapshot(MonitorSnapshot s)
    {
        Dispatcher.Invoke(() =>
        {
            if (!s.Ok) return;
            CurrentCpuTemp = s.CpuTemp;
            CurrentGpuTemp = s.GpuTemp;
            LiveCpuText.Text = $"CPU: {s.CpuTemp}°C / {s.CpuRpm} RPM";
            LiveGpuText.Text = $"GPU: {s.GpuTemp}°C / {s.GpuRpm} RPM";
        });
    }

    // ---------- 曲线绘制 ----------

    private void DrawCurves()
    {
        CurveCanvas.Children.Clear();
        double w = CurveCanvas.ActualWidth, h = CurveCanvas.ActualHeight;
        if (w < 40 || h < 40) return;
        double padL = 34, padB = 22, padT = 8, padR = 8;
        double plotW = w - padL - padR, plotH = h - padT - padB;

        // 网格与坐标
        for (int t = 30; t <= 100; t += 10)
        {
            double x = padL + (t - 25) / 80.0 * plotW;
            CurveCanvas.Children.Add(new Line
            {
                X1 = x, Y1 = padT, X2 = x, Y2 = h - padB,
                Stroke = (Brush)FindResource("DividerBrush"), StrokeThickness = 1
            });
            var lbl = new TextBlock { Text = $"{t}", FontSize = 10, Foreground = (Brush)FindResource("TextSecondaryBrush") };
            Canvas.SetLeft(lbl, x - 8); Canvas.SetTop(lbl, h - padB + 4);
            CurveCanvas.Children.Add(lbl);
        }
        for (int p = 0; p <= 100; p += 25)
        {
            double y = padT + (1 - p / 100.0) * plotH;
            CurveCanvas.Children.Add(new Line
            {
                X1 = padL, Y1 = y, X2 = w - padR, Y2 = y,
                Stroke = (Brush)FindResource("DividerBrush"), StrokeThickness = 1
            });
            var lbl = new TextBlock { Text = $"{p}%", FontSize = 10, Foreground = (Brush)FindResource("TextSecondaryBrush") };
            Canvas.SetLeft(lbl, 4); Canvas.SetTop(lbl, y - 7);
            CurveCanvas.Children.Add(lbl);
        }

        DrawOneCurve(_cpu, (Color)ColorConverter.ConvertFromString("#4CC2FF"), padL, padT, plotW, plotH);
        DrawOneCurve(_gpu, (Color)ColorConverter.ConvertFromString("#FFA54C"), padL, padT, plotW, plotH);
    }

    private void DrawOneCurve(int[] curve, Color color, double padL, double padT, double plotW, double plotH)
    {
        Point Pos(int i) => new(
            padL + (FanService.TempPoints[i] - 25) / 80.0 * plotW,
            padT + (1 - curve[i] / 100.0) * plotH);

        var seg = new PolyLineSegment();
        var fig = new PathFigure(Pos(0), new PathSegment[] { seg }, false);
        for (int i = 1; i < curve.Length; i++) seg.Points.Add(Pos(i));
        var geo = new PathGeometry(new[] { fig });
        CurveCanvas.Children.Add(new Path
        {
            Data = geo,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round
        });

        for (int i = 0; i < curve.Length; i++)
        {
            var p = Pos(i);
            CurveCanvas.Children.Add(new Ellipse
            {
                Width = 6, Height = 6, Fill = new SolidColorBrush(color),
                Margin = new Thickness(p.X - 3, p.Y - 3, 0, 0)
            });
        }
    }

    // ---------- 控制事件 ----------

    private void CustomToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppress) return;
        bool on = CustomToggle.IsChecked == true;
        App.Fan.SaveCurve(PresetCombo.SelectedIndex, _cpu, _gpu);
        App.Fan.SetPreset(PresetCombo.SelectedIndex);
        App.Fan.Enable(on);
    }

    private void Preset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppress) return;
        App.Fan.SetPreset(PresetCombo.SelectedIndex);
        _suppress = true;
        LoadCurves();
        _suppress = false;
    }

    private void RestoreAuto_Click(object sender, RoutedEventArgs e)
    {
        _suppress = true;
        CustomToggle.IsChecked = false;
        _suppress = false;
        App.Fan.Enable(false);
    }
}
