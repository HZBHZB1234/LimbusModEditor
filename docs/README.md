# 文档索引（docs/）

> 本目录只保留**长期有效**的文档：现状与约束、结构导航、逐文件索引、用户手册与验证证据。
> 逐轮执行计划（`plans/plan-*.md`）与交接计划（`NEXT-STEPS.md`）已在 2026-09-12 归档删除，
> 历史见 git（`git log --oneline`、`git show <commit>:docs/plans/<file>`）。

## 我应该读哪一篇

| 你的目标 | 读这篇 |
|---|---|
| **接手项目、想知道现在能做什么/下一步做什么/有哪些铁律** | `STATUS.md` |
| **只读代码就要建立全项目心智模型（分层、流程、不变量、改哪里）** | `CODE-STRUCTURE.md` |
| **按文件查职责、按症状定位要改的文件** | `PROJECT-INDEX.md` |
| **要加日志 / 用户报「未响应」要查日志** | `LOGGING.md` |
| 想知道界面怎么用、操作步骤、故障排查 | `USAGE.md` |
| 关心写回链路是否被真实验证过、严重缺陷的根因 | `REALDATA-VERIFY.md` |
| 关心自审发现、设计变更风险、性能量化 | `REVIEW.md` |
| 查看资源库 v2、启动分析与导出读取的真实性能对比及复现步骤 | `PERFORMANCE-REFACTOR.md` |
| 想查「为什么是这个口径 / 为什么不做某件事」 | `ARCHIVE-DESIGN-NOTES.md` |
| 想评估「用 WebView 重构前端」是否值得（现状实测 / 三方案对比 / 建议路线） | `WEBVIEW-FEASIBILITY.md` |
| 想知道 Spine 三层链路（路径归类 / 结构预览 / 真渲染播放）在真实库上的命中率与决策清单 | `SPINE-INTEGRATION-ANALYSIS.md` |
| 想核对维基化关联页的目标版式与信息结构（灰机 wiki 实地考察 + 取舍表） | `WIKI-PAGE-LAYOUT.md` |
| 想看实现历史与后续方向的长文记录 | `ROADMAP.md` |
| 项目门面（三步工作流、格式边界、CLI、打包） | `../README.md` |

## agent 推荐阅读顺序

1. `STATUS.md` §2（基线命令）→ 先跑一遍 build + test 确认全绿。
2. `CODE-STRUCTURE.md` §2–§6（分层 / 目录地图 / 流程 / 不变量）。
3. 按任务查 `PROJECT-INDEX.md` §14「按症状定位文件」或 `CODE-STRUCTURE.md` §8 决策表。
4. 动手前确认 `STATUS.md` §3 铁律与 §6 已知边界；涉及导出/调试再读 `ARCHIVE-DESIGN-NOTES.md`。
5. **改任何代码前扫一眼 `LOGGING.md` §3 硬性纪律**（热路径不许拼字符串、只加日志不改行为）。
6. 收尾：`STATUS.md` §2 三命令 + 必要时更新本文档地图与索引。

## 约定

- **分析以读代码为准**：不要靠运行程序、探针脚本或截图去推断结构；
  本文档体系的结论全部来自源码阅读（read/grep）。
  `PROJECT-INDEX.md` **不记行号、不记类名与方法签名**（改一次代码就过期、维护成本大于收益）：
  它只回答「哪个文件、什么命名空间、大致干什么」；要精确到方法请直接 grep 源码。
- **改代码就更新索引**：新增/删除源文件、改变职责边界或口径时，
  同步更新 `PROJECT-INDEX.md` 对应条目与 `CODE-STRUCTURE.md` 的不变量表。
  `PROJECT-INDEX.md` 的条目按「命名空间 / 文件 / 功能」三列，加一行即可，**不要回填行号**。
- **计划不常驻**：新的执行计划写完后按里程碑执行，完成后归档（git），
  只把**仍然有约束力的结论**沉淀进 `STATUS.md` / `CODE-STRUCTURE.md` / `ARCHIVE-DESIGN-NOTES.md`。
- **中文优先**：文档与提交信息用中文；代码注释保持现有风格（中文说明 + 必要时英文术语）。
