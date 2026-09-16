using LimbusModEditor.Application.StaticMods;
using LimbusModEditor.Application.Texts;
using LimbusModEditor.Domain.Assets;
using LimbusModEditor.Domain.Projects;

namespace LimbusModEditor.Application.Relations;

/// <summary>
/// 维基编辑 → 可写出处映射服务（t47）。
/// 将维基侧的编辑按 WritableSource 映射进既有项目编辑集，复用既有导出管线。
/// </summary>
public sealed class WikiEditService
{
    private readonly ModProject _project;
    private readonly LangEditSession _langEdits;
    private readonly StaticEditSession _staticEdits;
    private readonly StaticTableIndexStore? _staticIndex;

    /// <param name="project">当前项目（提供游戏目录与资源集）。</param>
    /// <param name="langEdits">项目语言编辑集（lang 编辑真正落在这里）。</param>
    /// <param name="staticEdits">项目静态数据编辑集（静态编辑真正落在这里）。</param>
    /// <param name="staticIndex">静态表索引（静态编辑要查到表条目才登记得进去）。</param>
    public WikiEditService(
        ModProject project,
        LangEditSession langEdits,
        StaticEditSession staticEdits,
        StaticTableIndexStore? staticIndex = null)
    {
        _project = project;
        _langEdits = langEdits;
        _staticEdits = staticEdits;
        _staticIndex = staticIndex;
    }

    /// <summary>尝试将一条维基编辑映射到可写出处。返回是否可导出。</summary>
    public WikiEditResult TryMapToWritableSource(Ipc.WikiEditItem edit)
    {
        if (edit.WritableSource is null)
        {
            return WikiEditResult.NotExportable("该编辑没有可写出处（仅百科内容，不可导出为模组）");
        }

        return edit.WritableSource.Kind switch
        {
            "asset" => MapAssetEdit(edit),
            "lang" => MapLangEdit(edit),
            "static" => MapStaticEdit(edit),
            _ => WikiEditResult.NotExportable($"未知的可写出处类型：{edit.WritableSource.Kind}")
        };
    }

    private WikiEditResult MapAssetEdit(Ipc.WikiEditItem edit)
    {
        var containerPath = edit.WritableSource!.Value;
        var asset = _project.Assets.FirstOrDefault(a => a.LogicalPath == containerPath);
        if (asset is null)
        {
            return WikiEditResult.NotFound($"未找到资源：{containerPath}");
        }

        // 登记替换编辑（如果有替换文件）
        if (edit.ReplacementPath is { } replacementPath)
        {
            if (!File.Exists(replacementPath))
            {
                return WikiEditResult.NotFound($"替换文件不存在：{replacementPath}");
            }
            return WikiEditResult.Ok(asset.LogicalPath, "asset.edit.replacePayload");
        }

        // 字段编辑
        if (edit.NewValue is not null)
        {
            return WikiEditResult.Ok(asset.LogicalPath, "asset.edit.fieldEdit");
        }

        return WikiEditResult.NotExportable("无可识别的编辑操作");
    }

    /// <summary>
    /// lang 编辑：登记进项目的 <see cref="LangEditSession"/>（<b>真正落库</b>，导出时才带得出去）。
    ///
    /// <para><b>为什么 <c>NewValue</c> 必须是整份文件的 JSON</b>：
    /// <c>LangEditSession.SetModified</c> 的口径是「替换整个 lang 文件的文本」，
    /// 拿单个字段值写进去会把文件改成一行文本。所以这里先校验它是合法 JSON，
    /// 不合法就如实回「失败」，绝不存半个文件进去。</para>
    /// </summary>
    private WikiEditResult MapLangEdit(Ipc.WikiEditItem edit)
    {
        var keyPath = edit.WritableSource!.Value;

        var langText = new LangTextWorkbenchService();
        var langRoot = langText.ResolveLangRoot(_project.GameDirectory);
        if (langRoot is null)
            return WikiEditResult.NotFound("未配置游戏目录，无法定位 lang 文件");

        try
        {
            langText.AttachLangRoot(langRoot);
            IReadOnlyList<LangTextFileInfo> files = langText.EnumerateFiles(langRoot);
            var file = files.FirstOrDefault(f =>
                Path.GetRelativePath(langRoot, f.FullPath).Replace('\\', '/') == keyPath ||
                f.FullPath == keyPath);
            if (file is null)
                return WikiEditResult.NotFound($"未找到 lang 文件：{keyPath}（共 {files.Count} 个文件）");

            if (edit.NewValue is { } newValue)
            {
                if (!IsValidJson(newValue))
                    return WikiEditResult.Failed($"lang 编辑的 newValue 必须是整份文件的 JSON：{keyPath}");

                // 用项目编辑集登记（此前这里 new 了一个临时服务实例，改完就丢 = 假成功）
                _langEdits.AttachLangRoot(langRoot);
                _langEdits.BeginEdit(keyPath);
                _langEdits.SetModified(keyPath, newValue);
            }

            return WikiEditResult.Ok(keyPath, "lang.editEntry");
        }
        catch (Exception ex)
        {
            return WikiEditResult.Failed($"编辑登记失败：{ex.Message}");
        }
    }

    private WikiEditResult MapStaticEdit(Ipc.WikiEditItem edit)
    {
        var recordKey = edit.WritableSource!.Value;

        if (edit.NewValue is not { } newValue)
            return WikiEditResult.NotExportable("无可识别的编辑操作（静态表编辑需要 newValue）");

        if (!IsValidJson(newValue))
            return WikiEditResult.Failed($"JSON 格式错误：{recordKey}");

        if (_staticIndex is null)
            return WikiEditResult.NotFound("没有静态表索引，无法定位表条目（请先完成启动扫描）");

        IReadOnlyList<StaticTableEntry> entries;
        try
        {
            entries = _staticIndex.ReadEntries();
        }
        catch (Exception ex)
        {
            return WikiEditResult.Failed($"读静态表索引失败：{ex.Message}");
        }

        var entry = entries.FirstOrDefault(e =>
            string.Equals(e.ContainerEntry, recordKey, StringComparison.Ordinal) ||
            string.Equals(e.Name, recordKey, StringComparison.Ordinal));
        if (entry is null)
            return WikiEditResult.NotFound($"静态表索引里没有这条记录：{recordKey}（共 {entries.Count} 张表）");

        // 真正登记进项目静态编辑集（此前只做了 JSON 校验就回 Ok = 假成功）
        var official = _staticEdits.TryGetOfficialText(entry.Key) ?? string.Empty;
        _staticEdits.Set(entry.Key, entry, official, newValue);
        return WikiEditResult.Ok(entry.Key, "static.editRecord");
    }

    private static bool IsValidJson(string text)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(text);
            return document.RootElement.ValueKind is System.Text.Json.JsonValueKind.Object
                or System.Text.Json.JsonValueKind.Array;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}

/// <summary>维基编辑映射结果。</summary>
public sealed record WikiEditResult(
    bool CanExport,
    string? AssetId,
    string? Method,
    string? Reason)
{
    public static WikiEditResult Ok(string assetId, string method) =>
        new(true, assetId, method, null);

    public static WikiEditResult NotExportable(string reason) =>
        new(false, null, null, reason);

    public static WikiEditResult NotFound(string reason) =>
        new(false, null, null, reason);

    public static WikiEditResult Failed(string reason) =>
        new(false, null, null, reason);
}
