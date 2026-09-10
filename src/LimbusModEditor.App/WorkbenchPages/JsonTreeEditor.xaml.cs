using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using LimbusModEditor.Application.Documents;

namespace LimbusModEditor.App;

/// <summary>一次文档变更的快照（宿主据此决定怎么存编辑集、怎么导出）。</summary>
/// <param name="JsonText">当前（含修改的）完整 JSON 文本。</param>
/// <param name="IsModified">相对本次载入是否已有修改。</param>
/// <param name="DiffOperationCount">与基线（官方版本；未提供时为载入文本）的 RFC6902 op 数。</param>
public sealed record JsonDocumentChangedEventArgs(string JsonText, bool IsModified, int DiffOperationCount);

/// <summary>
/// plan-09：共享 JSON 编辑器控件（plan-10 文本工作台 / plan-12 静态工作台共用，
/// 杜绝第二份实现）。
///
/// <para>能力：对象/数组/叶子树（<b>按层惰性展开</b>，替代旧实现 5000 行硬截断）、
/// 值编辑（按原值类型写回：字符串/整数/浮点/布尔/null；类型不匹配给中文错误）、
/// 删除键（确认框）、原始 JSON 双 Tab（树 / 原文，非法 JSON 拒绝且不改编辑集）、
/// 与官方版本的差异摘要（<see cref="LimbusModEditor.Application.Texts.TextDiffService"/>
/// 生成的 RFC6902 op 数，供 plan-12 的 diff Tab 复用）。</para>
///
/// <para><b>本控件不写任何文件、不管导出</b>：它通过 <see cref="DocumentChanged"/>
/// 把「当前修改后的 JSON 文本」「是否有修改」交给宿主页面，宿主自己决定怎么存编辑集与导出。
/// 一切判定委托给纯逻辑内核 <see cref="JsonDocumentEditor"/>（无 WPF 依赖，可单测）。</para>
/// </summary>
public partial class JsonTreeEditor : UserControl
{
    /// <summary>惰性展开占位项（Tag 为 null 的 TreeViewItem 即占位；真实行的 Tag 恒为 JsonEditRow）。</summary>
    private const string PlaceholderHeader = "载入中…";

    /// <summary>自动计算差异摘要的文档体量上限（两侧文本字符数之和）。超过就改成按需计算，
    /// 避免 MB 级静态表在每次编辑时全量走一遍 <c>TextDiffService</c> 冻住 UI。</summary>
    private const int DiffAutoComputeMaxChars = 1_000_000;

    private JsonNode? _document;
    private JsonEditRow? _selected;
    private string _loadedText = string.Empty;
    private string? _vanillaText;
    private bool _modified;

    public JsonTreeEditor()
    {
        InitializeComponent();
    }

    /// <summary>文档变更（值写回 / 删除键 / 保存原文）。载入文档时也会触发一次（IsModified=false）。</summary>
    public event EventHandler<JsonDocumentChangedEventArgs>? DocumentChanged;

    /// <summary>控件内部状态文案（宿主可转发到工作台状态条）。</summary>
    public event EventHandler<string>? StatusMessage;

    /// <summary>当前（含修改的）完整 JSON 文本；无文档时为空串。</summary>
    public string CurrentJsonText { get; private set; } = string.Empty;

    /// <summary>相对本次载入是否已有修改。</summary>
    public bool IsModified => _modified;

    /// <summary>是否已载入文档。</summary>
    public bool HasDocument => _document is not null;

    /// <summary>与基线（官方版本；未提供时为载入文本）的 RFC6902 op 数。</summary>
    public int DiffOperationCount { get; private set; }

    /// <summary>差异摘要的中文文案（plan-12 的 diff Tab 可直接显示）。</summary>
    public string DiffSummary => DiffInfo.Text;

    /// <summary>当前选中的树行。</summary>
    public JsonEditRow? SelectedRow => _selected;

    /// <summary>载入时的原文（宿主可用来做「还原」）。</summary>
    public string LoadedJsonText => _loadedText;

    /// <summary>与官方版本比较的基线文本（未提供时为 null）。</summary>
    public string? VanillaJsonText => _vanillaText;

    /// <summary>载入文档。<paramref name="jsonText"/> 必须是合法 JSON 且根不是 null，
    /// 否则抛 <see cref="InvalidDataException"/>（中文原因）且控件保持原状态。
    /// <paramref name="vanillaJsonText"/> 给「与官方版本差异」用的基线（plan-12 传官方原文）。</summary>
    public void LoadDocument(string jsonText, string? vanillaJsonText = null)
    {
        var document = JsonDocumentEditor.ParseStrict(jsonText);
        _document = document;
        _loadedText = jsonText;
        CurrentJsonText = jsonText;
        _vanillaText = vanillaJsonText;
        _modified = false;

        RawBox.Text = jsonText;
        Tabs.SelectedIndex = 0;
        RebuildTree();
        UpdateDiffSummary();
        var nodes = JsonDocumentEditor.CountNodes(document);
        var suffix = nodes >= 20000 ? "（至少这么多）" : string.Empty;
        SetStatus($"已载入文档（{nodes}{suffix} 个节点；树按需展开，不做全量物化）。");
        RaiseDocumentChanged();
    }

    /// <summary>清空（切换选中项 / 关闭页面时用）。</summary>
    public void Clear()
    {
        _document = null;
        _selected = null;
        _loadedText = string.Empty;
        _vanillaText = null;
        CurrentJsonText = string.Empty;
        _modified = false;
        DiffOperationCount = 0;
        Tree.Items.Clear();
        ValueBox.Text = string.Empty;
        RawBox.Text = string.Empty;
        DiffInfo.Text = "—";
        SetStatus("—");
        RefreshButtons();
    }

    /// <summary>定位并选中某个键路径（展平口径，如 <c>dataList/0/dialog</c>），
    /// 沿途逐级展开。路径不存在时返回 false。</summary>
    public bool SelectPath(string path)
    {
        if (_document is null) return false;
        var item = ExpandChain(path);
        if (item?.Tag is not JsonEditRow row || row.Path != path) return false;
        item.IsSelected = true;
        item.BringIntoView();
        return true;
    }

    // ── 树构建（惰性展开）────────────────────────────────────────────

    private void RebuildTree()
    {
        Tree.Items.Clear();
        _selected = null;
        ValueBox.Text = string.Empty;
        if (_document is null)
        {
            RefreshButtons();
            return;
        }
        var root = CreateItem(JsonDocumentEditor.CreateRoot(_document));
        Tree.Items.Add(root);
        root.IsExpanded = true; // 默认展开第一层（与资源工作台/旧键值表的默认口径一致）
        RefreshButtons();
    }

    private TreeViewItem CreateItem(JsonEditRow row)
    {
        var item = new TreeViewItem
        {
            Header = BuildHeader(row),
            Tag = row,
            Style = WorkbenchShell.CreateTreeItemStyle(),
        };
        if (row.IsContainer && row.ChildCount > 0) item.Items.Add(CreatePlaceholder());
        item.Expanded += TreeItem_Expanded;
        return item;
    }

    private static TreeViewItem CreatePlaceholder()
        => new() { Header = PlaceholderHeader, Tag = null };

    private static bool IsPlaceholder(TreeViewItem item)
        => item.Items.Count == 1 && item.Items[0] is TreeViewItem { Tag: null };

    private static string BuildHeader(JsonEditRow row)
        => row.IsContainer ? $"{row.Name}   {row.Preview}" : $"{row.Name}: {row.Preview}";

    private void TreeItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem item) MaterializeChildren(item);
    }

    /// <summary>把占位项换成真实子项（只在展开时生成这一层）。</summary>
    private void MaterializeChildren(TreeViewItem item)
    {
        if (item.Tag is not JsonEditRow row || !IsPlaceholder(item)) return;
        item.Items.Clear();
        foreach (var child in JsonDocumentEditor.EnumerateChildren(row))
            item.Items.Add(CreateItem(child));
    }

    /// <summary>逐级展开到指定路径并返回该项（用于编辑后保持定位）。</summary>
    private TreeViewItem? ExpandChain(string path)
    {
        if (Tree.Items.Count == 0 || Tree.Items[0] is not TreeViewItem root) return null;
        if (path.Length == 0) return root;

        var current = root;
        var accumulated = string.Empty;
        foreach (var token in path.Split('/'))
        {
            accumulated = accumulated.Length == 0
                ? token
                : $"{accumulated}/{token}";
            if (accumulated.Length == 0) continue; // 根节点（路径为空）
            MaterializeChildren(current);
            var next = FindChild(current, accumulated);
            if (next is null) return current;
            next.IsExpanded = true; // 触发 Expanded → 惰性生成下一层
            current = next;
        }
        return current;
    }

    private static TreeViewItem? FindChild(TreeViewItem parent, string path)
    {
        foreach (var entry in parent.Items)
        {
            if (entry is TreeViewItem item && item.Tag is JsonEditRow row && row.Path == path) return item;
        }
        return null;
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _selected = (e.NewValue as TreeViewItem)?.Tag as JsonEditRow;
        // 容器行只显示短预览（InitialEditorText），绝不把整棵子树塞进文本框。
        ValueBox.Text = _selected is null ? string.Empty : JsonDocumentEditor.InitialEditorText(_selected);
        ValueLabel.Text = _selected is null
            ? "选中键的值"
            : $"选中键的值（{_selected.Path}·{KindLabel(_selected.Kind)}）";
        RefreshButtons();
    }

    private static string KindLabel(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "对象",
        JsonValueKind.Array => "数组",
        JsonValueKind.String => "字符串",
        JsonValueKind.Integer => "整数",
        JsonValueKind.Number => "浮点",
        JsonValueKind.Boolean => "布尔",
        _ => "null",
    };

    private void ExpandRoot_Click(object sender, RoutedEventArgs e)
    {
        if (Tree.Items.Count > 0 && Tree.Items[0] is TreeViewItem root) root.IsExpanded = true;
    }

    // ── 值编辑 / 删除键 ─────────────────────────────────────────────

    private void ApplyValue_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _document is null) return;
        if (!_selected.IsLeaf)
        {
            SetStatus("只能编辑叶子值（字符串/整数/浮点/布尔/null）；数组与对象请用「原文 JSON」页编辑。");
            return;
        }
        var path = _selected.Path;
        try
        {
            var text = JsonDocumentEditor.SetLeafText(_selected, ValueBox.Text, _document);
            AdoptText(text, isModified: true, focusPath: path);
            SetStatus($"已修改键 {path}（未写盘；宿主导出时才落地）。");
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            SetStatus($"应用修改失败：{ex.Message}");
        }
    }

    private void DeleteKey_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _document is null || _selected.Parent is null) return;
        var path = _selected.Path;
        var confirm = MessageBox.Show(Window.GetWindow(this),
            $"删除键 {path}？\n\n删除会以 RFC6902 remove 操作进入补丁（游戏加载器按补丁回放）。",
            "删除键", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        if (!JsonDocumentEditor.RemoveNode(_selected, out var error))
        {
            SetStatus($"删除失败：{error}");
            return;
        }
        var parentPath = ParentPathOf(path);
        AdoptText(JsonDocumentEditor.Serialize(_document), isModified: true, focusPath: parentPath);
        SetStatus($"已删除键 {path}（未写盘；宿主导出时才落地）。");
    }

    /// <summary>删除后要重新定位到的父路径（把最后一段去掉）。</summary>
    private static string ParentPathOf(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? string.Empty : path[..index];
    }

    // ── 原文 JSON ───────────────────────────────────────────────────

    private void SaveRaw_Click(object sender, RoutedEventArgs e)
    {
        if (!JsonDocumentEditor.TryParse(RawBox.Text, out var document, out var error))
        {
            SetStatus($"原始 JSON 非法，已拒绝（{error}）；当前文档与宿主编辑集都未改动。");
            return;
        }
        // 与旧实现一致：进宿主的文本是用户输入的那份原文（不重新排版）。
        var text = RawBox.Text;
        _document = document;
        CurrentJsonText = text;
        _modified = true;
        RebuildTree();
        UpdateDiffSummary();
        SetStatus("原始 JSON 修改已进入宿主编辑集。");
        RaiseDocumentChanged();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 切到「原文 JSON」时把当前文本同步过去（树侧改动立刻可见）；切回树视图不覆盖用户输入。
        if (e.Source is TabControl && Tabs.SelectedIndex == 1 && _document is not null && RawBox.Text != CurrentJsonText)
            RawBox.Text = CurrentJsonText;
    }

    // ── 内部状态同步 ─────────────────────────────────────────────────

    /// <summary>用新的 JSON 文本替换当前文档（改动来自树侧），重建树并把选中定位回
    /// <paramref name="focusPath"/>（逐级展开）。</summary>
    private void AdoptText(string jsonText, bool isModified, string focusPath)
    {
        var document = JsonDocumentEditor.ParseStrict(jsonText);
        _document = document;
        CurrentJsonText = jsonText;
        _modified = isModified;
        RawBox.Text = jsonText;
        RebuildTree();
        UpdateDiffSummary();
        if (focusPath.Length > 0) SelectPath(focusPath);
        else if (Tree.Items.Count > 0 && Tree.Items[0] is TreeViewItem root) root.IsSelected = true;
        RaiseDocumentChanged();
    }

    private void UpdateDiffSummary(bool force = false)
    {
        var baseline = _vanillaText ?? _loadedText;
        if (string.IsNullOrWhiteSpace(baseline) || string.IsNullOrWhiteSpace(CurrentJsonText))
        {
            DiffOperationCount = 0;
            DiffInfo.Text = "—";
            return;
        }
        // 超大文档不在每次编辑时全量算差异（TextDiffService 会走完整棵树，MB 级表会明显掉帧）：
        // 只有小文档自动算，大文档由宿主/页面按需调用 RefreshDiffSummary(force: true)。
        if (!force && (long)baseline.Length + CurrentJsonText.Length > DiffAutoComputeMaxChars)
        {
            DiffOperationCount = 0;
            DiffInfo.Text = $"（文档较大：差异摘要按需计算，可点「计算差异」或在本页 diff 视图查看）";
            return;
        }
        try
        {
            DiffOperationCount = JsonDocumentEditor.CountDiffOperations(baseline, CurrentJsonText);
        }
        catch (InvalidDataException)
        {
            DiffOperationCount = 0;
            DiffInfo.Text = "（差异无法计算：JSON 非法）";
            return;
        }
        DiffInfo.Text = _vanillaText is null
            ? DiffOperationCount == 0 ? "与载入时无差异。" : $"本次修改：{DiffOperationCount} 个 RFC6902 操作。"
            : JsonDocumentEditor.DescribeDiff(baseline, CurrentJsonText);
    }

    /// <summary>按需（重新）计算差异摘要：超大文档由页面/宿主显式触发（plan-12 的 diff Tab）。</summary>
    public void RefreshDiffSummary(bool force = true) => UpdateDiffSummary(force);

    private void RefreshButtons()
    {
        ApplyValueButton.IsEnabled = _selected is { IsLeaf: true };
        DeleteKeyButton.IsEnabled = _selected is { Parent: not null };
        ExpandAllButton.IsEnabled = _document is not null;
        SaveRawButton.IsEnabled = _document is not null;
    }

    private void SetStatus(string message)
    {
        StatusInfo.Text = string.IsNullOrWhiteSpace(message) ? "—" : message;
        StatusMessage?.Invoke(this, StatusInfo.Text);
    }

    private void RaiseDocumentChanged()
        => DocumentChanged?.Invoke(this, new JsonDocumentChangedEventArgs(CurrentJsonText, _modified, DiffOperationCount));
}
