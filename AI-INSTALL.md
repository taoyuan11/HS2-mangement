# HS2 Manager：交给 AI 自动安装 MCP 和技能

本文件随便携包放在 `HS2Manager.exe`、`HS2Agent.exe` 旁边。解压整个包后，把本文件的完整路径交给能读写本机文件、执行 Windows 命令的 AI，并发送：

> 请读取这份 AI-INSTALL.md，按文档为你当前使用的客户端安装 HS2 MCP 和 hs2-card-agent 技能，自动识别路径、保留已有配置，并完成验证。只有无法从本机确定的信息才询问我。

以下内容是给执行安装的 AI 的操作任务。用户要求按本文安装时，请实际完成文件配置、技能复制和验证，不要只复述步骤或把命令交还用户执行。仅阅读本文不代表用户已要求安装。

## 1. 确定安装包和目标客户端

- 以**本文件实际所在目录**作为 `packageRoot`，不要使用当前工作目录、源码目录、构建机目录或文档里的示例路径。用户只发送了文档内容而没有路径时，先在已知工作目录定位；无法定位才询问解压位置。
- 检查同目录存在 `HS2Agent.exe`、`agent.example.json`、`classdata.tpk`、`native/win-x64`、`skills/hs2-card-agent/SKILL.md` 以及 `skills/hs2-card-agent/references/assets.md`。缺失时说明缺少的包文件，不从未知来源补装。
- 安装到用户明确指定的客户端；未指定时使用正在执行此任务的客户端。无法识别时检查本机已有客户端配置，仍有多个候选才询问。不要向所有客户端重复安装。
- 本包为 Windows x64 原生程序，MCP 传输为 **stdio**。由客户端启动 `HS2Agent.exe`；不需要端口、HTTP URL、API Key、Python、Node.js 或额外模型服务。运行环境必须能启动 Windows EXE 并访问游戏目录；只有云端执行能力的 AI 不能完成本机安装。
- 先检查目标客户端已有的 HS2 服务和技能。同内容重复执行应直接复用；需要更新时先备份。只修改 HS2 对应项，保留其他 MCP、模型设置、凭据和技能。不要把客户端完整配置输出到聊天。

## 2. 发现游戏和基准人物卡

按以下顺序读取已有配置，不要求用户重新填写已能确定的路径：

1. 目标客户端原 HS2 MCP 的 `--config` 文件；其次检查包旁已有的 `agent.local.json`。
2. 管理器实际数据目录中的 `settings.json` 的 `GamePath` 字段。本机便携包一般是 `packageRoot/Data/settings.json`；NAS 便携包也检查 `%LOCALAPPDATA%/HS2Manager/Portable/*/Data/settings.json`。用户指定过 `--data` 时优先检查该目录。多个有效游戏目录且无法确定本包对应项时再询问，不擅自选择另一套游戏。
3. 用户当前提供的游戏路径，或上述目录中的明确线索。示例配置里的 `D:/Games/HS2`、`YOUR_NAME` 不是真实配置，不能直接采用。

验证游戏目录至少含 `abdata/chara/oo_base.unity3d` 和 `UserData/chara`。NAS 或映射盘必须能从 MCP 客户端进程访问；映射盘不可用时，在知道对应共享位置的情况下改用 UNC 路径。

基准卡先复用已有 `base_cards.male` / `base_cards.female`。缺失时在游戏 `UserData/chara/male`、`UserData/chara/female` 查找现有 PNG，优先使用原版 `HS2_ill_*.png`，再用其他候选，通过第 4 步验证性别与卡片格式。原图只读，生成结果另存。不要将普通图片、虚构路径或 Unity 资源包填入基准卡字段。

若找不到游戏目录，只询问游戏根目录后继续。若仅某一性别缺少有效基准卡，可以先完成 MCP 和技能安装，将该字段写为空字符串，并明确报告该性别暂不可创建，不宣称完全可用。

## 3. 写入本机 Agent 配置

新安装默认使用 `%LOCALAPPDATA%/HS2Manager/Agent/<client>/agent.local.json`；`<client>` 替换为本次客户端的稳定名称，例如 `codex`。配置放在包外，便携包升级时不会丢失；不同客户端使用独立数据目录，避免同时占用同一 Agent 数据库。

| 字段 | 填写规则 |
| --- | --- |
| `game_path` | 第 2 步确认的游戏绝对路径 |
| `data_directory` | 默认配置文件旁的 `Data`，必须是本机磁盘目录，位于游戏之外，不能复用管理器的 Data |
| `output_directory` | 默认配置文件旁的 `Output`，位于游戏之外；保留用户原有的有效输出目录 |
| `base_cards.male` / `female` | 已发现的对应性别基准卡绝对路径，缺失性别用空字符串 |
| `max_iterations` | 新安装默认 `24`，更新保留用户设置 |

所有路径写成解析后的绝对路径，不能留下 `%LOCALAPPDATA%`、`~`、`<client>` 等变量或占位符。路径含空格、中文、反斜杠或 UNC 时，用 JSON 序列化器编码，不手拼字符串。默认不设置 `runtime_directory`，程序会从 EXE 所在目录加载资源；更新已有配置时检查旧的该字段，避免继续指向已移走的旧包。

先验证数据和输出目录均不在游戏目录内，再创建目录。原配置存在时先读取、备份并合并，不用示例配置整文件覆盖。首次创建可使用以下 PowerShell 模板；变量应由你在前述步骤填写真实值：

```powershell
# $agentConfigPath、$gameRoot、$agentData、$agentOutput、$maleCard、$femaleCard
# 均为前面已确定的实际值；本段仅用于尚不存在的配置文件。
if (Test-Path -LiteralPath $agentConfigPath) {
    throw '配置已存在，请先读取、备份并合并。'
}
$config = [ordered]@{
    game_path = $gameRoot
    data_directory = $agentData
    output_directory = $agentOutput
    base_cards = @{ male = $maleCard; female = $femaleCard }
    max_iterations = 24
}
New-Item -ItemType Directory -Path (Split-Path -Parent $agentConfigPath), $agentData, $agentOutput -Force | Out-Null
[IO.File]::WriteAllText($agentConfigPath, ($config | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
```

## 4. 验证本地服务

在注册到客户端之前执行，等待退出并检查退出码和返回的 JSON：

```powershell
$agentExe = Join-Path $packageRoot 'HS2Agent.exe'
& $agentExe --config $agentConfigPath --check
if ($LASTEXITCODE -ne 0) { throw 'HS2 Agent 启动检查失败，请修复后重试。' }
```

退出码为 0 不等于游戏资源全部可用。还必须检查 `game_available`、`resource_database_available`、`native_asset_build.available` 和 `native_asset_build.fbx_import_available` 为 `true`；配置了某性别基准卡时，其 `base_cards.<sex>.valid` 也必须为 `true`。不通过时修正路径或选择另一张有效基准卡后重试。

如果已有客户端正在使用相同数据目录，不能同时启动 `--check`。优先从该连接调用 `hs2_capabilities`；不要强行终止用户正在使用的 Agent 或修改其任务数据。

## 5. 注册当前客户端的 MCP

通用连接参数如下，所有路径都由本次实际位置生成：

- 服务名：`hs2`。
- 传输：`stdio`。
- 可执行程序：`packageRoot/HS2Agent.exe` 的绝对路径。
- 参数数组：`["--config", "agentConfigPath 的绝对路径"]`，不要包含 `--check`。
- 不通过 shell 拼接启动命令；不依赖客户端工作目录；不注册开机任务或后台系统服务。

### Codex

MCP 配置位于有效的 `CODEX_HOME/config.toml`；未设置 `CODEX_HOME` 时默认 `%USERPROFILE%/.codex/config.toml`。检查当前项目 `.codex/config.toml` 是否已有同名覆盖项，确保最终生效的路径正确。

优先使用客户端提供的 MCP 配置工具，或已安装的 Codex CLI。先检查已有 `hs2` 项。首次新增时执行：

```powershell
codex mcp add hs2 -- $agentExe --config $agentConfigPath
if ($LASTEXITCODE -ne 0) { throw 'Codex MCP 注册失败。' }
codex mcp list
```

没有 CLI 时，直接备份并编辑当前有效的 `config.toml` 中 HS2 对应表。下面仅展示结构，须替换成实际路径，不能原样写入：

```toml
[mcp_servers.hs2]
command = 'C:\实际解压目录\HS2Manager\HS2Agent.exe'
args = ['--config', 'C:\实际用户目录\AppData\Local\HS2Manager\Agent\codex\agent.local.json']
startup_timeout_sec = 20
tool_timeout_sec = 60
```

使用 TOML 感知的修改方式或精确替换原有 HS2 表，避免重复表。路径包含单引号时改用正确转义的 TOML 字符串。已有服务就是本包的 HS2 Agent 时更新其启动路径、配置路径并保留其他选项；已有 `hs2` 名称属于另一服务时使用空闲名称 `hs2-manager`，并在验证与交付中说明实际名称，不覆盖无关服务。不改变客户端的全局权限或审批策略。

### 其他支持本地 stdio MCP 的客户端

识别当前客户端后，使用其实际支持的配置工具和当前配置格式注册上述连接。支持 `mcpServers` JSON 的客户端，其条目结构一般如下；配置位置和是否需要 `type: "stdio"` 以该客户端文档为准：

```json
{
  "mcpServers": {
    "hs2": {
      "command": "C:/实际解压目录/HS2Manager/HS2Agent.exe",
      "args": ["--config", "C:/实际用户目录/AppData/Local/HS2Manager/Agent/客户端名称/agent.local.json"]
    }
  }
}
```

仅合并 HS2 条目，不覆盖整个 JSON。不确定该客户端能力时先查它的官方说明；若只支持远程 MCP，说明本包需要本地 stdio 客户端，不虚构 HTTP 地址。

## 6. 安装完整技能目录

源目录为 `packageRoot/skills/hs2-card-agent`。必须复制整个目录（包括 `references/assets.md`），不能只复制 `SKILL.md`。

Codex 默认用户技能目录是 `%USERPROFILE%/.agents/skills`，安装结果应为：

```text
%USERPROFILE%/.agents/skills/hs2-card-agent/
    SKILL.md
    references/
        assets.md
```

先检查该客户端当前启用的技能目录和同名技能，防止多个目录出现重复安装。其他客户端按其实际支持的技能目录安装；不支持本地技能加载的客户端要明确说明，不能把复制到任意文件夹算作技能已安装。

目标不存在时复制源文件夹；存在且内容相同时跳过；存在且内容不同时，将旧目录完整备份到 `%LOCALAPPDATA%/HS2Manager/Agent/SkillBackups/<时间标识>/hs2-card-agent` 等**技能扫描目录之外**的位置，再用随包目录替换。仅操作这个技能，不移动或清理其他技能。备份路径必须唯一。

复制后逐文件核对相对路径、大小与 SHA-256，确认没有误复制成 `hs2-card-agent/hs2-card-agent`。读取安装后的 `SKILL.md`，确认技能名为 `hs2-card-agent`，并确认 `references/assets.md` 相对链接有效。

## 7. 验证客户端连接并交付

1. 重新读取客户端有效配置，确认实际服务名、可执行程序和配置路径正确；确认技能已在正确的扫描目录中。
2. 当前客户端支持重载时刷新 MCP 与技能；必须重启或新建会话才生效时，报告“配置与技能已安装，等待客户端重载”，不要自行强制关闭用户的客户端。
3. 通过客户端连接执行 `initialize`、`tools/list`，确认能发现 `hs2_capabilities`、`hs2_begin_card`、`hs2_render`、`hs2_validate`、`hs2_finalize_card` 等工具，并实际调用 `hs2_capabilities` 核对第 4 步的结果。客户端一般自动完成初始化，使用其已连接工具即可。只有真正取得客户端工具结果后，才报告“客户端连接已验证”。
4. 暂时不能重载时，可用短命 stdio 测试进程验证程序协议：每行一条 JSON-RPC，先 `initialize`（协议 `2025-11-25`），再 `notifications/initialized`、`tools/list`、`tools/call` 调用 `hs2_capabilities`。读完响应后关闭标准输入并等待进程退出；设置超时，失败时只清理你本次启动的测试进程。此测试不能冒充客户端已经载入。
5. 本次安装不创建人物卡、不启动素材构建。安装验证结束前确认自己启动的检查进程均已退出；客户端之后按需管理正常的 stdio 服务生命周期。

最后简要列出：安装包实际目录、目标客户端与 MCP 名称、Agent 配置文件、数据和输出目录、技能安装目录、已完成的验证，以及是否还需重载或补齐某性别基准卡。随后用户即可发送参考图，让 AI 使用 `hs2-card-agent` 创建人物卡。

## 升级、迁移与卸载

包移动到新目录或升级后，可以再次让 AI 执行本文：复用已有 Agent 数据与输出，只更新 EXE 路径、失效配置及技能内容，然后重新验证。备份应保留在包外。只有用户要求卸载时，移除本次 HS2 MCP 配置和已安装技能；保留 Agent 配置、草稿、输出与备份，除非用户明确要求删除这些数据。

补充说明见随包 [Agent 文档](docs/AI-CARD-AGENT.md)。Codex 配置与技能位置依据 [官方 MCP 文档](https://developers.openai.com/codex/mcp) 和 [官方技能文档](https://developers.openai.com/codex/skills)；客户端版本变化时以其实际支持能力为准。
