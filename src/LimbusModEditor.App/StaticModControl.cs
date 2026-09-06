using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using LimbusModEditor.Application.StaticMods;

namespace LimbusModEditor.App;

/// <summary>静态数据模组（.staticmod）工作台：读取真实加载器（LCTA
/// launcher/staticmod.py）格式的静态数据模组并展示条目；支持把补丁预览
/// 应用到官方 JSON、以及从「官方 JSON vs 修改 JSON」差异生成 jsonpatch
/// 静态模组。bundle 打补丁与 catalog 双写由加载器在启动时完成（带风险
/// 开关），本工作台只做包的读写与预览，不改写游戏缓存。作为「静态数据」
/// 工作台嵌入主窗口（VS Code 式 tab）。</summary>
public sealed class StaticModControl : UserControl
{
    private readonly StaticModService _service = new();
    private readonly Window? _ownerWindow;
    private readonly TextBlock _manifestSummary;
    private readonly ListBox _entries;
    private readonly TextBlock _status;
    private StaticModPackage? _package;
    private string? _packagePath;

    public StaticModControl(Window? ownerWindow = null)
    {
        _ownerWindow = ownerWindow;

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            Text = "格式与真实加载器一致（staticmod/v1）：manifest.json + patches/<dataClass>_<n>.json" +
                   "（opType=jsonpatch|pathset）+ full/<dataClass>/<file>.json。生成的 .staticmod 放进模组目录，" +
                   "由加载器在启动时应用（需在 LCTA 配置页启用「静态数据 Mod」）。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.Gray
        });

        var openRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var open = new Button { Content = "打开 .staticmod…", Padding = new Thickness(12, 6, 12, 6) };
        open.Click += (_, _) => OpenPackage();
        openRow.Children.Add(open);
        panel.Children.Add(openRow);

        _manifestSummary = new TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, Text = "未打开模组。" };
        panel.Children.Add(_manifestSummary);
        _entries = new ListBox { Height = 220, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(_entries);

        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var preview = new Button { Content = "预览应用补丁到官方 JSON…", Padding = new Thickness(12, 6, 12, 6) };
        preview.Click += (_, _) => PreviewApply();
        var generate = new Button { Content = "从官方/修改 JSON 生成补丁模组…", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(10, 0, 0, 0) };
        generate.Click += (_, _) => GeneratePackage();
        actions.Children.Add(preview);
        actions.Children.Add(generate);
        panel.Children.Add(actions);

        _status = new TextBlock { Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Gray };
        panel.Children.Add(_status);
        Content = panel;
    }

    private bool? ShowDialog(Microsoft.Win32.CommonDialog dialog)
        => _ownerWindow is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();

    private void OpenPackage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 .staticmod 文件",
            Filter = "静态数据模组 (*.staticmod)|*.staticmod|所有文件 (*.*)|*.*"
        };
        if (ShowDialog(dialog) != true) return;
        try
        {
            _package = _service.Read(dialog.FileName);
            _packagePath = dialog.FileName;
            _manifestSummary.Text = $"{_package.Name} v{_package.Version}" +
                (string.IsNullOrWhiteSpace(_package.Description) ? "" : $" — {_package.Description}") +
                $"（补丁 {_package.Patches.Count}，整文件 {_package.FullFiles.Count}）";
            _entries.Items.Clear();
            foreach (var patch in _package.Patches)
                _entries.Items.Add(new ListBoxItem
                {
                    Tag = patch,
                    Content = $"[补丁 {patch.OpType}] {patch.DataClass}/{patch.File}" +
                              (patch.Container is null ? "" : $" → {patch.Container}")
                });
            foreach (var full in _package.FullFiles)
                _entries.Items.Add(new ListBoxItem
                {
                    Tag = full,
                    Content = $"[整文件] {full.DataClass}/{full.File}" +
                              (full.Container is null ? "" : $" → {full.Container}")
                });
            _status.Text = $"已加载 {_packagePath}";
        }
        catch (Exception ex)
        {
            _status.Text = $"读取失败: {ex.Message}";
        }
    }

    private void PreviewApply()
    {
        if (_package is null) { _status.Text = "请先打开 .staticmod。"; return; }
        if (_entries.SelectedItem is not ListBoxItem { Tag: StaticModPatchEntry patch })
        {
            if (_ownerWindow is { } owner) MessageBox.Show(owner, "请先在列表中选择一条补丁条目。", "预览应用", MessageBoxButton.OK, MessageBoxImage.Information);
            else MessageBox.Show("请先在列表中选择一条补丁条目。", "预览应用", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var official = PickJsonFile("选择该条目对应的官方 JSON（从 static bundle 解包或官方数据导出）");
        if (official is null) return;
        var save = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存应用补丁后的 JSON（预览结果，不写入游戏）",
            Filter = "JSON (*.json)|*.json",
            FileName = patch.File + ".patched.json"
        };
        if (ShowDialog(save) != true) return;
        try
        {
            var document = JsonNode.Parse(File.ReadAllText(official))
                ?? throw new InvalidDataException("官方 JSON 为空。");
            var applied = _service.ApplyPatchToDocument(document, _package.Payloads[patch.Source]!, patch.OpType);
            File.WriteAllText(save.FileName, applied.ToJsonString(
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            _status.Text = $"已把 {patch.DataClass}/{patch.File}（{patch.OpType}）应用到 {Path.GetFileName(official)} → {save.FileName}";
        }
        catch (Exception ex)
        {
            _status.Text = $"预览应用失败: {ex.Message}";
        }
    }

    private void GeneratePackage()
    {
        var official = PickJsonFile("选择官方 JSON（原版静态数据）");
        if (official is null) return;
        var modified = PickJsonFile("选择修改后的 JSON");
        if (modified is null) return;
        var dataClass = Prompt("dataClass（静态数据类名，如 personality / skill）：", "skill");
        if (dataClass is null) return;
        var fileName = Prompt("file（目标静态文件名，如 personality-skill-01）：", Path.GetFileNameWithoutExtension(official));
        if (fileName is null) return;
        var container = Prompt("container（bundle 内容器路径，可选）：",
            $"Assets/Resources_moved/StaticData/static-data/{dataClass}/{fileName}.json");
        if (container is null) return;
        var name = Prompt("模组名称：", Path.GetFileNameWithoutExtension(official));
        if (name is null) return;

        var save = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存生成的 .staticmod（放进模组目录，由加载器应用）",
            Filter = "静态数据模组 (*.staticmod)|*.staticmod",
            FileName = name + ".staticmod"
        };
        if (ShowDialog(save) != true) return;
        try
        {
            var package = _service.CreateJsonPatchPackage(name, "1.0.0", string.Empty,
                [(dataClass, fileName, string.IsNullOrWhiteSpace(container) ? null : container, official, modified)]);
            _service.Write(package, save.FileName);
            _status.Text = $"已生成 {save.FileName}（jsonpatch 条目 {package.Patches.Count}）";
            OpenGenerated(save.FileName);
        }
        catch (Exception ex)
        {
            _status.Text = $"生成失败: {ex.Message}";
        }
    }

    private void OpenGenerated(string path)
    {
        // 生成后直接载入，用户可以立即检查条目
        try
        {
            _package = _service.Read(path);
            _packagePath = path;
            _manifestSummary.Text = $"{_package.Name} v{_package.Version}（补丁 {_package.Patches.Count}）";
            _entries.Items.Clear();
            foreach (var patch in _package.Patches)
                _entries.Items.Add(new ListBoxItem
                {
                    Tag = patch,
                    Content = $"[补丁 {patch.OpType}] {patch.DataClass}/{patch.File} → {patch.Container}"
                });
        }
        catch (Exception ex)
        {
            _status.Text = $"生成成功但重新载入失败: {ex.Message}";
        }
    }

    private string? PickJsonFile(string title)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = "JSON (*.json)|*.json|所有文件 (*.*)|*.*" };
        return ShowDialog(dialog) == true ? dialog.FileName : null;
    }

    private string? Prompt(string message, string defaultValue)
    {
        var input = new StaticModPromptWindow(message, defaultValue);
        if (_ownerWindow is { } owner) input.Owner = owner;
        input.ShowDialog();
        return input.Confirmed ? input.Value : null;
    }

    private sealed class StaticModPromptWindow : Window
    {
        private readonly TextBox _box;
        public bool Confirmed { get; private set; }
        public string Value => _box.Text;

        public StaticModPromptWindow(string message, string defaultValue)
        {
            Title = "输入";
            Width = 480;
            Height = 150;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
            _box = new TextBox { Text = defaultValue, Margin = new Thickness(0, 8, 0, 8) };
            panel.Children.Add(_box);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "确定", Padding = new Thickness(14, 5, 14, 5), IsDefault = true };
            ok.Click += (_, _) => { Confirmed = true; Close(); };
            var cancel = new Button { Content = "取消", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(14, 5, 14, 5), IsCancel = true };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            Content = panel;
            _box.Focus();
            _box.SelectAll();
        }
    }
}
