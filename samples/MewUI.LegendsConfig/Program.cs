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
                .Title("MewUI Legends.ini Config")
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
            NativeMessageBox.Show(e.Exception.ToString(), "Unhandled UI exception");
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
    private readonly ObservableValue<string> _status = new("Ready");
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
            LoadLatest(restorePreferences: true, reason: "External change detected");
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
        LoadLatest(restorePreferences: true, reason: "Loaded");
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

    private Element Header() => new Border()
        .Padding(14, 12)
        .BorderThickness(1)
        .Child(
            new StackPanel()
                .Vertical()
                .Spacing(6)
                .Children(
                    new TextBlock()
                        .Text("Legends.ini Configuration Editor")
                        .FontSize(20)
                        .Bold(),
                    new TextBlock()
                        .Text($"Config: {_configPath}")
                        .TextWrapping(TextWrapping.Wrap),
                    new TextBlock()
                        .Text($"Preferences: {_preferencePath}")
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
                .Content("Reload latest")
                .OnClick(() => LoadLatest(restorePreferences: true, reason: "Reloaded")),
            new Button()
                .Content("Save preferences")
                .OnClick(Save),
            new Button()
                .Content("Restore preferences to ini")
                .OnClick(() =>
                {
                    LoadLatest(restorePreferences: true, reason: "Restored preferences");
                    SaveIniOnly();
                }),
            new Button()
                .Content("Forget preferences")
                .OnClick(() =>
                {
                    _preferences.Clear();
                    if (File.Exists(_preferencePath))
                    {
                        File.Delete(_preferencePath);
                    }
                    LoadLatest(restorePreferences: false, reason: "Preferences removed");
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

            _summary.Value = $"Sections: {_document.SectionCount}, settings: {_document.SettingCount}, stored preferences: {_preferences.Count}";
            _status.Value = restored > 0
                ? $"{reason}: restored {restored} preferred value(s) into the latest Legends.ini structure at {DateTime.Now:T}."
                : $"{reason}: showing latest Legends.ini at {DateTime.Now:T}.";
        }
        catch (Exception ex)
        {
            _status.Value = $"Failed to load Legends.ini: {ex.Message}";
        }
    }

    private void RebuildEditors()
    {
        _editors.Clear();
        _itemsPanel.Clear();

        if (!_configAvailable)
        {
            _itemsPanel.Add(new TextBlock()
                .Text("未找到 Legends.ini。请将本程序放在正确的 MapleLegends 游戏文件夹中（与 Legends.ini 同一目录）运行；修正位置后重启程序或点击 Reload latest。")
                .TextWrapping(TextWrapping.Wrap));
            return;
        }

        if (_document.Lines.Count == 0)
        {
            _itemsPanel.Add(new TextBlock().Text("Legends.ini is empty. The editor is showing the file exactly as found and will not invent default settings.").TextWrapping(TextWrapping.Wrap));
            return;
        }

        foreach (var line in _document.Lines)
        {
            switch (line)
            {
                case SectionLine section:
                    _itemsPanel.Add(new TextBlock()
                        .Text(string.IsNullOrEmpty(section.Name) ? "Global" : $"[{section.Name}]")
                        .FontSize(17)
                        .Bold());
                    break;

                case CommentLine comment:
                    _itemsPanel.Add(new TextBlock()
                        .Text(comment.Text)
                        .TextWrapping(TextWrapping.Wrap));
                    break;

                case SettingLine setting:
                    var editor = new SettingEditor(setting, new ObservableValue<string>(setting.Value));
                    editor.Value.Changed += () => setting.Value = editor.Value.Value;
                    _editors.Add(editor);
                    _itemsPanel.Add(BuildSetting(editor));
                    break;

                case BlankLine:
                    _itemsPanel.Add(new Border().Height(4));
                    break;

                case RawLine raw:
                    _itemsPanel.Add(new TextBlock()
                        .Text(raw.Text)
                        .TextWrapping(TextWrapping.Wrap));
                    break;
            }
        }
    }

    private Element BuildSetting(SettingEditor editor)
    {
        var setting = editor.Setting;
        var label = string.IsNullOrEmpty(setting.Section)
            ? setting.Key
            : $"{setting.Section}.{setting.Key}";

        var explanations = setting.Explanation.ToList();
        if (!string.IsNullOrWhiteSpace(setting.InlineComment))
        {
            explanations.Add(setting.InlineComment.Trim());
        }

        var explanation = string.Join(Environment.NewLine, explanations);

        return new Border()
            .Padding(12)
            .BorderThickness(1)
            .Child(
                new Grid()
                    .Columns("220,*")
                    .Spacing(10)
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
                                    .Text($"Line {setting.LineNumber.ToString(CultureInfo.InvariantCulture)}")
                                    .FontSize(11)),
                        new StackPanel()
                            .Vertical()
                            .Spacing(6)
                            .Column(1)
                            .Children(
                                new TextBox()
                                    .BindText(editor.Value),
                                BuildExplanation(explanation))));
    }

    private Element BuildExplanation(string explanation)
    {
        return string.IsNullOrWhiteSpace(explanation)
            ? new Border().Height(0)
            : new TextBlock()
                .Text(explanation)
                .TextWrapping(TextWrapping.Wrap);
    }

    private void Save()
    {
        if (!File.Exists(_configPath))
        {
            ShowMissingConfigError();
            return;
        }

        foreach (var editor in _editors)
        {
            editor.Setting.Value = editor.Value.Value;
            _preferences[editor.Setting.PreferenceKey] = editor.Value.Value;
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
            _status.Value = $"Saved Legends.ini and preference snapshot at {DateTime.Now:T}.";
        }
        catch (Exception ex)
        {
            _status.Value = $"Failed to save Legends.ini: {ex.Message}";
        }
    }

    private void ShowMissingConfigError()
    {
        _configAvailable = false;
        _document = new IniDocument();
        RebuildEditors();
        _summary.Value = "Legends.ini not found.";
        _status.Value = $"未找到 Legends.ini：{_configPath}。请将本程序放在正确的 MapleLegends 游戏文件夹中运行。";

        try
        {
            NativeMessageBox.Show(
                "未找到 Legends.ini。请将本程序放在正确的 MapleLegends 游戏文件夹中（与 Legends.ini 同一目录）并重新运行。",
                "Legends.ini not found");
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

            if (trimmed.StartsWith('[']) && trimmed.EndsWith(']') && trimmed.Length >= 2)
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
