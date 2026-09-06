using System.IO;
using System.Windows;
using System.Windows.Controls;
using LimbusModEditor.Application.Debugging;
using LimbusModEditor.Application.Texts;

namespace LimbusModEditor.App;

/// <summary>lang 文本模组工作台（T2）：围绕真实加载器（LCTA launcher/changes.py）
/// 的 RFC6902 补丁约定，提供「目录差分生成补丁」与「应用补丁到 lang 目录」两个
/// 入口。补丁文件放进模组目录即可被加载器应用；应用语义与加载器一致
/// （目标缺失跳过并报告）。不做 .bak 备份——那是加载器启动/退出时的自动行为。
/// 作为「文本模组」工作台嵌入主窗口（VS Code 式 tab）；ownerWindow 用于
/// 承载文件对话框与消息框的所有权。</summary>
public sealed class LangTextModControl : UserControl
{
    private readonly LangTextPatchService _service = new();
    private readonly Window? _ownerWindow;
    private readonly TextBox _langRootBox;
    private readonly TextBlock _activeLang;
    private readonly ListBox _report;
    private readonly TextBlock _status;

    public LangTextModControl(string defaultLangRoot, string? modsDirectory, Window? ownerWindow = null)
    {
        _ownerWindow = ownerWindow;

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            Text = "文本补丁格式与真实加载器一致：{\"patchs\": {\"<语言目录>/<文件>.json\": [RFC6902 操作]}}。" +
                   "生成的补丁 JSON 复制/保存到模组目录即会被加载器在启动时应用（目标文件自动 .bak，退出还原）。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.Gray
        });

        var rootRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        _langRootBox = new TextBox { Width = 460, VerticalContentAlignment = VerticalAlignment.Center, Text = defaultLangRoot };
        _langRootBox.TextChanged += (_, _) => RefreshActiveLanguage();
        rootRow.Children.Add(_langRootBox);
        var browse = new Button { Content = "浏览…", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 5, 10, 5) };
        browse.Click += (_, _) => { var picked = PickFolder("选择 lang 根目录（LimbusCompany_Data/lang）"); if (picked is not null) _langRootBox.Text = picked; };
        rootRow.Children.Add(browse);
        var auto = new Button { Content = "自动获取", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 5, 10, 5), ToolTip = "自动定位游戏目录并拼接 LimbusCompany_Data/lang" };
        auto.Click += (_, _) => AutoLocateLangRoot();
        rootRow.Children.Add(auto);
        panel.Children.Add(rootRow);

        _activeLang = new TextBlock { Margin = new Thickness(0, 8, 0, 0), Foreground = System.Windows.Media.Brushes.Gray, Text = "活动语言：—" };
        panel.Children.Add(_activeLang);

        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var generate = new Button { Content = "从目录差异生成补丁…", Padding = new Thickness(12, 6, 12, 6) };
        generate.Click += async (_, _) => await GenerateAsync();
        var apply = new Button { Content = "应用补丁到 lang 目录…", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(10, 0, 0, 0) };
        apply.Click += async (_, _) => await ApplyAsync();
        actions.Children.Add(generate);
        actions.Children.Add(apply);
        panel.Children.Add(actions);

        _report = new ListBox { Height = 260, Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(_report);
        _status = new TextBlock { Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Gray };
        panel.Children.Add(_status);
        Content = panel;
        RefreshActiveLanguage();
    }

    private bool? ShowDialog(Microsoft.Win32.CommonDialog dialog)
        => _ownerWindow is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();

    private void MessageBoxInfo(string message, string title, MessageBoxImage image = MessageBoxImage.Information)
    {
        if (_ownerWindow is { } owner) MessageBox.Show(owner, message, title, MessageBoxButton.OK, image);
        else MessageBox.Show(message, title, MessageBoxButton.OK, image);
    }

    private void AutoLocateLangRoot()
    {
        var lookup = GameDirectoryLocator.Scan(GameDirectoryLocator.DefaultCandidateRoots());
        if (!lookup.Found)
        {
            _status.Text = "自动定位失败：未找到 LimbusCompany.exe，请手动选择 lang 根目录。";
            return;
        }
        _langRootBox.Text = Path.Combine(lookup.GameDirectory!, "LimbusCompany_Data", "lang");
    }

    private void RefreshActiveLanguage()
    {
        try
        {
            var root = _langRootBox.Text;
            var lang = Directory.Exists(root) ? _service.ReadActiveLanguage(root) : null;
            _activeLang.Text = lang is null ? "活动语言：—（没有 config.json 或目录无效）" : $"活动语言：{lang}";
        }
        catch (Exception ex)
        {
            _activeLang.Text = $"活动语言：读取失败（{ex.Message}）";
        }
    }

    private async Task GenerateAsync()
    {
        var vanilla = PickFolder("选择原版 lang 目录（对比基准，建议用游戏目录的备份或官方解包）");
        if (vanilla is null) return;
        var modified = PickFolder("选择修改后的 lang 目录（以模组化结果为准）");
        if (modified is null) return;
        var save = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存文本补丁（放进模组目录即可被加载器应用）",
            Filter = "文本补丁 (*.json)|*.json",
            FileName = "lang-patch.json"
        };
        if (ShowDialog(save) != true) return;

        try
        {
            var (document, diagnostics) = await Task.Run(() => _service.GenerateFromDirectories(vanilla, modified));
            if (document.Patches.Count == 0)
            {
                _status.Text = "没有发现差异，未生成补丁。";
                Report(diagnostics.Select(x => $"⚠ {x}"));
                return;
            }
            var operationCount = document.Patches.Values.Sum(x => x.Count);
            await Task.Run(() => _service.Write(document, save.FileName));
            _report.Items.Clear();
            foreach (var (file, ops) in document.Patches)
                _report.Items.Add($"✅ {file}（{ops.Count} 条操作）");
            Report(diagnostics.Select(x => $"⚠ {x}"));
            _status.Text = $"已生成补丁 {save.FileName}：{document.Patches.Count} 个文件 / {operationCount} 条操作；诊断 {diagnostics.Count} 条。";
        }
        catch (Exception ex)
        {
            _status.Text = $"生成补丁失败: {ex.Message}";
        }
    }

    private async Task ApplyAsync()
    {
        var root = _langRootBox.Text;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            MessageBoxInfo("请先设置有效的 lang 根目录。", "应用补丁");
            return;
        }
        var open = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择文本补丁 JSON",
            Filter = "文本补丁 (*.json)|*.json|所有文件 (*.*)|*.*"
        };
        if (ShowDialog(open) != true) return;

        var confirm = _ownerWindow is { } owner
            ? MessageBox.Show(owner,
                "将把补丁逐文件就地写回 lang 目录（目标缺失的条目会跳过并报告）。\n" +
                "真实加载器在启动时会自动备份原文件并在退出时还原；直接应用前请自行确认 lang 目录可回滚。\n\n继续？",
                "应用补丁", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            : MessageBox.Show(
                "将把补丁逐文件就地写回 lang 目录（目标缺失的条目会跳过并报告）。\n" +
                "真实加载器在启动时会自动备份原文件并在退出时还原；直接应用前请自行确认 lang 目录可回滚。\n\n继续？",
                "应用补丁", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            var document = await Task.Run(() => _service.Read(open.FileName));
            var statuses = await Task.Run(() => _service.ApplyToDirectory(root, document));
            _report.Items.Clear();
            foreach (var status in statuses)
                _report.Items.Add($"{(status.Applied ? "✅" : "⚠")} {status.RelativePath}（{status.OperationCount} 条操作）{status.Note}");
            _status.Text = $"已应用 {statuses.Count(x => x.Applied)} / {statuses.Count} 个文件到 {root}";
            RefreshActiveLanguage();
        }
        catch (Exception ex)
        {
            _status.Text = $"应用补丁失败: {ex.Message}";
        }
    }

    private void Report(IEnumerable<string> lines)
    {
        foreach (var line in lines) _report.Items.Add(line);
    }

    private string? PickFolder(string title)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            CheckFileExists = false,
            ValidateNames = false,
            FileName = "选择此文件夹",
            Title = title
        };
        if (ShowDialog(dialog) != true) return null;
        var directory = Path.GetDirectoryName(dialog.FileName);
        return directory is not null && Directory.Exists(directory) ? directory : null;
    }
}
