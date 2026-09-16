namespace LimbusModEditor.Application.Relations;

/// <summary>
/// 二级页拆分规则：决定什么时候一个分节要拆成独立的二级页。
///
/// <para><b>为什么需要这个</b>：当一个分节的资源过多时（例如语音超过 10 条、画廊超过 20 张），
/// 把所有内容塞在一个页面里会导致加载缓慢且难以导航。拆分规则把这些经验值集中定义，
/// 避免在编排引擎里散落大量的魔法数字。</para>
///
/// <para><b>命名法</b>：参考 docs/WIKI-PAGE-LAYOUT.md 的斜杠命名约定。
/// 例如「语音/战斗语音」、「语音/移动」、「画廊/立绘」、「画廊/CG」。</para>
/// </summary>
public static class WikiSecondaryPageRules
{
    /// <summary>语音分节的拆分阈值：超过此数量拆分为多个二级页。</summary>
    public const int AudioSplitThreshold = 10;

    /// <summary>图像分节的拆分阈值：超过此数量拆分为多个二级页。</summary>
    public const int ImageSplitThreshold = 20;

    /// <summary>文本分节的拆分阈值：超过此数量拆分为多个二级页。</summary>
    public const int TextSplitThreshold = 15;

    /// <summary>其它资源分节的拆分阈值：超过此数量拆分为多个二级页。</summary>
    public const int OtherSplitThreshold = 10;

    /// <summary>
    /// 判断指定分节是否需要拆分。
    /// <param name="sectionId">分节标识（<see cref="WikiSectionDefinition.SectionId"/>）。</param>
    /// <param name="itemCount">该分节的资源数量。</param>
    /// </summary>
    public static bool NeedsSplit(string sectionId, int itemCount) => sectionId switch
    {
        WikiSectionTables.Audio => itemCount > AudioSplitThreshold,
        WikiSectionTables.Images => itemCount > ImageSplitThreshold,
        WikiSectionTables.Text => itemCount > TextSplitThreshold,
        WikiSectionTables.OtherResources => itemCount > OtherSplitThreshold,
        _ => false,
    };

    /// <summary>
    /// 获取拆分后的二级页命名前缀。
    /// <param name="sectionId">分节标识。</param>
    /// <param name="index">拆分索引（从 0 开始）。</param>
    /// </summary>
    public static string GetSplitPageName(string sectionId, int index)
    {
        var baseName = sectionId switch
        {
            WikiSectionTables.Audio => "语音",
            WikiSectionTables.Images => "画廊",
            WikiSectionTables.Text => "文本",
            WikiSectionTables.OtherResources => "其它资源",
            _ => sectionId,
        };
        return index == 0 ? baseName : $"{baseName}/{index + 1}";
    }

    /// <summary>
    /// 按容器路径前缀拆分图像资源。
    /// <para>真实数据里图像资源分散在多个目录下（头像/CG/缩略图/皮肤预览），
/// 按前缀拆分可以让同一页内的图片类型一致。</para>
    /// </summary>
    /// <param name="containerEntry">资源容器路径。</param>
    /// <returns>分组键（用于拆分二级页）。</returns>
    public static string GetImageGroupKey(string? containerEntry)
    {
        if (string.IsNullOrEmpty(containerEntry)) return "other";
        if (containerEntry.Contains("/Sprite/Unit/Profile/", StringComparison.OrdinalIgnoreCase)) return "profile";
        if (containerEntry.Contains("/Sprite/UnitCgThumbnail/", StringComparison.OrdinalIgnoreCase)) return "thumbnail";
        if (containerEntry.Contains("/Sprite/Unit/CG/", StringComparison.OrdinalIgnoreCase)) return "cg";
        if (containerEntry.Contains("/Sprite/Unit/Info/", StringComparison.OrdinalIgnoreCase)) return "info";
        if (containerEntry.Contains("/Sprite/SkinPreview/", StringComparison.OrdinalIgnoreCase)) return "skin_preview";
        if (containerEntry.Contains("/UserInfoSuppotPortrait/", StringComparison.OrdinalIgnoreCase)) return "support_portrait";
        if (containerEntry.Contains("/Sprite/", StringComparison.OrdinalIgnoreCase)) return "sprite";
        return "texture";
    }

    /// <summary>
    /// 按文件名模式拆分语音资源。
    /// <para>语音文件通常按场景分类：战斗(battle)、移动(move)、技能(skill)等。
    /// 按文件名前缀拆分可以让同一页内的语音类型一致。</para>
    /// </summary>
    /// <param name="sampleName">音频样本名。</param>
    /// <returns>分组键（用于拆分二级页）。</returns>
    public static string GetAudioGroupKey(string? sampleName)
    {
        if (string.IsNullOrEmpty(sampleName)) return "other";
        if (sampleName.Contains("battle_", StringComparison.OrdinalIgnoreCase)) return "battle";
        if (sampleName.Contains("move_", StringComparison.OrdinalIgnoreCase)) return "move";
        if (sampleName.Contains("skill_", StringComparison.OrdinalIgnoreCase)) return "skill";
        if (sampleName.Contains("die_", StringComparison.OrdinalIgnoreCase) || sampleName.Contains("death_", StringComparison.OrdinalIgnoreCase)) return "death";
        if (sampleName.Contains("lobby_", StringComparison.OrdinalIgnoreCase)) return "lobby";
        if (sampleName.Contains("event_", StringComparison.OrdinalIgnoreCase)) return "event";
        return "other";
    }
}
