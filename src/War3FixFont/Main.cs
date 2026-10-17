using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Forms;
using War3FixFont.WinAPI;
using Timer = System.Timers.Timer;

namespace War3FixFont;

public partial class Main : Form
{
    private readonly KeyboardHook _hook = new();

    // 在消息循环中执行，退出和重设间隔不会与后台线程上的修复回调竞争。
    private readonly System.Windows.Forms.Timer _timer = new();

    private const string Url = "https://github.com/Zonciu/War3FixFont";

    /// <summary>
    /// 修复频率限制
    /// </summary>
    private readonly long _fixThresholdTicks;

    /// <summary>
    /// 最后一次修复时间刻
    /// </summary>
    private long _lastFixTicks;

    private const long ToggleWindowThresholdTicks = 1 * 100 * 10000;

    private long _lastToggleWindowTicks;

    private const int UNLOCKED = 0;

    private const int LOCKED = 1;

    /// <summary>
    /// 修复锁
    /// </summary>
    private int _locker = UNLOCKED;

    /// <summary>
    /// 窗口事件钩子
    /// </summary>
    private IntPtr _winEventHook;

    /// <summary>
    /// 魔兽3进程扫描
    /// </summary>
    private readonly Timer _war3RunningMonitor = new();

    private const int FixHotKeyId = 1;

    private int _fixHotKeyId;

    private const int ShowMeHotKeyId = 2;

    private int _showMeHotKeyId;

    public readonly SettingsManager SettingsManager = new();

    public Settings Settings => SettingsManager.Settings;

    public readonly Fixer Fixer;

    private readonly bool _inited;

    private bool _allowShow;

    private bool _exitRequested;

    private volatile bool _shuttingDown;

    private static API.WinEventProc _lockCursorCallback;

    public Main()
    {
        SettingsManager.Load();
        Fixer = new() { Settings = SettingsManager.Settings };
        _fixThresholdTicks = Settings.FixThreshold * 1000 * 10000;

        InitializeComponent();

        VersionLabel.Text = @$"v{Application.ProductVersion}";

        _hook.KeyPressed += HotKeyEvent;
        _timer.Tick += TimerFix;

        // 读取定时配置
        var interval = Settings.TimerInterval;
        IntervalInput.Value = Math.Max((int)IntervalInput.Minimum, Math.Min(interval, (int)IntervalInput.Maximum));
        Settings.TimerInterval = (int)IntervalInput.Value;
        EnableTimerFixCheckBox.Checked = Settings.UseTimer;

        // 读取窗口配置
        WindowModeComboBox.DisplayMember = "Name";
        WindowModeComboBox.ValueMember = "Value";
        var windowModeSource = Enum.GetValues(typeof(WindowMode))
                                   .Cast<WindowMode>()
                                   .Select(
                                        value => new ComboBoxItem<WindowMode>
                                        {
                                            Name = (Attribute.GetCustomAttribute(value.GetType().GetField(value.ToString()), typeof(DescriptionAttribute)) as DescriptionAttribute)!.Description,
                                            Value = value
                                        })
                                   .OrderBy(item => item.Value)
                                   .ToList();
        WindowModeComboBox.SelectedIndexChanged -= WindowModeComboBox_SelectedIndexChanged;
        WindowModeComboBox.DataSource = windowModeSource;
        var mode = windowModeSource.Single(e => e.Value == Settings.WindowMode);
        WindowModeComboBox.SelectedItem = mode;
        WindowModeComboBox.SelectedIndexChanged += WindowModeComboBox_SelectedIndexChanged;

        // 读取修复快捷键配置
        var hotKey = Settings.Hotkey;
        if (hotKey.IsValid)
        {
            HotKeyInputBox.Hotkey = hotKey;
        }
        else
        {
            Settings.Hotkey = Hotkey.DefaultFixHotkey;
            HotKeyInputBox.Hotkey = Settings.Hotkey;
        }

        HotKeyInputBox.HotKeyEditing += (_, _) =>
        {
            if (_fixHotKeyId != 0)
            {
                _hook.UnregisterHotKey(_fixHotKeyId);
                _fixHotKeyId = 0;
            }
        };
        EnableHotKeyCheckBox.Checked = Settings.UseHotKey;
        UpdateFixHotKey();

        // 读取窗口模式
        var useCustomMode = Settings.WindowMode == WindowMode.Custom;
        WidthInput.Enabled = useCustomMode;
        WidthInput.Value = Settings.Width;
        HeightInput.Enabled = useCustomMode;
        HeightInput.Value = Settings.Height;
        CustomPositionCheckBox.Enabled = useCustomMode;
        CustomPositionCheckBox.Checked = Settings.UseCustomPosition;
        CustomPositionX.Enabled = useCustomMode && CustomPositionCheckBox.Checked;
        CustomPositionY.Enabled = useCustomMode && CustomPositionCheckBox.Checked;

        // 读取显示窗口快捷键配置
        if (Settings.ShowMeHotkey.IsValid)
        {
            ShowMeHotKeyInputBox.Hotkey = Settings.ShowMeHotkey;
        }
        else
        {
            Settings.ShowMeHotkey = Hotkey.DefaultShowMeHotkey;
            ShowMeHotKeyInputBox.Hotkey = Settings.ShowMeHotkey;
        }

        UpdateShowMeHotKey();

        // 窗口切换事件
        LockCursorCheckBox.Checked = Settings.LockCursor;
        _lockCursorCallback = LockCursorEventProc;
        if (Settings.LockCursor)
        {
            LockCursor();
        }

        // 扫描魔兽3进程
        AutoApplyCheckBox.Checked = Settings.AutoApplyWindow;
        _war3RunningMonitor.Interval = 2000;
        _war3RunningMonitor.Elapsed += CheckWar3Process;
        if (Settings.AutoApplyWindow)
        {
            _war3RunningMonitor.Start();
        }

        SettingsManager.Save();
        _inited = true;
        ShowInTaskbar = false;
        UpdateTimer();
        if (Settings.UseTimer)
        {
            FixFont(false);
        }
    }

    /// <summary>
    /// 启动时只创建隐藏句柄，避免主窗口闪现；托盘仍可随时显示设置。
    /// </summary>
    protected override void SetVisibleCore(bool value)
    {
        if (value && !_allowShow)
        {
            if (!IsHandleCreated)
            {
                CreateHandle();
            }

            value = false;
        }

        base.SetVisibleCore(value);
    }

    /// <summary>
    /// 关闭设置窗口继续后台运行；托盘退出和系统关机才结束进程。
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !_exitRequested)
        {
            e.Cancel = true;
            HideWindow();
            base.OnFormClosing(e);
            return;
        }

        base.OnFormClosing(e);
        if (e.Cancel)
        {
            return;
        }

        _shuttingDown = true;
        _timer.Stop();
        _timer.Dispose();
        _war3RunningMonitor.Stop();
        _war3RunningMonitor.Dispose();
        NotifyIcon.Visible = false;
        UnlockCursor();
        SettingsManager.Save();
        _hook.Dispose();
    }

    /// <summary>
    /// 快捷键事件
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="args"></param>
    private void HotKeyEvent(object sender, KeyPressedEventArgs args)
    {
        if (Settings.Hotkey.SameAs(args))
        {
            HotKeyFix();
        }
        else if (Settings.ShowMeHotkey.SameAs(args))
        {
            ToggleWindow();
        }
    }

    /// <summary>
    /// 快捷键修复
    /// </summary>
    private void HotKeyFix()
    {
        if (!EnableHotKeyCheckBox.Checked)
        {
            return;
        }

        if (HotKeyInputBox.IsEditing)
        {
            return;
        }

        FixFont(true);
    }

    /// <summary>
    /// 手动修复
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="args"></param>
    private void FixButton_Click(object sender, EventArgs args)
    {
        FixFont(true);
    }

    /// <summary>
    /// 定时修复
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="args"></param>
    private void TimerFix(object sender, EventArgs args)
    {
        FixFont(false);
    }

    /// <summary>
    /// 重置修复定时器
    /// </summary>
    private void ResetTimer()
    {
        if (!_shuttingDown && Settings.UseTimer)
        {
            _timer.Stop();
            _timer.Start();
        }
    }

    /// <summary>
    /// 修复叠字
    /// </summary>
    private void FixFont(bool resetTimer)
    {
        if (_shuttingDown)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _locker, LOCKED, UNLOCKED) != UNLOCKED)
        {
            return;
        }

        try
        {
            var nowTicks = DateTime.Now.Ticks;
            if (nowTicks - _lastFixTicks >= _fixThresholdTicks)
            {
                _lastFixTicks = nowTicks;
                Fixer.Fix();
            }
        }
        catch (Exception e)
        {
            ShowNotification($@"叠字修复时异常: {e.Message}");
        }
        finally
        {
            if (resetTimer)
            {
                ResetTimer();
            }

            Interlocked.Exchange(ref _locker, UNLOCKED);
        }
    }

    /// <summary>
    /// 更新快捷键
    /// </summary>
    private void UpdateFixHotKey()
    {
        if (_fixHotKeyId != 0)
        {
            _hook.UnregisterHotKey(_fixHotKeyId);
            _fixHotKeyId = 0;
        }

        if (EnableHotKeyCheckBox.Checked && HotKeyInputBox.Hotkey.IsValid)
        {
            var id = _hook.RegisterHotKey(HotKeyInputBox.Hotkey.Modifier, HotKeyInputBox.Hotkey.KeyCode, FixHotKeyId);
            if (id.HasValue)
            {
                _fixHotKeyId = id.Value;
                ActiveControl = null;
            }
            else
            {
                ShowNotification("注册修复快捷键失败，定时修复仍可使用。请从托盘打开设置更换快捷键。");
            }
        }
    }

    /// <summary>
    /// 点击仓库链接
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void LinkLabel_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
    {
        var ps = new ProcessStartInfo(Url)
        {
            UseShellExecute = true,
            Verb = "open"
        };
        Process.Start(ps);
    }

    /// <summary>
    /// 更新定时间隔
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void IntervalInput_ValueChanged(object sender, EventArgs e)
    {
        if (!_inited || Settings.TimerInterval == (int)IntervalInput.Value)
        {
            return;
        }

        Settings.TimerInterval = (int)IntervalInput.Value;
        SettingsManager.Save();
        UpdateTimer();
    }

    /// <summary>
    /// 更改定时器启用状态
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void EnableTimerFixCheckBox_CheckedChanged(object sender, EventArgs e)
    {
        if (!_inited || Settings.UseTimer == EnableTimerFixCheckBox.Checked)
        {
            return;
        }

        Settings.UseTimer = EnableTimerFixCheckBox.Checked;
        SettingsManager.Save();
        UpdateTimer();
    }

    private void UpdateTimer()
    {
        _timer.Stop();
        _timer.Interval = Settings.TimerInterval * 1000;
        _timer.Enabled = Settings.UseTimer;
        NotifyIcon.Text = Settings.UseTimer ? "魔兽3叠字修复 - 后台定时修复中" : "魔兽3叠字修复 - 定时修复已暂停";
    }

    private void ShowNotification(string message)
    {
        if (!_shuttingDown)
        {
            NotifyIcon.ShowBalloonTip(5000, "魔兽3叠字修复", message, ToolTipIcon.Warning);
        }
    }

    /// <summary>
    /// 更改修复快捷键启用状态
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void EnableHotKeyCheckBox_CheckedChanged(object sender, EventArgs e)
    {
        if (!_inited || Settings.UseHotKey == EnableHotKeyCheckBox.Checked)
        {
            return;
        }

        Settings.UseHotKey = EnableHotKeyCheckBox.Checked;
        SettingsManager.Save();
        UpdateFixHotKey();
    }

    /// <summary>
    /// 输入修复快捷键
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void HotKeyInputBox_HotKeyChanged(object sender, EventArgs e)
    {
        var hotkey = HotKeyInputBox.Hotkey;
        hotkey = hotkey.IsValid ? hotkey : Hotkey.Empty;
        if (!_inited || Settings.Hotkey == hotkey)
        {
            return;
        }

        Settings.Hotkey = hotkey;
        SettingsManager.Save();
        UpdateFixHotKey();
    }

    /// <summary>
    /// 使用说明
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void ManualButton_Click(object sender, EventArgs e)
    {
        using var manual = new Manual();
        manual.StartPosition = FormStartPosition.CenterParent;
        manual.ShowDialog();
    }

    #region 系统托盘

    /// <summary>
    /// 从托盘显示
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void NotifyIcon_MouseClick(object sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (ShowInTaskbar)
            {
                HideWindow();
            }
            else
            {
                ShowWindow();
            }
        }
    }

    /// <summary>
    /// 窗口最小化到托盘
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Main_SizeChanged(object sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized)
        {
            HideWindow();
        }
    }

    /// <summary>
    /// 点击显示窗口
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void ShowWindowMenuItem_Click(object sender, EventArgs e)
    {
        ShowWindow();
    }

    private void ExitApplication(object sender, EventArgs e)
    {
        _exitRequested = true;
        Close();
    }

    private void ShowWindow()
    {
        _allowShow = true;
        ShowInTaskbar = true;                 //设置程序允许显示在任务栏
        Visible = true;                       //设置窗口可见
        WindowState = FormWindowState.Normal; //设置窗口状态
        Activate();                           //设置窗口为活动状态，防止被其他窗口遮挡。
    }

    private void HideWindow()
    {
        Visible = false;       //隐藏窗口
        ShowInTaskbar = false; //将程序从任务栏移除显示
    }

    private void ToggleWindow()
    {
        if (ShowMeHotKeyInputBox.IsEditing)
        {
            return;
        }

        var nowTicks = DateTime.Now.Ticks;
        if (nowTicks - _lastToggleWindowTicks >= ToggleWindowThresholdTicks)
        {
            _lastToggleWindowTicks = nowTicks;
            var foregroundWindow = API.GetForegroundWindow();
            if (Visible && Handle == foregroundWindow)
            {
                HideWindow();
            }
            else
            {
                ShowWindow();
            }
        }
    }

    /// <summary>
    /// 显示本窗口快捷键改变
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void ShowMeHotKeyInputBox_HotKeyChanged(object sender, EventArgs e)
    {
        var hotkey = ShowMeHotKeyInputBox.Hotkey;
        hotkey = hotkey.IsValid ? hotkey : Hotkey.DefaultShowMeHotkey;
        if (!_inited || Settings.ShowMeHotkey == hotkey)
        {
            return;
        }

        Settings.ShowMeHotkey = hotkey;
        SettingsManager.Save();
        UpdateShowMeHotKey();
    }

    /// <summary>
    /// 更新显示窗口快捷键
    /// </summary>
    private void UpdateShowMeHotKey()
    {
        if (_showMeHotKeyId != 0)
        {
            _hook.UnregisterHotKey(_showMeHotKeyId);
            _showMeHotKeyId = 0;
        }

        var hotKey = ShowMeHotKeyInputBox.Hotkey;
        if (hotKey.IsValid)
        {
            var id = _hook.RegisterHotKey(hotKey.Modifier, hotKey.KeyCode, ShowMeHotKeyId);
            if (id.HasValue)
            {
                _showMeHotKeyId = id.Value;
                ActiveControl = null;
            }
            else
            {
                ShowNotification("注册显示窗口快捷键失败，请从托盘打开设置更换快捷键。");
            }
        }
        else
        {
            ShowNotification("显示窗口快捷键无效，请从托盘打开设置更换快捷键。");
        }
    }

    #endregion

    #region 自动窗口模式

    /// <summary>
    /// 自动应用窗口模式
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void AutoWindowCheckBox_CheckedChanged(object sender, EventArgs e)
    {
        if (!_inited || Settings.AutoApplyWindow == AutoApplyCheckBox.Checked)
        {
            return;
        }

        Settings.AutoApplyWindow = AutoApplyCheckBox.Checked;
        SettingsManager.Save();
        if (Settings.AutoApplyWindow)
        {
            Apply();
            _war3RunningMonitor.Start();
        }
        else
        {
            _war3RunningMonitor.Stop();
        }
    }

    /// <summary>
    /// 扫描魔兽3进程
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void CheckWar3Process(object sender, ElapsedEventArgs e)
    {
        if (_shuttingDown || !Settings.AutoApplyWindow)
        {
            return;
        }

        var window = Fixer.GetWar3Window();
        if (window == IntPtr.Zero)
        {
            return;
        }

        Task.Run(
            async () =>
            {
                await Task.Delay(800);
                if (!_shuttingDown && Settings.AutoApplyWindow)
                {
                    Apply();
                }
            });
    }

    /// <summary>
    /// 应用窗口模式
    /// </summary>
    private void Apply()
    {
        Fixer.Apply();
    }

    #endregion

    #region 锁定鼠标

    /// <summary>
    /// 锁定鼠标
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void LockCursorCheckBox_CheckedChanged(object sender, EventArgs e)
    {
        if (!_inited || Settings.LockCursor == LockCursorCheckBox.Checked)
        {
            return;
        }

        Settings.LockCursor = LockCursorCheckBox.Checked;
        SettingsManager.Save();
    }

    /// <summary>
    /// 窗口改变事件，设置鼠标锁定
    /// </summary>
    /// <param name="hWinEventHook"></param>
    /// <param name="eventType"></param>
    /// <param name="hwnd"></param>
    /// <param name="idObject"></param>
    /// <param name="idChild"></param>
    /// <param name="dwEventThread"></param>
    /// <param name="dwmsEventTime"></param>
    public void LockCursorEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        Fixer.LockCursor();
    }

    /// <summary>
    /// 锁定鼠标
    /// </summary>
    private void LockCursor()
    {
        _winEventHook = API.SetWinEventHook(API.EVENT_SYSTEM_FOREGROUND, API.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _lockCursorCallback, 0, 0, API.WINEVENT_OUTOFCONTEXT);
    }

    /// <summary>
    /// 解锁鼠标
    /// </summary>
    private void UnlockCursor()
    {
        if (_winEventHook != IntPtr.Zero)
        {
            API.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }

        API.ClipCursor(IntPtr.Zero);
    }

    #endregion

    /// <summary>
    /// 自定义宽度
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void WidthInput_ValueChanged(object sender, EventArgs e)
    {
        if (!_inited || Settings.Width == (int)WidthInput.Value)
        {
            return;
        }

        Settings.Width = (int)WidthInput.Value;
        SettingsManager.Save();
    }

    /// <summary>
    /// 自定义高度
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void HeightInput_ValueChanged(object sender, EventArgs e)
    {
        if (!_inited || Settings.Height == (int)HeightInput.Value)
        {
            return;
        }

        Settings.Height = (int)HeightInput.Value;
        SettingsManager.Save();
    }

    /// <summary>
    /// 自定义位置X
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void CustomPositionX_ValueChanged(object sender, EventArgs e)
    {
        if (!_inited || Settings.X == (int)CustomPositionX.Value)
        {
            return;
        }

        Settings.X = (int)CustomPositionX.Value;
        SettingsManager.Save();
    }

    /// <summary>
    /// 自定义位置Y
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void CustomPositionY_ValueChanged(object sender, EventArgs e)
    {
        if (!_inited || Settings.Y == (int)CustomPositionY.Value)
        {
            return;
        }

        Settings.Y = (int)CustomPositionY.Value;
        SettingsManager.Save();
    }

    /// <summary>
    /// 启用/停用自定义位置
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void CustomPositionCheckBox_CheckedChanged(object sender, EventArgs e)
    {
        if (!_inited || Settings.UseCustomPosition == CustomPositionCheckBox.Checked)
        {
            return;
        }

        Settings.UseCustomPosition = CustomPositionCheckBox.Checked;
        SettingsManager.Save();
        var enableCustomPosition = Settings.WindowMode == WindowMode.Custom && CustomPositionCheckBox.Checked;
        CustomPositionX.Enabled = enableCustomPosition;
        CustomPositionY.Enabled = enableCustomPosition;
    }

    /// <summary>
    /// 窗口模式改变
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void WindowModeComboBox_SelectedIndexChanged(object sender, EventArgs e)
    {
        var mode = (ComboBoxItem<WindowMode>)WindowModeComboBox.SelectedItem;
        if (!_inited || Settings.WindowMode == mode.Value)
        {
            return;
        }

        Settings.WindowMode = mode.Value;
        SettingsManager.Save();

        var useCustomMode = mode.Value == WindowMode.Custom;
        WidthInput.Enabled = useCustomMode;
        HeightInput.Enabled = useCustomMode;
        CustomPositionCheckBox.Enabled = useCustomMode;
        CustomPositionX.Enabled = useCustomMode && CustomPositionCheckBox.Checked;
        CustomPositionY.Enabled = useCustomMode && CustomPositionCheckBox.Checked;

        if (Settings.AutoApplyWindow)
        {
            Apply();
        }
    }
}
