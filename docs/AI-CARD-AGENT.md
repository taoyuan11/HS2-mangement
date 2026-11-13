# 图片创建人物卡 Agent

`HS2Agent.exe` 是独立 C++20 MCP stdio 服务。外部 AI 通过随包 `skills/hs2-card-agent` 技能调用参数编辑、素材检索、D3D11 渲染、原生素材构建及角色包导出。无需另配视觉模型 API。

## 让 AI 自动安装

便携包根目录附带 [AI-INSTALL.md](../AI-INSTALL.md)。将它的完整路径交给能读写本机文件、执行 Windows 命令的 AI，发送“请按这份文档安装 HS2 MCP 和 hs2-card-agent 技能”。AI 按实际解压位置发现游戏和基准卡，在包外保存本机配置，注册当前客户端的 stdio MCP，复制完整技能目录并验证连接。已有配置先备份再合并；需要客户端重载或缺少有效基准卡时会明确说明。

`build-cpp.ps1`、`build.ps1` 和 `build-desktop.ps1` 的输出均携带该文档，发布审计会检查它存在且非空。

## 手动配置（可选）

完整构建 `build-cpp.ps1` 后，Agent 与管理器一起位于构建目录的 `bin/Release`。也可手动复制 `agent.example.json` 为本机 `agent.local.json`，填写游戏目录、独立本地数据目录、男女基准卡、输出目录。建议将配置保存在包外的本机目录，避免升级替换程序时丢失。数据目录和输出目录必须在游戏之外。基准卡保留原件，生成卡另存。

```powershell
& 'D:\Apps\HS2Manager\HS2Agent.exe' --config 'C:\Users\YOUR_NAME\AppData\Local\HS2Manager\Agent\codex\agent.local.json' --check
codex mcp add hs2 -- 'D:\Apps\HS2Manager\HS2Agent.exe' --config 'C:\Users\YOUR_NAME\AppData\Local\HS2Manager\Agent\codex\agent.local.json'
```

上面路径需替换为本机实际值。也可按 `agent.mcp.example.toml` 配置 MCP 客户端。将随包完整 `skills/hs2-card-agent` 文件夹放入客户端的技能目录（Codex 默认 `%USERPROFILE%/.agents/skills`）。首次连接后发送参考图片并请求创建 HS2 人物卡。

配饰、发型、服装和覆盖贴图的生成、适配及资源包写入由内置 C++ 完成，无需 Unity Editor、Blender 或 ModdingTool 工程。游戏资源与插件版本仍须与人物卡兼容；材质和组件模板只从本机游戏／MOD 中读取，不随应用分发。建模配方与迁移说明见 [素材接口](../skills/hs2-card-agent/references/assets.md)。

`hs2_capabilities.native_asset_build` 返回构建版本、描述版本、输入格式及 FBX 适配层状态。旧配置里的 `blender_path`、`unity_path`、`modding_project` 可保留，但 Agent 不再使用。3D 模型导出的 `.blend` 和 Unreal 项目仍使用相应软件，通用 FBX 导出继续由原生适配层完成。

## 数据与恢复

Agent 使用独立 SQLite 资料库、`Sessions`、`Jobs`、`Assets` 和 `Cache`，不打开管理器数据库。会话逐版本写入完整草稿，版本检查避免异步任务覆盖新修改。启动后原先运行的任务标记为中断；草稿和构建日志保留，可重新调用工具。

参考 FBX、结构化场景、骨架、尺寸和 UV PNG 放在 `Reference`；构建描述、回读结果与候选资源包放在 `Builds`。参考场景采用 HS2 左手坐标、Y 向上，每单位 0.1 米。构建会重新取得当前草稿的身体和骨架，不会使用修改体型前的旧参考。

服务标准输出仅写 JSON-RPC；断开输入会取消任务并等待结束。服务不安装系统后台服务。只读使用游戏资源，输出角色包由用户导入游戏。候选包经过序列化回读、对象引用检查、素材目录解析、原生试渲染及适配采样后才登记到素材库并提交草稿。

素材指纹覆盖版本化描述、输入内容、生成几何、模板依赖和原生构建版本；新构建不会使用旧编辑器管线的缓存。原有草稿及已登记的素材继续可读。`source_script`、`template_material`、`shader` 等旧工程参数返回 `ASSET_SPEC_MIGRATION`，不会执行脚本。

## 验证状态

### 参考还原工作流（Agent 1.1）

`hs2_set_reference_regions` 接收参考图中实际可见的正侧背和脸部裁剪。`hs2_render` 默认输出 768×1024 的正侧背、正侧脸，使用浅灰背景、25° 视角，并返回左参考右渲染的同视图对照图。裁剪和图板保持长宽比，不补造原图细节，也不计算相似度分数。侧脸方向可用 `side` 调整。

滑条探测默认同时比较两个视图，`delta` 可控制小范围扰动；每个字段用一张负向／当前／正向图板展示，避免多张图片被客户端额度截断。参数修改和素材制作后先比较主要差异，再推进细节。默认修正预算提高到 24 次；已有配置的显式预算继续有效。

新增 `hs2_review_render`，对脸、发型、身体、服装、配饰、材质逐项记录参考特征、渲染观察和修正动作。记录绑定当前角色版本、参考区域版本和 `render_id`。存在待修项、缺少视图或使用旧评估时，`hs2_finalize_card` 保留草稿并返回 `needs_work`；仅提交 `passed: true` 不能完成导出。旧会话继续可读，完成前需要补充裁剪和逐项评估。

`hs2_apply_changes.mark_best` 仅标记候选，查看图像并提交评估后才确认最佳版本。最终封面直接使用已评估的正面图，对照图和评估记录随角色包保存。评估仍来自调用方 AI，服务检查记录完整性和新鲜度，不能独立判断实际相似程度。接口示例见 [对照与评估](../skills/hs2-card-agent/references/quality.md)。

结构和依赖检查、当前版本原生渲染、调用方视觉评估、真实游戏验证分别记录。原生加载和打包不能证明游戏加载成功。新素材构建不通过或达到尝试上限时保留待完善草稿。

原生替换验收见 [原生素材验证](validation/NATIVE-ASSETS.md)。旧编辑器管线的历史记录保留在 `validation/AI-CARD-AGENT.md`。
