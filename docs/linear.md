# Linear 连接

桌面便签可以把 Linear 未完成 Issue 作为一个只读待办视图显示在原有窗口中。

## 当前映射

- 顶部便签图标 → 本地便签；左上角标题为「便签」
- 顶部 Linear 图标 → 待办视图；左上角标题为「待办」
- 只显示仍有未完成 Issue 的 Project 标题，不显示项目描述
- 每个 Project 下列出未完成 Issue；没有 Project 的 Issue 归入「无项目」
- 已完成或已取消的 Issue，以及没有任何未完成 Issue 的 Project，不显示
- Issue 与 Project 右侧的 `↗` → 在浏览器打开对应 Linear 页面
- 待办视图中的刷新按钮 → 重新从 Linear 拉取数据
- 右键 Linear 图标 → 管理或断开 Linear 连接

本地便签的数据结构与备份格式保持不变，Linear 内容不会写入 `notes.json`。

## 连接方式

首次进入 Linear 视图时输入 Linear Personal API Key。可以在 Linear 的 **Settings → Security & access → Personal API keys** 中创建。

API Key 使用 Windows DPAPI（CurrentUser）加密后保存在与 `notes.json` 同目录的 `linear.json`。这份连接文件不会进入便签导出的 JSON 备份。

当前版本只读取 Linear 项目和未完成 Issue，不会修改 Linear 内容。

## API

应用直接调用 Linear GraphQL API：

```text
https://api.linear.app/graphql
```

一次请求读取当前用户、最近更新的项目，以及状态不是 completed / canceled 的 Issue，并在本地窗口中渲染。当前上限为 50 个项目、100 条未完成 Issue。项目描述不会显示。
