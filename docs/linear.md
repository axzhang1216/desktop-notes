# Linear 连接

桌面便签可以把 Linear 项目与项目文档作为一个只读视图显示在原有窗口中。

## 当前映射

- Linear Project → 一个项目分区
- Project summary / description → 项目摘要卡片
- Linear Document → 便签卡片
- 点击项目标题、摘要卡片或文档卡片 → 在浏览器打开对应 Linear 页面
- 顶部 `L` 按钮 → 在本地便签与 Linear 视图之间切换
- Linear 视图中的刷新按钮 → 重新从 Linear 拉取数据

本地便签的数据结构与备份格式保持不变，Linear 内容不会写入 `notes.json`。

## 连接方式

首次进入 Linear 视图时输入 Linear Personal API Key。可以在 Linear 的 **Settings → Security & access → Personal API keys** 中创建。

API Key 使用 Windows DPAPI（CurrentUser）加密后保存在与 `notes.json` 同目录的 `linear.json`。这份连接文件不会进入便签导出的 JSON 备份。

当前版本只读取 Linear 项目和文档，不会修改 Linear 内容。

## API

应用直接调用 Linear GraphQL API：

```text
https://api.linear.app/graphql
```

一次请求读取当前用户、最近更新的项目和文档，并在本地窗口中渲染。当前上限为 50 个项目、100 个文档。
