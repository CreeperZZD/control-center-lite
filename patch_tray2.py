import io

# csproj: tray.ico 打包为 Resource
p = 'CCLite.csproj'
s = io.open(p, encoding='utf-8').read()
if 'tray.ico' not in s:
    s = s.replace('    <Resource Include="assets\\app.ico" />',
                  '    <Resource Include="assets\\app.ico" />\n    <Resource Include="assets\\tray.ico" />')
    io.open(p, 'w', encoding='utf-8').write(s)
print('csproj resource added')

p = 'App.xaml.cs'
s = io.open(p, encoding='utf-8').read()

# 1) 托盘图标用 tray.ico
old = '''        try
        {
            var sri = GetResourceStream(new Uri("pack://application:,,,/assets/app.ico"));
            _tray.Icon = new System.Drawing.Icon(sri.Stream);
        }'''
new = '''        try
        {
            var sri = GetResourceStream(new Uri("pack://application:,,,/assets/tray.ico"));
            _tray.Icon = new System.Drawing.Icon(sri.Stream);
        }'''
assert old in s
s = s.replace(old, new, 1)

# 2) 菜单预建（首次右键不卡）：打开时只刷新勾选状态
old = '''        var menu = new System.Windows.Forms.ContextMenuStrip();
        _tray.ContextMenuStrip = menu;
        menu.Opening += (_, _) => RebuildTrayMenu(menu);   // 每次展开前刷新勾选状态
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowMain();
        };
        _tray.DoubleClick += (_, _) => ShowMain();'''
new = '''        _menu = new System.Windows.Forms.ContextMenuStrip();
        BuildTrayMenu(_menu);                       // 启动即建好全部菜单项（首次右键零延迟）
        _tray.ContextMenuStrip = _menu;
        _menu.Opening += (_, _) => RefreshTrayMenuStates();   // 展开前只刷新勾选状态（毫秒级）
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowMain();
        };
        _tray.DoubleClick += (_, _) => ShowMain();'''
assert old in s
s = s.replace(old, new, 1)

# 3) 字段
s = s.replace('''    private System.Windows.Forms.NotifyIcon _tray;''',
'''    private System.Windows.Forms.NotifyIcon _tray;
    private System.Windows.Forms.ContextMenuStrip _menu;''', 1)

# 4) RebuildTrayMenu 改造
old = '''    private void RebuildTrayMenu(System.Windows.Forms.ContextMenuStrip menu)
    {
        menu.Items.Clear();

        // 系统模式
        var mode = PowerModeService.GetCurrentMode();
        var modeItem = new System.Windows.Forms.ToolStripMenuItem("系统模式");
        AddRadioMenuItem(modeItem, "办公模式", mode == PowerModeService.ModeOffice, () => SwitchModeSafe(PowerModeService.ModeOffice));
        AddRadioMenuItem(modeItem, "游戏模式", mode == PowerModeService.ModeGaming, () => SwitchModeSafe(PowerModeService.ModeGaming));
        AddRadioMenuItem(modeItem, "狂暴模式", mode == PowerModeService.ModePerformance, () => SwitchModeSafe(PowerModeService.ModePerformance));
        menu.Items.Add(modeItem);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        // 键盘亮度三档
        var bri = CurrentBriLevelIndex();
        var briItem = new System.Windows.Forms.ToolStripMenuItem("键盘亮度");
        AddRadioMenuItem(briItem, "熄灭", bri == 0, () => TraySetBrightness(0));
        AddRadioMenuItem(briItem, "50%", bri == 1, () => TraySetBrightness(1));
        AddRadioMenuItem(briItem, "100%", bri == 2, () => TraySetBrightness(2));
        menu.Items.Add(briItem);

        // 灯光总开关
        var ledOn = GMReg.Get(GMReg.ValLedWizard, 1) == 1;
        AddCheckMenuItem(menu, "灯光总开关", ledOn, () => TrayToggleMaster(!ledOn));

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        // Win 键锁定
        bool winLocked = GMReg.Get(GMReg.ValWinKey, 0) == 1;
        AddCheckMenuItem(menu, "Win 键锁定", winLocked, () =>
        {
            if (!DisplayService.SetWinKeyLock(!winLocked))
                TrayToast("设置失败：EC 通信异常");
        });

        // 开机自启
        bool auto = AutoStart.IsEnabled();
        AddCheckMenuItem(menu, "开机自启", auto, () =>
        {
            if (!AutoStart.Set(!auto)) TrayToast("设置开机自启失败");
        });

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("把图标固定到任务栏", null, (_, _) => PinTrayIconToTaskbar());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("打开 面板", null, (_, _) => ShowMain());
        menu.Items.Add("退出", null, (_, _) => ReallyExit());
    }'''
new = '''    private System.Windows.Forms.ToolStripMenuItem _miModeOffice, _miModeGaming, _miModePerf;
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
    }'''
assert old in s
s = s.replace(old, new, 1)

# 5) AddRadioMenuItem 简化
old = '''    private static void AddRadioMenuItem(System.Windows.Forms.ToolStripMenuItem parent, string text, bool checkd, Action onClick)
    {
        var it = new System.Windows.Forms.ToolStripMenuItem(text) { Checked = checkd };
        it.Click += (_, _) => onClick();
        parent.DropDownItems.Add(it);
    }'''
new = '''    private static System.Windows.Forms.ToolStripMenuItem AddRadioMenuItem(System.Windows.Forms.ToolStripMenuItem parent, string text, Action onClick)
    {
        var it = new System.Windows.Forms.ToolStripMenuItem(text);
        it.Click += (_, _) => onClick();
        parent.DropDownItems.Add(it);
        return it;
    }'''
assert old in s
s = s.replace(old, new, 1)

# 6) 删除 AddCheckMenuItem
old = '''    private static void AddCheckMenuItem(System.Windows.Forms.ContextMenuStrip menu, string text, bool checkd, Action onClick)
    {
        var it = new System.Windows.Forms.ToolStripMenuItem(text) { Checked = checkd };
        it.Click += (_, _) => onClick();
        menu.Items.Add(it);
    }

'''
s = s.replace(old, '', 1)

# 7) 启动刷新自启缓存 + 托盘切换后刷新
s = s.replace('''        Monitor.Start();
        Fan.RestoreFromRegistry();''',
'''        Monitor.Start();
        Fan.RestoreFromRegistry();
        RefreshAutoStartCache();   // 后台缓存开机自启状态（托盘菜单直接用缓存）''', 1)

s = s.replace('''        LedService.ApplyProfile(profile, moduleId);   // 开=应用档案 关=熄灭
    }''',
'''        LedService.ApplyProfile(profile, moduleId);   // 开=应用档案 关=熄灭
        System.Threading.Tasks.Task.Run(() => _autoStartCached = AutoStart.IsEnabled());
    }''', 1)

io.open(p, 'w', encoding='utf-8').write(s)
print('tray menu prebuilt')
