using LimbusModEditor.Domain.Diagnostics;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace LimbusModEditor.Domain.Tests;

/// <summary>
/// 日志调用约定（<see cref="LoggerExtensions"/>）的行为测试。
///
/// <para>用**独立的 <see cref="LogFactory"/> + <see cref="MemoryTarget"/>**（不碰全局
/// <see cref="LogManager"/>），因此与其他测试并行安全，也不会往 <c>logs/</c> 写任何文件 ——
/// 这正是「测试宿主不落盘」这条设计的可验证部分。</para>
/// </summary>
public sealed class LoggingTests
{
    private const string TestLayout =
        "${level}|${message}${onexception:inner=|${exception:format=Type,Message,StackTrace}}";

    private static (LogFactory Factory, MemoryTarget Target) Create(LogLevel? minimum = null)
    {
        var factory = new LogFactory { AutoShutdown = false };
        var target = new MemoryTarget { Name = "memory", Layout = TestLayout, MaxLogsCount = 1000 };
        var configuration = new LoggingConfiguration();
        configuration.AddTarget(target);
        configuration.AddRule(minimum ?? LogLevel.Trace, LogLevel.Fatal, target, "*");
        factory.Configuration = configuration;
        return (factory, target);
    }

    private static string[] Lines(MemoryTarget target) => target.Logs.ToArray();

    [Fact]
    public void Scope_records_enter_and_completion_with_the_given_name()
    {
        var (factory, target) = Create();
        var logger = factory.GetLogger("scope");

        using (logger.Scope("导出 lang"))
        {
        }

        var lines = Lines(target);
        Assert.Contains(lines, line => line.Contains("▶ 导出 lang"));
        Assert.Contains(lines, line => line.Contains("⏹ 导出 lang 完成"));
        Assert.Contains(lines, line => line.Contains("ms"));
    }

    [Fact]
    public void Scope_escalates_to_warn_when_slower_than_the_threshold()
    {
        var (factory, target) = Create();
        var logger = factory.GetLogger("slow");

        using (logger.Scope("全量扫描", slowMs: 0))
        {
        }

        Assert.Contains(Lines(target), line => line.StartsWith("Warn|", StringComparison.Ordinal) && line.Contains("慢操作：全量扫描"));
    }

    [Fact]
    public void Scope_dispose_is_idempotent()
    {
        var (factory, target) = Create();
        var logger = factory.GetLogger("once");

        var scope = logger.Scope("只记一次", slowMs: int.MaxValue);
        scope.Dispose();
        scope.Dispose();

        Assert.Single(Lines(target), line => line.Contains("完成"));
    }

    [Fact]
    public void Guard_returns_true_and_writes_nothing_when_the_action_succeeds()
    {
        var (factory, target) = Create();
        var logger = factory.GetLogger("guard-ok");
        var ran = false;

        var ok = logger.Guard("刷新资源列表", () => { ran = true; });

        Assert.True(ok);
        Assert.True(ran);
        Assert.Empty(Lines(target));
    }

    [Fact]
    public void Guard_swallows_the_exception_and_records_type_message_and_stack()
    {
        var (factory, target) = Create();
        var logger = factory.GetLogger("guard-fail");

        var ok = logger.Guard("写出产物", () => throw new InvalidOperationException("boom"));

        Assert.False(ok);
        var record = Assert.Single(Lines(target));
        Assert.StartsWith("Error|", record, StringComparison.Ordinal);
        Assert.Contains("异常被拦截：写出产物", record);
        Assert.Contains("System.InvalidOperationException", record);
        Assert.Contains("boom", record);
        Assert.Contains("at ", record);
    }

    [Fact]
    public void Guard_generic_returns_fallback_and_records_the_exception()
    {
        var (factory, target) = Create();
        var logger = factory.GetLogger("guard-generic");

        var result = logger.Guard("读取配置", () => throw new IOException("磁盘出错"), fallback: 42);

        Assert.Equal(42, result);
        Assert.Contains(Lines(target), line => line.Contains("异常被拦截：读取配置") && line.Contains("System.IO.IOException"));
    }

    [Fact]
    public async Task Guard_async_treats_cancellation_as_information_not_error()
    {
        var (factory, target) = Create();
        var logger = factory.GetLogger("guard-cancel");

        var ok = await logger.GuardAsync("导出槽位", () => Task.FromException(new OperationCanceledException("已取消")));

        Assert.False(ok);
        var record = Assert.Single(Lines(target));
        Assert.StartsWith("Info|已取消：导出槽位", record, StringComparison.Ordinal);
        Assert.DoesNotContain("Error|", record);
    }

    [Fact]
    public void Every_logs_the_first_item_and_then_every_nth()
    {
        var (factory, target) = Create();
        var logger = factory.GetLogger("every");

        for (var i = 1; i <= 12; i++)
        {
            var index = i;
            logger.Every(index, 5, LogLevel.Info, () => $"第 {index} 条");
        }

        var messages = Lines(target).Select(line => line.Split('|')[1]).ToArray();
        Assert.Equal(new[] { "第 1 条", "第 5 条", "第 10 条" }, messages);
    }

    [Fact]
    public void Every_does_not_build_the_message_when_the_level_is_filtered_out()
    {
        var (factory, target) = Create(LogLevel.Warn);
        var logger = factory.GetLogger("every-filtered");
        var built = false;

        for (var i = 1; i <= 3; i++)
        {
            logger.Every(i, 1, LogLevel.Info, () => { built = true; return "不该构造"; });
        }

        Assert.False(built);
        Assert.Empty(Lines(target));
    }
}
