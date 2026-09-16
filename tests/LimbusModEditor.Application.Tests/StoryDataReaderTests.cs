using LimbusModEditor.Application.Relations;

namespace LimbusModEditor.Application.Tests;

/// <summary>
/// <see cref="StoryDataReader"/> 的测试：守住「剧情正文从哪来、章节键怎么定」这两件事。
///
/// <para>真实格式是 <c>dataList[].content</c>（不是早期文档里写的 <c>dialog</c>），
/// 真实文件名是 <c>S001A.json</c> / <c>1D101B.json</c> / <c>ES001B.json</c> 这类
/// （最后一个数字之前是章节键，之后是分部标记）——这两条一旦被改回去，
/// 剧情页会整片变空，所以必须有测试盯着。</para>
/// </summary>
public sealed class StoryDataReaderTests : IDisposable
{
    private readonly string _langDir;

    public StoryDataReaderTests()
    {
        _langDir = Path.Combine(Path.GetTempPath(), "lme-storydata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_langDir, StoryDataReader.StoryDirectory));
    }

    public void Dispose()
    {
        try { Directory.Delete(_langDir, true); } catch (Exception) { /* 临时目录 */ }
    }

    private void WriteStory(string fileName, string json)
    {
        var path = Path.Combine(_langDir, StoryDataReader.StoryDirectory, fileName);
        File.WriteAllText(path, json);
    }

    [Fact]
    public void Reads_dataList_content_as_dialog_body()
    {
        WriteStory("S001A.json", """
            {"dataList":[
              {"id":0,"place":"巴士","model":"???","teller":"浮士德","title":"","content":"我本就没抱什么期望。"},
              {"id":1,"place":"巴士","model":"???","teller":"","title":"","content":"……"}
            ]}
            """);

        var lines = StoryDataReader.Read(_langDir, "StoryData/S001A.json");

        Assert.Equal(2, lines.Count);
        Assert.Equal("我本就没抱什么期望。", lines[0].Content);
        Assert.Equal("浮士德", lines[0].Teller);
        Assert.Equal("巴士", lines[0].Place);
        Assert.Equal(0, lines[0].Id);
        Assert.Equal("……", lines[1].Content);
        Assert.Null(lines[1].Teller);
    }

    [Fact]
    public void Chapter_key_comes_from_the_part_before_the_last_digit()
    {
        Assert.Equal("S001", StoryDataReader.ChapterKeyOf("StoryData/S001A.json"));
        Assert.Equal("S001", StoryDataReader.ChapterKeyOf("StoryData/S001B.json"));
        Assert.Equal("1D101", StoryDataReader.ChapterKeyOf("StoryData/1D101B.json"));
        Assert.Equal("ES001", StoryDataReader.ChapterKeyOf("StoryData/ES001B.json"));
        // 没有分部标记时整体当键
        Assert.Equal("P01011", StoryDataReader.ChapterKeyOf("StoryData/P01011.json"));
    }

    [Fact]
    public void Missing_or_broken_file_yields_no_lines()
    {
        Assert.Empty(StoryDataReader.Read(_langDir, "StoryData/不存在.json"));
        Assert.Empty(StoryDataReader.Read(null, "StoryData/S001A.json"));
        Assert.Empty(StoryDataReader.Read(_langDir, " "));

        WriteStory("Bad.json", "{ 这不是 JSON");
        Assert.Empty(StoryDataReader.Read(_langDir, "StoryData/Bad.json"));
    }

    [Fact]
    public void File_without_dataList_yields_no_lines()
    {
        WriteStory("NoList.json", """{"dialog":[{"content":"旧口径"}]}""");
        Assert.Empty(StoryDataReader.Read(_langDir, "StoryData/NoList.json"));
    }
}
