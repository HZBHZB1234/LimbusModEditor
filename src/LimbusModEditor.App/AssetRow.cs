using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.App;

/// <summary>资源列表的行视图模型：把技术性的 AssetRecord 换算成用户可读的
/// 「名称（m_Container 友好名）/ 类型中文 / 状态中文 / 显示路径」。
/// 列表直接绑定本类型；<see cref="Asset"/> 供选中逻辑还原底层记录。</summary>
public sealed class AssetRow
{
    // 显示路径/名称/类型标签只取决于扫描期写入的元数据，不会随编辑变化；
    // 列表虚拟化下每次滚动重绑都会重读这几个属性，惰性缓存一次即可。
    // 「状态标签」不缓存 —— 它随编辑状态实时变化。
    private string? _name;
    private string? _displayPath;
    private string? _typeLabel;

    public AssetRow(AssetRecord asset) => Asset = asset;

    public AssetRecord Asset { get; }

    public Guid AssetId => Asset.AssetId;

    /// <summary>叶子名（m_Container 最后一段 / 导入路径文件名 / 类型兜底名）。</summary>
    public string Name => _name ??= AssetDisplay.DisplayName(Asset);

    /// <summary>完整显示路径（文件夹 + 名称），列表行 ToolTip 用。</summary>
    public string DisplayPath => _displayPath ??= AssetDisplay.DisplayPath(Asset);

    public string TypeLabel => _typeLabel ??= AssetDisplay.TypeLabel(Asset.Type);

    public string StateLabel => AssetDisplay.StateLabel(Asset.EditState);

    public AssetEditState State => Asset.EditState;

    public long Size => Asset.Size;
}
