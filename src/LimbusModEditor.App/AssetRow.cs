using LimbusModEditor.Application.Assets;
using LimbusModEditor.Domain.Assets;

namespace LimbusModEditor.App;

/// <summary>资源列表的行视图模型：把技术性的 AssetRecord 换算成用户可读的
/// 「名称（m_Container 友好名）/ 类型中文 / 状态中文 / 显示路径」。
/// 列表直接绑定本类型；<see cref="Asset"/> 供选中逻辑还原底层记录。</summary>
public sealed class AssetRow
{
    public AssetRow(AssetRecord asset) => Asset = asset;

    public AssetRecord Asset { get; }

    public Guid AssetId => Asset.AssetId;

    /// <summary>叶子名（m_Container 最后一段 / 导入路径文件名 / 类型兜底名）。</summary>
    public string Name => AssetDisplay.DisplayName(Asset);

    /// <summary>完整显示路径（文件夹 + 名称），列表行 ToolTip 用。</summary>
    public string DisplayPath => AssetDisplay.DisplayPath(Asset);

    public string TypeLabel => AssetDisplay.TypeLabel(Asset.Type);

    public string StateLabel => AssetDisplay.StateLabel(Asset.EditState);

    public AssetEditState State => Asset.EditState;

    public long Size => Asset.Size;
}
