using System;
using System.Windows;

namespace CCLite;

public partial class SplashWindow : Window
{
    private System.Windows.Threading.DispatcherTimer _pTimer;
    private double _value;

    public SplashWindow()
    {
        InitializeComponent();
        // 假进度：先缓爬到 99，真正就绪时由 Finish() 推到 100
        _pTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _pTimer.Tick += (_, _) =>
        {
            _value = Math.Min(99, _value + (99 - _value) * 0.08 + 0.6);
            Progress.Value = _value;
        };
        _pTimer.Start();
    }

    public void SetStatus(string text)
    {
        StatusText.Text = text;
    }

    /// <summary>就绪：推到 100 并停住，随即由调用方关闭。</summary>
    public void Finish()
    {
        _pTimer?.Stop();
        Progress.Value = 100;
        StatusText.Text = "启动完成";
    }
}
