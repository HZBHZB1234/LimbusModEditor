# 日志与报错收集（LOGGING）

> 目的：让**任何一次用户现场复现**都能在 `logs/` 里留下足够定位问题的证据 ——
> 谁在什么时候点了什么、哪一步慢、哪个异常被吞掉、UI 线程什么时候被堵住。
>
> 引擎：**NLog 6.2.0**（`nlog.config`）。铁律 §3-12：通用能力优先用成熟第三方库，不要自造。
> 本文是**唯一权威**的埋点约定；改代码前先读完。

---

## 1. 日志在哪

| 位置 | 内容 |
|---|---|
| `<程序目录>/logs/current.log` | **本次会话全量日志**（固定入口，每次都覆盖重开） |
| `<程序目录>/logs/errors.log` | 只收 Error/Fatal，**跨会话追加**（直接看这个最快定位报错） |
| `<程序目录>/logs/lme-session-*.log` | 上一次会话的 `current.log` 归档（启动时自动改名的结果） |
| `<程序目录>/logs/crash-*.log` | 崩溃快照：异常 + 最近 400 条日志（由 `App/Logging/LogHost.cs` 写） |

- 程序目录默认是 exe 所在目录（`artifacts/publish-win-x64/logs/`）；
  也可用环境变量 `LME_LOG_DIR` 指到任意目录（例如仓库根的 `logs/`）。
- 程序目录不可写时自动退到 `%TEMP%/LimbusModEditor-logs`，并在日志里记一条 Warn。
- **单元测试不落盘**：测试宿主不调用 `NLogBootstrap.Initialize`，NLog 没有 target，
  记录直接丢弃 —— 测试不会污染 `logs/`，也不需要清理。

每行固定列（便于肉眼扫与 grep）：

```
2026-09-12 23:29:01.1234 INFO  t 1 ExportAsync(ModPackExportService.cs:212) 导出开始：4 个槽位，目标 D:\out
```

---

## 2. 调用约定（两行样板）

```csharp
using NLog;

internal sealed class FooService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();   // ← 每个类一行

    public void DoWork(int count)
    {
        using var scope = Log.Scope("DoWork");          // 计时作用域（Domain/Diagnostics/LoggerExtensions.cs）
        Log.Info("开始处理：{0} 项", count);             // 位置占位符 = 惰性格式化
        Log.Error(ex, "处理失败：第 {0} 项", index);     // 异常必须带 ex（否则丢栈）
    }
}
```

要点：

- 字段名统一叫 `Log`（类型 `NLog.Logger`）；一个文件里有多个类/嵌套类，各自持有自己的字段。
- 文件/方法/行号由 NLog 的 `${callsite}` 自动带上，**不要**手写。
- 消息用**中文**；技术标识、路径、枚举名用原文。
- 每条消息要带**能定位问题的具体值**（数量、路径、槽位名、字节数、耗时），
  不要写「出错了」「开始处理」这种没有信息量的日志。

### 级别怎么选

| 级别 | 什么时候用 | 例子 |
|---|---|---|
| `Trace` | 逐条目细节，默认关闭 | 每个 bundle/每个对象一行 |
| `Debug` | 阶段边界、外部 IO、耗时（**默认级别**，会写文件） | `读 bundle 成功：3.4 MB / 120 ms` |
| `Info` | 用户可见的关键事件 | `导出完成：4 个槽位 → 12 个文件` |
| `Warn` | 可疑但不致命：回退、降级、被吞掉的异常、慢操作 | `找不到索引，改用全量扫描` |
| `Error` | 失败（异常被拦截），进 `errors.log` | `Log.Error(ex, "写产物失败：{0}", path)` |
| `Fatal` | 致命 / 未处理异常（一般由全局钩子记） | — |

### 三个便利方法（NLog 没有，所以放在 `Domain/Diagnostics/LoggerExtensions.cs`）

```csharp
using var scope = Log.Scope("导出 lang");      // 进入 Trace、离开 Debug；超过 5 s 升级为 Warn（"慢操作"）
Log.Guard("刷新资源列表", () => { ... });       // 拦截异常 + 记完整栈；**只能替换已存在的 try/catch**
Log.Every(i, 5000, LogLevel.Info, () => $"已扫描 {i} 条");   // 采样：第 1 次 + 每 5000 次一条
```

> `Guard` 的定位：**把已经吞掉的异常记下来**。不要用它新引入拦截 —— 那会改变程序行为。

---

## 3. 硬性纪律

1. **只加日志，不改行为**：不改签名/返回值/控制流；不重构；不格式化既有代码；不动 `.csproj`。
2. **既有 `catch` 的语义原样保留**：原来是吞掉就继续吞，只是现在留一行日志；原来 rethrow 就保持 rethrow。
3. **热路径不许拼字符串**（插值、`string.Format`、`+` 都算）。每秒上千次的循环里：
   ```csharp
   if (Log.IsTraceEnabled) Log.Trace("bundle {0} → {1} 个对象", path, count);   // 守卫
   Log.Every(i, 5000, LogLevel.Info, () => $"已扫描 {i} 条");                     // 采样
   ```
   判断标准：这条日志在**正常一次运行**里会出现多少次？> 10 万次就必须守卫或采样。
4. **不记大块内容**：文本表/JSON 全文、二进制、hex dump 只记长度、条数或前若干字符。
   单条消息控制在几百字符内。
5. **`TryParse` / 反射 / P/Invoke / 磁盘写入失败必须留痕**（`Warn` 或 `Error`），
   这类"静默失败"正是历史上最难查的 bug。
6. `TreatWarningsAsErrors=true`：新代码零警告。注意可空性 —— 可能为 `null` 的值
   先 `?? "-"` 再作为 NLog 参数传递（`params object[]` 收到 `object?` 会报 CS8604）。
7. 不要引入第二个日志库，也不要在业务代码里直接 `Console.WriteLine`（CLI 的用户可见输出除外）。

---

## 4. 该在哪里埋点（按价值排序）

| 位置 | 级别 | 必须带上 |
|---|---|---|
| 公开入口 / UI 事件处理器开头 | `Info` + `Log.Scope` | 用户动作、关键入参 |
| 阶段边界（开始/结束） | `Info`/`Debug` | 条目数、字节数、耗时 |
| 外部 IO、编解码、压缩、进程调用 | `Debug` | 路径/大小/耗时/成败 |
| 回退 / 降级 / 兜底分支 | `Warn` | 原本想要什么、实际用了什么 |
| 所有 `catch` | `Error`（无害的用 `Debug`） | `ex` + 上下文值 |
| 十万级以上的循环 | `Trace` 守卫 或 `Every` | 进度计数 |

**不要**给每条语句加日志：日志的价值在于「事后能重建现场」，不在于数量。

---

## 5. 运行中调级别（不用重启）

按优先级：

1. 改 `nlog.config`（`autoReload="true"`，保存即生效）：把 `<logger name="*" minlevel="Trace" writeTo="sessionAsync" />`
   的 `minlevel` 改成 `Trace`/`Info` 即可。**用户现场排查首选这个。**
2. 启动前设环境变量：
   - `LME_LOG_LEVEL=trace|debug|info|warn|error`（默认 `debug`）
   - `LME_LOG_DIR=<目录>`、`LME_LOG_OFF=1`（完全关闭）、`LME_LOG_CONSOLE=1`（同时输出控制台）
3. `internalLogLevel="Off"` 改成 `Warn`（并把 `internalLogFile` 指到 `logs/nlog-internal.log`）
   可以看 NLog 自身的故障。

---

## 6. 怎么用这些日志定位「未响应」

- 日志里带 `t<线程号>`：UI 线程是**创建窗口的那个线程**（心跳启动时会记一行 `UI 心跳启动（UI 线程 t…）`）。
- `App/Logging/UiHeartbeat.cs` 每秒一跳：
  - 正常时每 10 秒一条 `DEBUG UI 心跳 #N（近 10 秒最大间隔 … ms）`；
  - **UI 线程被堵住 ≥ 2 秒会立刻记一条 `WARN UI 线程被阻塞约 xxx ms`** —— 这条之后
    如果还有工作线程的日志，说明「界面卡死但后台还在跑」。
- 崩溃（含 UI 线程未处理异常）会额外落 `logs/crash-*.log`，里面是异常 + 最近 400 条日志，
  可以直接定位到「最后一条正常日志」和「第一个异常」。

---

## 7. 维护约定

- 新增/删除源文件后，同步更新 `docs/PROJECT-INDEX.md`（铁律 §3-11）。
- 日志相关的三个"非第三方"文件，改动前先读本文件的 §2/§3：
  `Domain/Diagnostics/LoggerExtensions.cs`（调用便利方法）、
  `Application/Diagnostics/NLogBootstrap.cs`（目录判定/配置装配/内存快照）、
  `App/Logging/LogHost.cs` + `App/Logging/UiHeartbeat.cs`（全局异常钩子 + UI 心跳）。
- 日志配置的**唯一来源**是仓库根的 `nlog.config`：它同时内嵌进 `LimbusModEditor.Application`
  （输出目录被删也能自动还原），App/Cli 两个可执行项目都以链接方式复制它到输出。
