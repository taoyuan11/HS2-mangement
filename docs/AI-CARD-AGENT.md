# 图片创建人物卡 Agent

`HS2Agent.exe` 是独立 C++20 MCP stdio 服务。外部 AI 通过随包 `skills/hs2-card-agent` 技能调用参数编辑、素材检索、D3D11 渲染、Blender／Unity 构建及角色包导出。无需另配视觉模型 API。

## 配置

完整构建 `build-cpp.ps1` 后，Agent 与管理器一起位于构建目录的 `bin/Release`。复制 `agent.example.json` 为本机 `agent.local.json`，填写游戏目录、独立本地数据目录、男女基准卡、输出目录和工具路径。数据目录和输出目录必须在游戏之外。基准卡保留原件，生成卡另存。

```powershell
& 'D:\Apps\HS2Manager\HS2Agent.exe' --config 'D:\Apps\HS2Manager\agent.local.json' --check
codex mcp add hs2 -- 'D:\Apps\HS2Manager\HS2Agent.exe' --config 'D:\Apps\HS2Manager\agent.local.json'
```

也可按 `agent.mcp.example.toml` 配置 MCP 客户端。将随包技能文件夹放入客户端的技能目录。首次连接后发送参考图片并请求创建 HS2 人物卡。

新网格需要 Blender、Unity 2018.4.11f1 和固定 ModdingTool 工程（基线提交 `67b460fc880e8c46164781967d2bb06a5d10896d`）。保持游戏、Shader 和工具工程引用可读；Unity 许可与首次导入需事先完成。素材制作描述及脚本变量见技能的 `references/assets.md`。

可运行 `scripts/development/Prepare-AgentTools.ps1 -BlenderDirectory <Blender目录>`，将官方 Unity Editor 与固定 ModdingTool 准备到项目根目录的 `.tools/agent-tools`。脚本校验安装包哈希和官方签名，用 7-Zip 展开 Editor，并输出工具路径；`-Check` 只检查已存在的环境。工具目录被 Git 忽略。首次仍需在 Unity Editor 中登录账号并激活许可；未激活时 Agent 返回 `UNITY_LICENSE_REQUIRED`，保留草稿。

环境准备需要 Git、7-Zip 和 Python 3.12 或以上。准备脚本会将七项依赖按提交或包版本固定并嵌入本地 ModdingTool 工程，写入 `Packages/agent-package-lock.json`；构建副本使用这些本地包，避免旧版 UPM 每次联网解析。许可激活后可重复运行 `-Check` 检查本地工具和依赖。

## 数据与恢复

Agent 使用独立 SQLite 资料库、`Sessions`、`Jobs`、`Assets` 和 `Cache`，不打开管理器数据库。会话逐版本写入完整草稿，版本检查避免异步任务覆盖新修改。启动后原先运行的任务标记为中断；草稿和构建日志保留，可重新调用工具。

FBX／Blender 参考和构建工程放在 Agent 数据目录的 `Reference`、`Builds`，由会话记录其路径；使用较短路径以兼容旧工具的文件 API。Agent 启用长路径清单，不修改系统长路径设置。

服务标准输出仅写 JSON-RPC。Blender／Unity 由关闭时终止的 Windows Job 管理，断开输入会取消任务并等待回收。服务不安装系统后台服务。只读使用游戏资源，输出角色包由用户导入游戏。

生成素材的指纹包含 FBX、贴图、构建脚本、Unity 工程版本和依赖锁，管线修正后会生成新的素材标识。Blender 保留新网格的世界变换；Unity 处理坐标单位、有效骨骼、绑定矩阵及游戏清单格式，再构建资源包。

## 验证状态

结构和依赖检查、当前版本原生渲染、调用方视觉评估、真实游戏验证分别记录。原生加载和打包不能证明游戏加载成功。新素材构建不通过或达到尝试上限时保留待完善草稿。

实际运行范围和当前环境未完成项见 `docs/validation/AI-CARD-AGENT.md`。
