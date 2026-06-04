using System.Globalization;
using System.Text;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

#if DEBUG
[assembly: System.Reflection.Metadata.MetadataUpdateHandler(typeof(Aprillz.MewUI.HotReload.MewUiMetadataUpdateHandler))]
#endif

Startup();

var controller = new LegendsConfigController(GetConfigPath());

Application
    .Create()
    .UseAccent(Accent.Green)
    .BuildMainWindow(() =>
        new Window()
            .Resizable(980, 720)
            .StartCenterScreen()
            .OnBuild(w => w
                .Title("MewUI Legends.ini 配置编辑器")
                .Content(controller.BuildView())
                .OnLoaded(() => controller.Start())
                .OnClosed(() => controller.Dispose())))
    .Run();

static string GetConfigPath()
{
    var args = Environment.GetCommandLineArgs();
    if (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]))
    {
        return Path.GetFullPath(args[1]);
    }

    return Path.Combine(AppContext.BaseDirectory, "Legends.ini");
}

static void Startup()
{
    var args = Environment.GetCommandLineArgs();

#if MEWUI_LEGENDS_WIN
#pragma warning disable CA1416
    Win32Platform.Register();
    if (args.Any(a => a is "--gdi"))
    {
        GdiBackend.Register();
    }
    else if (args.Any(a => a is "--vg"))
    {
        MewVGWin32Backend.Register();
    }
    else
    {
        Direct2DBackend.Register();
    }
#pragma warning restore CA1416
#elif MEWUI_LEGENDS_OSX
    MacOSPlatform.Register();
    MewVGMacOSBackend.Register();
#elif MEWUI_LEGENDS_LINUX
    X11Platform.Register();
    MewVGX11Backend.Register();
#else
    if (OperatingSystem.IsWindows())
    {
        Win32Platform.Register();
        if (args.Any(a => a is "--gdi"))
        {
            GdiBackend.Register();
        }
        else if (args.Any(a => a is "--vg"))
        {
            MewVGWin32Backend.Register();
        }
        else
        {
            Direct2DBackend.Register();
        }
    }
    else if (OperatingSystem.IsMacOS())
    {
        MacOSPlatform.Register();
        MewVGMacOSBackend.Register();
    }
    else
    {
        X11Platform.Register();
        MewVGX11Backend.Register();
    }
#endif

    Application.DispatcherUnhandledException += e =>
    {
        try
        {
            NativeMessageBox.Show(e.Exception.ToString(), "未处理的 UI 异常");
        }
        catch
        {
            // Best effort for fatal UI errors.
        }

        e.Handled = true;
    };
}

sealed class LegendsConfigController : IDisposable
{
    private readonly string _configPath;
    private readonly string _preferencePath;
    private readonly StackPanel _itemsPanel = new();
    private readonly ObservableValue<string> _status = new("就绪");
    private readonly ObservableValue<string> _summary = new(string.Empty);
    private readonly List<SettingEditor> _editors = new();
    private readonly DispatcherTimer _reloadTimer = new(TimeSpan.FromMilliseconds(350));
    private readonly object _watchGate = new();

    private FileSystemWatcher? _watcher;
    private IniDocument _document = new();
    private Dictionary<string, string> _preferences = new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressWatcher;
    private bool _disposed;
    private bool _configAvailable = true;

    public LegendsConfigController(string configPath)
    {
        _configPath = configPath;
        _preferencePath = _configPath + ".userprefs";
        _itemsPanel.Vertical().Spacing(12).Padding(4);
        _reloadTimer.Tick += () =>
        {
            _reloadTimer.Stop();
            LoadLatest(restorePreferences: true, reason: "检测到外部变更");
        };
    }

    public Element BuildView() => new DockPanel()
        .Padding(16)
        .Spacing(12)
        .Children(
            Header().DockTop(),
            Actions().DockTop(),
            new ScrollViewer()
                .VerticalScroll(ScrollMode.Auto)
                .Content(_itemsPanel));

    public void Start()
    {
        _preferences = PreferenceStore.Load(_preferencePath);
        StartWatcher();
        LoadLatest(restorePreferences: true, reason: "已加载");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _reloadTimer.Dispose();
        _watcher?.Dispose();
    }

    private Element Header() => CreateCard(
        new StackPanel()
            .Vertical()
            .Spacing(6)
            .Children(
                new TextBlock()
                    .Text("Legends.ini 配置编辑器")
                    .FontSize(20)
                    .Bold(),
                new TextBlock()
                    .Text($"配置文件：{_configPath}")
                    .TextWrapping(TextWrapping.Wrap),
                new TextBlock()
                    .Text($"用户偏好：{_preferencePath}")
                    .TextWrapping(TextWrapping.Wrap),
                new TextBlock()
                    .BindText(_summary)
                    .TextWrapping(TextWrapping.Wrap),
                new TextBlock()
                    .BindText(_status)
                    .TextWrapping(TextWrapping.Wrap)));

    private Element Actions() => new StackPanel()
        .Horizontal()
        .Spacing(8)
        .Children(
            new Button()
                .Content("重新加载")
                .OnClick(() => LoadLatest(restorePreferences: true, reason: "已重新加载")),
            new Button()
                .Content("保存偏好")
                .OnClick(Save),
            new Button()
                .Content("恢复偏好到 ini")
                .OnClick(() =>
                {
                    LoadLatest(restorePreferences: true, reason: "已恢复偏好");
                    SaveIniOnly();
                }),
            new Button()
                .Content("忘记偏好")
                .OnClick(() =>
                {
                    _preferences.Clear();
                    if (File.Exists(_preferencePath))
                    {
                        File.Delete(_preferencePath);
                    }
                    LoadLatest(restorePreferences: false, reason: "已清除偏好");
                }));

    private void StartWatcher()
    {
        var directory = Path.GetDirectoryName(_configPath);
        if (string.IsNullOrEmpty(directory))
        {
            directory = AppContext.BaseDirectory;
        }

        if (!Directory.Exists(directory))
        {
            return;
        }

        _watcher = new FileSystemWatcher(directory, Path.GetFileName(_configPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };

        _watcher.Changed += (_, _) => ScheduleExternalReload();
        _watcher.Created += (_, _) => ScheduleExternalReload();
        _watcher.Renamed += (_, _) => ScheduleExternalReload();
        _watcher.Deleted += (_, _) => ScheduleExternalReload();
    }

    private void ScheduleExternalReload()
    {
        lock (_watchGate)
        {
            if (_suppressWatcher || _disposed)
            {
                return;
            }
        }

        Application.Current.Dispatcher?.BeginInvoke(() =>
        {
            if (!_disposed)
            {
                _reloadTimer.Stop();
                _reloadTimer.Start();
            }
        });
    }

    private void LoadLatest(bool restorePreferences, string reason)
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                ShowMissingConfigError();
                return;
            }

            _configAvailable = true;
            var document = IniDocument.Load(_configPath);
            var restored = restorePreferences ? document.ApplyPreferences(_preferences) : 0;
            _document = document;
            RebuildEditors();

            if (restored > 0 && restorePreferences)
            {
                WriteDocument();
            }

            _summary.Value = $"分类：{_document.SectionCount}，配置项：{_document.SettingCount}，已保存偏好：{_preferences.Count}";
            _status.Value = restored > 0
                ? $"{reason}：已将 {restored} 个偏好值恢复到最新 Legends.ini 结构中（{DateTime.Now:T}）。"
                : $"{reason}：正在显示最新 Legends.ini（{DateTime.Now:T}）。";
        }
        catch (Exception ex)
        {
            _status.Value = $"读取 Legends.ini 失败：{ex.Message}";
        }
    }

    private void RebuildEditors()
    {
        _editors.Clear();
        _itemsPanel.Clear();

        if (!_configAvailable)
        {
            _itemsPanel.Add(CreateCard(
                new TextBlock()
                    .Text("未找到 Legends.ini。请将本程序放在正确的 MapleLegends 游戏文件夹中（与 Legends.ini 同一目录）运行；修正位置后重启程序或点击重新加载。")
                    .TextWrapping(TextWrapping.Wrap)));
            return;
        }

        if (_document.Lines.Count == 0)
        {
            _itemsPanel.Add(CreateCard(new TextBlock()
                .Text("Legends.ini 为空。编辑器将按文件实际内容显示，不会创建或假设默认配置。")
                .TextWrapping(TextWrapping.Wrap)));
            return;
        }

        var sections = BuildSectionGroups();
        var tabs = sections
            .Select(section => new TabItem()
                .Header(section.DisplayName, accessKey: false)
                .Content(new ScrollViewer()
                    .VerticalScroll(ScrollMode.Auto)
                    .Content(section.Panel)))
            .ToArray();

        _itemsPanel.Add(new TabControl().TabItems(tabs));
    }

    private List<SectionGroup> BuildSectionGroups()
    {
        var groups = new List<SectionGroup>();
        var pendingComments = new List<CommentLine>();

        SectionGroup EnsureGroup(string sectionName)
        {
            var displayName = string.IsNullOrEmpty(sectionName)
                ? "通用"
                : Translation.ToChinese(sectionName);
            var group = new SectionGroup(sectionName, displayName, new StackPanel().Vertical().Spacing(12).Padding(4));
            groups.Add(group);
            return group;
        }

        void FlushComments(SectionGroup group)
        {
            foreach (var comment in pendingComments)
            {
                group.Panel.Add(new TextBlock()
                    .Text(Translation.TranslateCommentLine(comment.Text))
                    .TextWrapping(TextWrapping.Wrap));
            }

            pendingComments.Clear();
        }

        var current = EnsureGroup(string.Empty);

        foreach (var line in _document.Lines)
        {
            switch (line)
            {
                case SectionLine section:
                    FlushComments(current);
                    current = EnsureGroup(section.Name);
                    current.Panel.Add(new TextBlock()
                        .Text($"[{Translation.ToChinese(section.Name)}]")
                        .FontSize(17)
                        .Bold());
                    break;

                case CommentLine comment:
                    pendingComments.Add(comment);
                    break;

                case SettingLine setting:
                    pendingComments.Clear();
                    var editor = new SettingEditor(setting, new ObservableValue<string>(setting.Value));
                    editor.Value.Changed += () => setting.Value = editor.Value.Value;
                    _editors.Add(editor);
                    current.Panel.Add(BuildSetting(editor));
                    break;

                case BlankLine:
                    FlushComments(current);
                    current.Panel.Add(new Border().Height(4));
                    break;

                case RawLine raw:
                    FlushComments(current);
                    current.Panel.Add(new TextBlock()
                        .Text(raw.Text)
                        .TextWrapping(TextWrapping.Wrap));
                    break;
            }
        }

        FlushComments(current);
        return groups.Where(static group => group.Panel.Count > 0).ToList();
    }

    private Element BuildSetting(SettingEditor editor)
    {
        var setting = editor.Setting;
        var label = Translation.ToChinese(setting.Key);
        var originalKey = string.IsNullOrEmpty(setting.Section)
            ? setting.Key
            : $"{setting.Section}.{setting.Key}";

        var explanations = setting.Explanation
            .Select(Translation.ToChinese)
            .ToList();
        if (!string.IsNullOrWhiteSpace(setting.InlineComment))
        {
            explanations.Add(Translation.ToChinese(setting.InlineComment.Trim().TrimStart(';', '#').Trim()));
        }

        var explanation = string.Join(Environment.NewLine, explanations.Where(static text => !string.IsNullOrWhiteSpace(text)));

        return CreateCard(
            new Grid()
                .Columns("240,*")
                .Spacing(12)
                .Children(
                    new StackPanel()
                        .Vertical()
                        .Spacing(4)
                        .Column(0)
                        .Children(
                            new TextBlock()
                                .Text(label)
                                .Bold()
                                .TextWrapping(TextWrapping.Wrap),
                            new TextBlock()
                                .Text(originalKey)
                                .FontSize(11)
                                .TextWrapping(TextWrapping.Wrap),
                            new TextBlock()
                                .Text($"第 {setting.LineNumber.ToString(CultureInfo.InvariantCulture)} 行")
                                .FontSize(11)),
                    new StackPanel()
                        .Vertical()
                        .Spacing(8)
                        .Column(1)
                        .Children(
                            BuildSettingControl(editor),
                            BuildExplanation(explanation))));
    }

    private Element BuildSettingControl(SettingEditor editor)
    {
        var setting = editor.Setting;
        if (TryParseBoolean(setting.Value, out var isChecked))
        {
            var value = new ObservableValue<bool>(isChecked);
            value.Changed += () => setting.Value = value.Value ? "true" : "false";
            return new StackPanel()
                .Horizontal()
                .Spacing(8)
                .Children(
                    new ToggleSwitch().BindIsChecked(value),
                    new TextBlock().BindText(value, static v => v ? "启用" : "禁用").CenterVertical());
        }

        var options = SettingOptions.From(setting).ToList();
        if (options.Count > 0)
        {
            var selectedIndex = Math.Max(0, options.FindIndex(option => string.Equals(option.Value, setting.Value, StringComparison.OrdinalIgnoreCase)));
            if (options.FindIndex(option => string.Equals(option.Value, setting.Value, StringComparison.OrdinalIgnoreCase)) < 0)
            {
                options.Insert(0, new SettingOption(setting.Value, setting.Value));
                selectedIndex = 0;
            }

            var selected = new ObservableValue<int>(selectedIndex);
            selected.Changed += () =>
            {
                if (selected.Value >= 0 && selected.Value < options.Count)
                {
                    setting.Value = options[selected.Value].Value;
                }
            };

            return new ComboBox()
                .Items(options, static option => option.DisplayText)
                .BindSelectedIndex(selected);
        }

        return new TextBox().BindText(editor.Value);
    }

    private static bool TryParseBoolean(string value, out bool result)
    {
        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
            return true;
        }

        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            result = false;
            return true;
        }

        result = false;
        return false;
    }

    private Element BuildExplanation(string explanation)
    {
        return string.IsNullOrWhiteSpace(explanation)
            ? new Border().Height(0)
            : new TextBlock()
                .Text(explanation)
                .TextWrapping(TextWrapping.Wrap);
    }

    private FrameworkElement CreateCard(UIElement content) => new Border()
        .Padding(14)
        .CornerRadius(10)
        .BorderThickness(1)
        .Child(content);

    private void Save()
    {
        if (!File.Exists(_configPath))
        {
            ShowMissingConfigError();
            return;
        }

        foreach (var editor in _editors)
        {
            _preferences[editor.Setting.PreferenceKey] = editor.Setting.Value;
        }

        PreferenceStore.Save(_preferencePath, _preferences);
        SaveIniOnly();
    }

    private void SaveIniOnly()
    {
        if (!File.Exists(_configPath))
        {
            ShowMissingConfigError();
            return;
        }

        try
        {
            WriteDocument();
            _status.Value = $"已保存 Legends.ini 和用户偏好快照（{DateTime.Now:T}）。";
        }
        catch (Exception ex)
        {
            _status.Value = $"保存 Legends.ini 失败：{ex.Message}";
        }
    }

    private void ShowMissingConfigError()
    {
        _configAvailable = false;
        _document = new IniDocument();
        RebuildEditors();
        _summary.Value = "未找到 Legends.ini。";
        _status.Value = $"未找到 Legends.ini：{_configPath}。请将本程序放在正确的 MapleLegends 游戏文件夹中运行。";

        try
        {
            NativeMessageBox.Show(
                "未找到 Legends.ini。请将本程序放在正确的 MapleLegends 游戏文件夹中（与 Legends.ini 同一目录）并重新运行。",
                "未找到 Legends.ini");
        }
        catch
        {
            // The status text in the main window carries the same message if native dialogs are unavailable.
        }
    }

    private void WriteDocument()
    {
        lock (_watchGate)
        {
            _suppressWatcher = true;
        }

        try
        {
            _document.Save(_configPath);
        }
        finally
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(500).ConfigureAwait(false);
                lock (_watchGate)
                {
                    _suppressWatcher = false;
                }
            });
        }
    }
}


sealed record SectionGroup(string Name, string DisplayName, StackPanel Panel);

sealed record SettingOption(string Value, string DisplayText);

static class SettingOptions
{
    public static IEnumerable<SettingOption> From(SettingLine setting)
    {
        foreach (var explanation in setting.Explanation)
        {
            var separatorIndex = explanation.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var value = explanation[..separatorIndex].Trim();
            var description = explanation[(separatorIndex + 1)..].Trim();
            if (value.Length == 0 || description.Length == 0)
            {
                continue;
            }

            yield return new SettingOption(value, $"{value} - {Translation.ToChinese(description)}");
        }
    }
}

static class Translation
{
    private static readonly Dictionary<string, string> EnglishToChinese = new(StringComparer.OrdinalIgnoreCase)
    {
        ["APPEARANCE"] = "外观",
        ["PERFORMANCE"] = "性能",
        ["GAME"] = "游戏",
        ["HDClient"] = "分辨率",
        ["Windowed"] = "窗口模式",
        ["DarkChat"] = "深色聊天背景",
        ["DarkQuestAlarm"] = "深色任务提醒背景",
        ["StreamerMode"] = "主播模式",
        ["WeaponEffects"] = "武器特效",
        ["WeaponsBehindCharacter"] = "武器背在角色身后",
        ["SkipLogoAnimation"] = "跳过开场动画",
        ["AutoClearCache"] = "自动清理图片缓存",
        ["FastLoading"] = "快速加载",
        ["Transition"] = "地图切换效果",
        ["InfiniteChatLog"] = "无限聊天记录",
        ["ClickMode"] = "点击交互模式",
        ["NpcInteractBehavior"] = "NPC 交互按键行为",
        ["WhiteScrollPrompt"] = "白卷使用确认",
        ["CloseGameConfirmation"] = "关闭游戏确认",
        ["FilterChatNotices"] = "过滤聊天提示",
        ["AddMonsterLevelTag"] = "显示怪物等级",
        ["RaiseDamageLines"] = "抬高伤害数字",
        ["HighlightUpgradeSlots"] = "高亮升级次数",
        ["OldSchool"] = "旧版界面元素",
        ["MapleLegends - Old School MapleStory Configuration Menu (https://maplelegends.com/)"] = "MapleLegends - 旧版 MapleStory 配置菜单（https://maplelegends.com/）",
        ["Version 1.34.0 Mar 22 2026"] = "版本 1.34.0（2026 年 3 月 22 日）",
        ["true = enabled | false = disabled"] = "true = 启用 | false = 禁用",
        ["This sets your resolution for MapleLegends"] = "设置 MapleLegends 的游戏分辨率。",
        ["Run client in windowed mode"] = "以窗口模式运行客户端。",
        ["Put on false if you prefer starting in full-screen mode"] = "如果希望启动时进入全屏模式，请设置为 false。",
        ["In-game you can also press ALT + ENTER to toggle between windowed and full-screen"] = "在游戏中也可以按 ALT + ENTER 在窗口和全屏之间切换。",
        ["Enable darker chat background, which helps you seeing the chat in bright areas"] = "启用更深色的聊天背景，便于在明亮场景中看清聊天内容。",
        ["Enable darker Quest alarm background, which makes quests data visible on all backgrounds."] = "启用更深色的任务提醒背景，使任务信息在各种背景上都更清晰。",
        ["Enable the Streamer Mode, which hides the login ID in the Title screen and in the Cash Shop."] = "启用主播模式，在标题画面和商城中隐藏登录 ID。",
        ["Set to false to hide the visual effects that would show when swinging a cosmetic Weapon cover."] = "设置为 false 可隐藏挥动外观武器覆盖物时显示的视觉特效。",
        ["Set to true to allow any two-handed weapon to appear behind the character's back, when standing in a rest position."] = "设置为 true 后，角色静止站立时任意双手武器都可显示在角色背后。",
        ["This skips the starting logo animations and sends you straight to login."] = "跳过启动 Logo 动画，直接进入登录界面。",
        ["Automatic cleaning of the image cache. Set to false if you experience freezing or stuttering during gameplay."] = "自动清理图片缓存。如果游戏中出现卡死或卡顿，可设置为 false。",
        ["Turning it to false may cause crashes while bossing instead."] = "但设置为 false 可能会导致打 Boss 时崩溃。",
        ["If set to true, allows for some game elements to be loaded more quickly on game launch."] = "设置为 true 时，部分游戏元素可在启动时更快加载。",
        ["Set it to false if you experience frequent crashes after character selection."] = "如果选择角色后经常崩溃，请设置为 false。",
        ["Select the type of transitioning between two maps. This affects the duration and speed of the dark screen during map transfer."] = "选择地图之间的切换类型；这会影响换图时黑屏的持续时间和速度。",
        ["Enable infinite chat logging, which allows you to see everything said in-game from your game session without it erasing."] = "启用无限聊天记录，使当前游戏会话中的聊天内容不会被自动清除。",
        ["Change this setting if you would like NPCs and Hired Merchants to only require a single click to interact with."] = "如果希望 NPC 和雇佣商人只需单击即可交互，请修改此设置。",
        ["Change this setting if you would like NPC interact keys to work in dialogue options."] = "如果希望 NPC 交互键可用于对话选项，请修改此设置。",
        ["Adds a confirmation prompt for White Scroll usage in the Legendary Spirit window. Set to false to remove the prompt."] = "在传奇之魂窗口中使用白卷时增加确认提示；设置为 false 可移除该提示。",
        ["Adds a confirmation popup when closing the game via Alt-F4, via the X button, or by pressing Quit Game while ingame."] = "通过 Alt-F4、窗口 X 按钮或游戏内退出按钮关闭游戏时显示确认弹窗。",
        ["Set to false to remove the popup."] = "设置为 false 可移除该弹窗。",
        ["When set to true, chat notices about skill cooldowns or skill unavailability will not be displayed."] = "设置为 true 时，不显示技能冷却或技能不可用的聊天提示。",
        ["When set to false, those chat notices will regularly get displayed on a two-seconds cooldown."] = "设置为 false 时，这些聊天提示会以两秒冷却间隔正常显示。",
        ["Adds the level of a monster next to its name tag, under the sprite."] = "在怪物名称旁、精灵图下方显示怪物等级。",
        ["Set to false to keep only the monster name."] = "设置为 false 则仅保留怪物名称。",
        ["Changes the height at which the damage numbers over a monster's head are displayed."] = "改变怪物头顶伤害数字显示的高度。",
        ["Set to true for the damage lines to appear above the monster buff icons."] = "设置为 true 时，伤害数字会显示在怪物增益图标上方。",
        ["When set to true, equip upgrade slots number will be blue if the item is not clean, and red if no slots are left."] = "设置为 true 时，装备不干净时升级次数显示为蓝色，无剩余次数时显示为红色。",
        ["Toggles between regular and old-school version of certain game elements (name in statusbar, game cursor)."] = "在部分游戏元素（状态栏名称、游戏光标）的常规版本和旧版之间切换。",
        ["Set to true for the old-school version of the above elements to be enabled."] = "设置为 true 可启用上述元素的旧版样式。",
        ["--------------------------------------"] = "--------------------------------------",
        ["Run into a problem running the game?"] = "运行游戏时遇到问题？",
        ["Website: https://maplelegends.com/"] = "官网：https://maplelegends.com/",
        ["Forums: https://forum.maplelegends.com/"] = "论坛：https://forum.maplelegends.com/",
        ["ijl15.dll / MapleLegends.exe not found errors? Your anti-virus is blocking the game. Make an exception to MapleLegends folder!"] = "提示找不到 ijl15.dll / MapleLegends.exe？可能是杀毒软件拦截了游戏，请将 MapleLegends 文件夹加入例外。",
        ["*File* is either not designed for Windows or contains an error? Turn off Windows Smart App. This is a new feature bundled with windows 11, which stops unsigned programs, such as this fan server, from running."] = "提示 *File* 不适用于 Windows 或包含错误？请关闭 Windows Smart App；这是 Windows 11 的新功能，会阻止此类未签名程序运行。",
        ["Classic (slowest)"] = "经典（最慢）",
        ["Modern GMS-like"] = "现代 GMS 风格",
        ["MapleLegends' tweaks (fastest)"] = "MapleLegends 调整版（最快）",
        ["Classic (double click)"] = "经典（双击）",
        ["Modern GMS-like (NPCs require single click)"] = "现代 GMS 风格（NPC 单击交互）",
        ["MapleLegends' tweaks (NPCs and Hired Merchants require single click)"] = "MapleLegends 调整版（NPC 和雇佣商人单击交互）",
        ["Classic (Close the dialogue)"] = "经典（关闭对话）",
        ["Modern GMS-like (Skips the text animation and selects the dialogue)"] = "现代 GMS 风格（跳过文字动画并选择对话）",
        ["MapleLegends' tweaks (Pressing once skips the animation and pressing again selects the dialogue)"] = "MapleLegends 调整版（按一次跳过动画，再按一次选择对话）",
    };

    public static string ToChinese(string text)
    {
        if (EnglishToChinese.TryGetValue(text.Trim(), out var translated))
        {
            return translated;
        }

        return text;
    }

    public static string TranslateCommentLine(string raw)
    {
        var trimmed = raw.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is not (';' or '#'))
        {
            return ToChinese(raw);
        }

        var prefixLength = raw.Length - trimmed.Length;
        var marker = trimmed[0];
        var text = trimmed[1..].Trim();
        var translated = ToChinese(text);
        return raw[..prefixLength] + marker + " " + translated;
    }
}

sealed class SettingEditor(SettingLine setting, ObservableValue<string> value)
{
    public SettingLine Setting { get; } = setting;

    public ObservableValue<string> Value { get; } = value;
}

abstract class IniLine
{
    public int LineNumber { get; set; }

    public abstract string ToIniText();
}

sealed class BlankLine : IniLine
{
    public override string ToIniText() => string.Empty;
}

sealed class RawLine(string text) : IniLine
{
    public string Text { get; } = text;

    public override string ToIniText() => Text;
}

sealed class CommentLine(string text) : IniLine
{
    public string Text { get; } = text;

    public override string ToIniText() => Text;
}

sealed class SectionLine(string name, string originalText) : IniLine
{
    public string Name { get; } = name;

    public string OriginalText { get; } = originalText;

    public override string ToIniText() => OriginalText;
}

sealed class SettingLine(
    string section,
    string key,
    string value,
    string separator,
    string leadingWhitespace,
    string keyPadding,
    string valuePrefix,
    string valueSuffix,
    IReadOnlyList<string> explanation) : IniLine
{
    public string Section { get; } = section;

    public string Key { get; } = key;

    public string Value { get; set; } = value;

    public string Separator { get; } = separator;

    public string LeadingWhitespace { get; } = leadingWhitespace;

    public string KeyPadding { get; } = keyPadding;

    public string ValuePrefix { get; } = valuePrefix;

    public string InlineComment { get; } = valueSuffix;

    public IReadOnlyList<string> Explanation { get; } = explanation;

    public string PreferenceKey => Section + "\u001f" + Key;

    public override string ToIniText() => LeadingWhitespace + Key + KeyPadding + Separator + ValuePrefix + Value + InlineComment;
}

sealed class IniDocument
{
    private string _newline = Environment.NewLine;

    public List<IniLine> Lines { get; } = new();

    public int SettingCount => Lines.OfType<SettingLine>().Count();

    public int SectionCount => Lines.OfType<SectionLine>().Count();

    public static IniDocument Load(string path)
    {
        var text = ReadAllTextWithRetry(path);
        var document = new IniDocument { _newline = DetectNewline(text) };
        var rawLines = SplitLines(text);
        var section = string.Empty;
        var pendingComments = new List<string>();

        for (var i = 0; i < rawLines.Length; i++)
        {
            var raw = rawLines[i];
            var lineNumber = i + 1;
            var trimmed = raw.Trim();

            if (trimmed.Length == 0)
            {
                document.Lines.Add(new BlankLine { LineNumber = lineNumber });
                pendingComments.Clear();
                continue;
            }

            if (trimmed[0] is ';' or '#')
            {
                document.Lines.Add(new CommentLine(raw) { LineNumber = lineNumber });
                pendingComments.Add(trimmed.TrimStart(';', '#').Trim());
                continue;
            }

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']') && trimmed.Length >= 2)
            {
                section = trimmed[1..^1].Trim();
                document.Lines.Add(new SectionLine(section, raw) { LineNumber = lineNumber });
                pendingComments.Clear();
                continue;
            }

            if (TryParseSetting(raw, section, pendingComments, out var setting))
            {
                setting.LineNumber = lineNumber;
                document.Lines.Add(setting);
                pendingComments.Clear();
                continue;
            }

            document.Lines.Add(new RawLine(raw) { LineNumber = lineNumber });
            pendingComments.Clear();
        }

        return document;
    }

    public int ApplyPreferences(IReadOnlyDictionary<string, string> preferences)
    {
        var count = 0;
        foreach (var setting in Lines.OfType<SettingLine>())
        {
            if (preferences.TryGetValue(setting.PreferenceKey, out var preferred) && setting.Value != preferred)
            {
                setting.Value = preferred;
                count++;
            }
        }

        return count;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        var text = string.Join(_newline, Lines.Select(static line => line.ToIniText()));
        File.WriteAllText(path, text, Encoding.UTF8);
    }

    private static bool TryParseSetting(string raw, string section, IReadOnlyList<string> comments, out SettingLine setting)
    {
        setting = null!;
        var separatorIndex = raw.IndexOf('=');
        var separator = "=";
        if (separatorIndex < 0)
        {
            separatorIndex = raw.IndexOf(':');
            separator = ":";
        }

        if (separatorIndex <= 0)
        {
            return false;
        }

        var left = raw[..separatorIndex];
        var rawValue = raw[(separatorIndex + 1)..];
        var suffix = ExtractInlineComment(ref rawValue);
        var valuePrefixLength = rawValue.Length - rawValue.TrimStart().Length;
        var valuePrefix = rawValue[..valuePrefixLength];
        var value = rawValue[valuePrefixLength..];
        var leadingCount = left.Length - left.TrimStart().Length;
        var leading = left[..leadingCount];
        var keyWithPadding = left[leadingCount..];
        var key = keyWithPadding.TrimEnd();
        if (key.Length == 0)
        {
            return false;
        }

        var padding = keyWithPadding[key.Length..];
        setting = new SettingLine(section, key, value, separator, leading, padding, valuePrefix, suffix, comments.ToArray());
        return true;
    }

    private static string ExtractInlineComment(ref string value)
    {
        for (var i = 0; i < value.Length - 1; i++)
        {
            if ((value[i] == ' ' || value[i] == '\t') && (value[i + 1] == ';' || value[i + 1] == '#'))
            {
                var suffix = value[i..];
                value = value[..i].TrimEnd();
                return suffix;
            }
        }

        return string.Empty;
    }

    private static string ReadAllTextWithRetry(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : string.Empty;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(80);
            }
        }

        return File.ReadAllText(path, Encoding.UTF8);
    }

    private static string DetectNewline(string text)
    {
        var rn = text.IndexOf("\r\n", StringComparison.Ordinal);
        var n = text.IndexOf('\n');
        return rn >= 0 || n >= 0 ? (rn >= 0 && rn == n - 1 ? "\r\n" : "\n") : Environment.NewLine;
    }

    private static string[] SplitLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return normalized.Split('\n');
    }
}

static class PreferenceStore
{
    public static Dictionary<string, string> Load(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return result;
        }

        foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
        {
            var parts = line.Split('\t');
            if (parts.Length != 3)
            {
                continue;
            }

            var section = Decode(parts[0]);
            var key = Decode(parts[1]);
            var value = Decode(parts[2]);
            result[section + "\u001f" + key] = value;
        }

        return result;
    }

    public static void Save(string path, IReadOnlyDictionary<string, string> preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        var lines = preferences
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static pair =>
            {
                var parts = pair.Key.Split('\u001f', 2);
                var section = parts.Length > 0 ? parts[0] : string.Empty;
                var key = parts.Length > 1 ? parts[1] : string.Empty;
                return Encode(section) + "\t" + Encode(key) + "\t" + Encode(pair.Value);
            });
        File.WriteAllLines(path, lines, Encoding.UTF8);
    }

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
}
