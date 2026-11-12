# 图片创建人物卡 Agent

`HS2Agent.exe` 是独立 C++20 MCP stdio 服务。外部 AI 通过随包 `skills/hs2-card-agent` 技能调用参数编辑、素材检索、D3D11 渲染、原生素材构建及角色包导出。无需另配视觉模型 API。

## 配置

完整构建 `build-cpp.ps1` 后，Agent 与管理器一起位于构建目录的 `bin/Release`。复制 `agent.example.json` 为本机 `agent.local.json`，填写游戏目录、独立本地数据目录、男女基准卡、输出目录。数据目录和输出目录必须在游戏之外。基准卡保留原件，生成卡另存。

```powershell
& 'D:\Apps\HS2Manager\HS2Agent.exe' --config 'D:\Apps\HS2Manager\agent.local.json' --check
codex mcp add hs2 -- 'D:\Apps\HS2Manager\HS2Agent.exe' --config 'D:\Apps\HS2Manager\agent.local.json'
```

也可按 `agent.mcp.example.toml` 配置 MCP 客户端。将随包技能文件夹放入客户端的技能目录。首次连接后发送参考图片并请求创建 HS2 人物卡。

配饰、发型、服装和覆盖贴图的生成、适配及资源包写入由内置 C++ 完成，无需 Unity Editor、Blender 或 ModdingTool 工程。游戏资源与插件版本仍须与人物卡兼容；材质和组件模板只从本机游戏／MOD 中读取，不随应用分发。建模配方与迁移说明见 [素材接口](../skills/hs2-card-agent/references/assets.md)。

`hs2_capabilities.native_asset_build` 返回构建版本、描述版本、输入格式及 FBX 适配层状态。旧配置里的 `blender_path`、`unity_path`、`modding_project` 可保留，但 Agent 不再使用。3D 模型导出的 `.blend` 和 Unreal 项目仍使用相应软件，通用 FBX 导出继续由原生适配层完成。

## 数据与恢复

Agent 使用独立 SQLite 资料库、`Sessions`、`Jobs`、`Assets` 和 `Cache`，不打开管理器数据库。会话逐版本写入完整草稿，版本检查避免异步任务覆盖新修改。启动后原先运行的任务标记为中断；草稿和构建日志保留，可重新调用工具。

参考 FBX、结构化场景、骨架、尺寸和 UV PNG 放在 `Reference`；构建描述、回读结果与候选资源包放在 `Builds`。参考场景采用 HS2 左手坐标、Y 向上，每单位 0.1 米。构建会重新取得当前草稿的身体和骨架，不会使用修改体型前的旧参考。

服务标准输出仅写 JSON-RPC；断开输入会取消任务并等待结束。服务不安装系统后台服务。只读使用游戏资源，输出角色包由用户导入游戏。候选包经过序列化回读、对象引用检查、素材目录解析、原生试渲染及适配采样后才登记到素材库并提交草稿。

素材指纹覆盖版本化描述、输入内容、生成几何、模板依赖和原生构建版本；新构建不会使用旧编辑器管线的缓存。原有草稿及已登记的素材继续可读。`source_script`、`template_material`、`shader` 等旧工程参数返回 `ASSET_SPEC_MIGRATION`，不会执行脚本。

## 验证状态

结构和依赖检查、当前版本原生渲染、调用方视觉评估、真实游戏验证分别记录。原生加载和打包不能证明游戏加载成功。新素材构建不通过或达到尝试上限时保留待完善草稿。

原生替换验收见 [原生素材验证](validation/NATIVE-ASSETS.md)。旧编辑器管线的历史记录保留在 `validation/AI-CARD-AGENT.md`。
