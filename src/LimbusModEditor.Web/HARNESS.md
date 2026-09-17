# 验证接缝使用指南（Verification Harness）

## 快速开始

### 1. 启动本地静态服务

```bash
cd src/LimbusModEditor.Web
npx serve dist -l 5173
```

### 2. 在浏览器中打开

```
http://localhost:5173?harness=1#/wiki/page/persona:10201
```

### 3. 注入桩桥（浏览器控制台）

```javascript
// 加载夹具（单个对象，非数组）
const page = await fetch('/fixtures/wiki-persona-10201.json').then(r => r.json());

// 创建桩桥
window.__lmeIpc = {
  postMessage: (msg) => {
    const req = JSON.parse(msg);
    // 直接返回夹具中的 response（夹具是单个对象）
    const payload = page.method === req.method ? page.response : null;
    setTimeout(() => {
      const res = {
        id: req.id,
        kind: 'response',
        ok: true,
        payload,
      };
      window.__lmeIpc.__messageHandler?.({ data: JSON.stringify(res) });
    }, 10);
  },
  addEventListener: (type, handler) => {
    window.__lmeIpc.__messageHandler = handler;
  },
  removeEventListener: () => {},
};
```

### 4. 刷新页面

刷新后桩桥仍然有效（IPC 客户端每次加载都会检查 `window.__lmeIpc`）。

**注意**：刷新后需要重新注入桩桥（重新执行步骤 3），因为 `window.__lmeIpc` 会被清空。

## 协议说明

### 请求格式（页面 → 后端）

```json
{
  "id": "req-1",
  "kind": "request",
  "method": "wiki.page.load",
  "payload": { "pageId": "persona:10201" }
}
```

### 响应格式（后端 → 页面）

成功：
```json
{
  "id": "req-1",
  "kind": "response",
  "ok": true,
  "payload": { ... }
}
```

失败：
```json
{
  "id": "req-1",
  "kind": "response",
  "ok": false,
  "error": { "code": "not-found", "message": "页面不存在" }
}
```

### 事件格式（后端 → 页面，主动推送）

```json
{
  "kind": "event",
  "method": "progress",
  "payload": { "operationId": "export-000", "phase": "carra2", "current": 37, "total": 100, "message": "正在重打包…" }
}
```

## 夹具格式

夹具是**单个 JSON 文件**，每个文件对应一个页面：

```json
{
  "method": "wiki.page.load",
  "response": {
    "id": "persona:10201",
    "title": "格里高尔",
    "category": "persona",
    "sections": [
      { "id": "overview", "title": "概述", "content": "...", "bindings": [] }
    ],
    "toc": [{ "id": "overview", "title": "概述", "level": 1 }]
  }
}
```

字段说明：
- `method`：匹配的方法名（必需）
- `response`：响应数据

## 夹具命名规范

所有夹具文件使用**连字符**命名（统一规范）：

| 文件 | 说明 |
|------|------|
| `wiki-persona-10201.json` | 人格页 - 格里高尔 |
| `wiki-persona-3.json` | 人格页 |
| `wiki-persona-998.json` | 人格页 |
| `wiki-enemy-8106.json` | 敌方单位页 |
| `wiki-enemy-8582.json` | 敌方单位页 |
| `wiki-ego-20205.json` | E.G.O 装备页 |
| `wiki-ego-21005.json` | E.G.O 装备页 |
| `wiki-ego-gift-9182.json` | E.G.O 饰品页 |
| `wiki-ego-gift-9841.json` | E.G.O 饰品页 |
| `wiki-abnormality-8002.json` | 异想体页 |
| `wiki-abnormality-8003.json` | 异想体页 |
| `wiki-announcer-3detectives-announcer.json` | 播报员页 |
| `wiki-announcer-amiya-announcer.json` | 播报员页 |

## 生成夹具（C# 侧）

### 人格页夹具

```csharp
// 在 Application 层临时添加 harness 端点
var page = await wikiPageQueryService.GetPageAsync("persona:10201");
var json = JsonSerializer.Serialize(page, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText("wiki-persona-10201.json", json);
```

### 剧情页夹具

```csharp
// 剧情数据经 StoryDataAuthorityProvider 提取
var storyData = await storyDataAuthorityProvider.GetStoryDataAsync("story:1");
var sections = await wikiPageArranger.ArrangeSectionsAsync(storyData);
var page = new WikiPage
{
    Id = "story:1",
    Title = storyData.Title,
    Category = "story",
    Sections = sections,
};
var json = JsonSerializer.Serialize(page, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText("wiki-story-1.json", json);
```

数据来源：
- `lang/StoryData/S<n>.json`：剧情文本
- `static-tables` 中的 `cutscene`/`storytheater*`：过场动画数据

## Playwright 自动化示例

```typescript
import { test, expect } from '@playwright/test';

test('人格页渲染', async ({ page }) => {
  // 注入桩桥（使用真实夹具）
  await page.addInitScript(async () => {
    const page = await fetch('/fixtures/wiki-persona-10201.json').then(r => r.json());
    window.__lmeIpc = {
      postMessage: (msg) => {
        const req = JSON.parse(msg);
        const payload = page.method === req.method ? page.response : null;
        setTimeout(() => {
          window.__lmeIpc.__messageHandler?.({
            data: JSON.stringify({ id: req.id, kind: 'response', ok: true, payload }),
          });
        }, 10);
      },
      addEventListener: (t, h) => { window.__lmeIpc.__messageHandler = h; },
      removeEventListener: () => {},
    };
  });

  await page.goto('http://localhost:5173?harness=1#/wiki/page/persona:10201');

  // 验证标题渲染
  await expect(page.locator('h1')).toContainText('格里高尔');

  // 验证分节渲染（夹具中有 3 个分节）
  await expect(page.locator('.wiki-section')).toHaveCount(3);

  // 刷新后仍可用（需重新注入桩桥）
  await page.reload();
  // 重新注入...
  await expect(page.locator('h1')).toContainText('格里高尔');
});
```

## 实测证据

### 测试环境

- 静态服务：`npx serve dist -l 5173`
- URL：`http://localhost:5173?harness=1#/wiki/page/persona:10201`
- 夹具：`wiki-persona-10201.json`

### 预期结果

| 检查项 | 预期值 |
|--------|--------|
| 页面标题 | `格里高尔` |
| 分节数量 | 3（概述、剧情、E.G.O） |
| TOC 数量 | 3 |
| 信息框字段 | 身份、武器、初始E.G.O |

### 截图

（待 architect 使用 Playwright 截图后补充路径）

## 文件清单

| 文件 | 说明 |
|------|------|
| `src/ipc/harness.ts` | 验证接缝核心逻辑 |
| `src/ipc/stubBridge.ts` | 桩桥实现 |
| `src/ipc/client.ts` | IPC 客户端（支持验证接缝） |
| `src/main.ts` | 应用入口 |
| `public/fixtures/*.json` | 夹具文件（13 个，覆盖 6 类别） |
| `HARNESS.md` | 本文档 |

## 安全说明

- 验证接缝仅在 URL 带 `?harness=1` 时生效
- 生产环境（WebView2 宿主）不受影响
- 不暴露项目路径、游戏路径等用户数据
- 代码注释明确标注为验证专用模式
