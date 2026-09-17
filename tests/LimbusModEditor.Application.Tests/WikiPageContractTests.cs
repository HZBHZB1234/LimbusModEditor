using System.Linq;
using System.Reflection;
using LimbusModEditor.Application.Ipc;
using Xunit;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// 维基页面契约守护：后端 DTO 的形状 ↔ 前端 WikiEntityPage 在渲染的字段。
/// 目的：前端渲染了、后端却没有 → 页面上永远空白。这类"契约漂移"必须被测试钉死。
/// </summary>
public sealed class WikiPageContractTests
{
    private static readonly Assembly Application = typeof(WikiPageDto).Assembly;

    [Fact]
    public void Page_load_request_uses_pageId_field()
    {
        // 前端发 { pageId }：字段名必须与后端一致，否则查不到页面。
        Assert.Contains(typeof(WikiPageLoadRequest).GetProperties(), p => p.Name == "PageId");
        Assert.DoesNotContain(typeof(WikiPageLoadRequest).GetProperties(), p => p.Name == "Id");
    }

    [Fact]
    public void Page_response_has_no_extra_page_envelope()
    {
        // 历史上 HandleWikiPageLoad 返回 new WikiPageResponse(new WikiPageDto(...))（多包一层 page），
        // 前端直接把响应当页面对象用 → 整页空白。响应类型必须已删除。
        Assert.DoesNotContain(Application.GetTypes(), t => t.Name == "WikiPageResponse");
    }

    [Fact]
    public void Page_dto_carries_everything_the_page_renders()
    {
        var names = typeof(WikiPageDto).GetProperties().Select(p => p.Name).ToArray();

        // 前端 WikiEntityPage 渲染的字段：信息框 / 标签 / 画廊 / 封面 / 分节 / 相关页面
        Assert.Contains("Infobox", names);
        Assert.Contains("Tags", names);
        Assert.Contains("Gallery", names);
        Assert.Contains("Cover", names);
        Assert.Contains("Sections", names);
        Assert.Contains("RelatedPages", names);
    }

    [Fact]
    public void Section_dto_carries_entries_and_bindings()
    {
        var names = typeof(WikiSectionDto).GetProperties().Select(p => p.Name).ToArray();

        Assert.Contains("Entries", names);
        Assert.Contains("Bindings", names);
    }

    [Fact]
    public void Binding_dto_exposes_media_url_for_lme_data_host()
    {
        // 二进制走 lme.data 虚拟主机，禁止 base64；拿不到真实地址时为 null（前端降级、不占位）。
        var names = typeof(WikiBindingDto).GetProperties().Select(p => p.Name).ToArray();

        Assert.Contains("MediaUrl", names);
        Assert.Contains("RefKey", names);
        Assert.Contains("Kind", names);
        Assert.Contains("DurationSec", names);
    }
}
