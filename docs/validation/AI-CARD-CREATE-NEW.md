# 直接新建素材与持续迭代验收

日期：2026-11-16。Agent 协议版本 `2.0.0`，构建器 `native-assets-3.0.0`，制作描述版本 `3`（兼容原有版本 `2` 网格描述）。

## 行为

- MCP 不再暴露成品素材搜索；旧调用返回 `ASSET_SEARCH_REMOVED`。参数编辑拒绝非本会话制作的素材，管理器人工素材选择保持原有接口。
- 角色修改与单素材构建均不限次数，旧配置 `max_iterations` 被忽略。能力接口的两项次数上限为 `null`，计数仅用于记录。
- Schema 为所有素材和布局字段提供制作入口及启用状态。制作支持服装、配饰、发型、独立目录贴图、保留拓扑的头部模型、布局及覆盖贴图。
- 完成验收同时检查当前启用素材的新建记录、结构、依赖、当前版本渲染和逐项视觉评估。任一缺项返回 `needs_work`，仅完整导出成功返回 `complete`。
- 取消、失败、旧版本恢复和服务重启保留草稿。已结束的异步作业留盘后回收线程及内存；只加载当前角色和指定制作模板引用的生成资源。

## 回归入口

`scripts/build/Build-Cpp.ps1 -SkipUi -Jobs 4` 完成底层构建及 20 项 CTest，其中会话测试在旧的 12 轮配置下连续修改 32 次，并对同一素材执行 6 次失败尝试，确认不再遇到次数限制。完整应用构建使用 `scripts/build/Build-Cpp.ps1 -Jobs 4`。

```powershell
python tests/agent/Test-NewAssetTypes.py --executable <HS2Agent.exe> --config <隔离配置.json> --image <测试PNG> --report <报告.json> --sex female --exercise-recovery
python tests/agent/Test-NewAssetTypes.py --executable <HS2Agent.exe> --config <隔离配置.json> --image <测试PNG> --report <完整报告.json> --sex female --session <会话ID> --complete
```

第一条覆盖新制作类别、原生回读和渲染、禁止旧素材选择、四次头部拓扑拒绝后继续制作、旧头部技术模板复用、取消保留版本及恢复历史布局。未新建的启用素材仍列入 `creation_issues`。第二条补齐待制作目标并验证最终导出。

需要修复的基准卡配饰可在测试会话内使用 `--disable-accessory-slot <槽位>` 关闭，不修改原卡。`--enable-test-accessory` 用于启用配饰测试槽；`--rebuild-kinds head catalog_texture` 可在兼容性修复后重新制作已启用的对应素材。

完整导出测试提交的是模拟调用方评估，仅验证协议和文件管线。测试 PNG 与固定几何不代表人物还原质量；服务不会把此测试当成其他角色的视觉通过记录。

## 真实游戏检查

```powershell
python tests/agent/Prepare-GameSandbox.py --game <正式游戏目录> --output <独立游戏副本> --reports <完整男性报告.json> <完整女性报告.json>
./tests/agent/Test-GameAssets.ps1 -Game <正式游戏目录> -Sandbox <独立游戏副本> -CheckSource tests/agent/GameCreationChecks.cs -Runner <hs2_agent_tool_runner.exe> -TimeoutSeconds 180
```

只在有独立标记的副本内安装测试插件和生成的角色包。检查真实 HS2 读卡、角色装配、Sideloader 素材来源、目录贴图解码、头部与皮肤绑定、网格蒙皮、表情通道及布局参数；角色装配异步异常也判定失败。游戏进程及子进程由 Windows Job 管理，退出或超时后回收。

游戏检查发现并修复了原生回读无法暴露的两项兼容要求：Unity 资源路径必须为小写；脸部皮肤清单的 `HeadID` 与 manifest 的 `faceSkinInfo` 必须指向当前生成头部。头部再次改变后，旧皮肤重新列为待制作项。服装技术模板优先使用原版组件，以免旧 MOD 的悬空依赖阻断新素材制作。

验收副本使用与本机游戏相同的 BepInEx 默认 `UnityEngine.CoreModule.dll / Application / .cctor` 入口，创建空 `patchers` 目录。原测试的 `TitleScene.Start` 入口在部分启动中停滞，已更换；超时样本不会计为游戏通过。

无图形模式已通过 71 项检查及 61 个纹理通道解码。男性头部 6744 顶点、150 个表情通道，女性头部 7484 顶点、147 个表情通道。该模式不代表 GPU Shader 或最终画面检查。

最终 D3D11 复测连续两次在游戏／插件启动阶段超时，未生成逐项结果；由受控进程工具回收。原生 D3D11 制作预览已通过，但修复后真实游戏的 GPU Shader 与最终画面验证仍未完成。

## 本机证据

隔离数据与报告位于 `artifacts/agent-create-validation`，不会进入发布包：

- `types-male.json`、`types-female-final.json`：男性 24、女性 23 组新制作类别的原生检查与恢复测试。
- `complete-male-final.json`、`complete-female-final.json`：修复后完整制作和导出结果。
- `game-final/agent-test-result-nographics.json`：71 项真实游戏无图形加载结果；同目录保留游戏及受控进程日志，`game-final-test-d3d11.log` 记录最终图形模式启动超时。
- `../agent-create-build-final.log`：底层构建及 CTest。
- `../agent-create-build-package.log`：完整 WinUI 构建及原生运行库检查。
- `process-audit.json`：本任务进程退出检查。

Agent 为任意新素材保留 `game_validation: not_run`；上述固定样本的游戏检查不自动适用于其他角色或几何输入。
