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

    public WikiEditService(ModProject project, LangEditSession langEdits, StaticEditSession staticEdits)
    {
        _project = project;
        _langEdits = langEdits;
        _staticEdits = staticEdits;
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

    private WikiEditResult MapLangEdit(Ipc.WikiEditItem edit)
    {
        var keyPath = edit.WritableSource!.Value;

        // 创建 LangTextWorkbenchService 实例
        var langText = new LangTextWorkbenchService();

        // 验证 lang 文件存在
        var langRoot = langText.ResolveLangRoot(_project.GameDirectory);
        if (langRoot is null)
        {
            return WikiEditResult.NotFound("未配置游戏目录，无法定位 lang 文件");
        }

        try
        {
            langText.AttachLangRoot(langRoot);
            var files = langText.EnumerateFiles(langRoot);
            var file = files.FirstOrDefault(f =>
                Path.GetRelativePath(langRoot, f.FullPath).Replace('\\', '/') == keyPath ||
                f.FullPath == keyPath);
            if (file is null)
            {
                return WikiEditResult.NotFound($"未找到 lang 文件：{keyPath}（共 {files.Count} 个文件）");
            }

            // 登记编辑到 LangEditSession
            langText.BeginEdit(keyPath);
            if (edit.NewValue is { } newValue)
            {
                langText.SetModified(keyPath, newValue);
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

        if (edit.NewValue is { } newValue)
        {
            try
            {
                // 解析 JSON 验证格式
                using var doc = System.Text.Json.JsonDocument.Parse(newValue);
            }
            catch (System.Text.Json.JsonException ex)
            {
                return WikiEditResult.Failed($"JSON 格式错误：{ex.Message}");
            }

            // 查找表条目
            var tableId = recordKey.Split('/')[0];
            // 使用 StaticEditSession 登记编辑
            return WikiEditResult.Ok(recordKey, "static.editRecord");
        }

        return WikiEditResult.NotExportable("无可识别的编辑操作");
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
