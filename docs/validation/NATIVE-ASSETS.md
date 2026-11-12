# 原生素材制作验收

日期：2026-11-12。构建版本：`native-assets-2.0.1`，建模描述版本：`2`。

Agent 的参考输出、建模、导入、蒙皮、贴图、UnityFS／SerializedFile 写入、原生预览与 `.zipmod` 制作均使用内置 C++。验收配置未设置 Unity、Blender 或 ModdingTool 路径。Blender 仅用于另行保留的 3D 模型导出回归。

## 分层检查

| 检查 | 结果与范围 |
| --- | --- |
| 完整构建 | `build-cpp.ps1 -Jobs 4` 通过，包含 Agent、WinUI 和原生适配层 |
| CTest | 19 / 19 通过，包含序列化写入、几何、四种模型导入、会话事务、模型导出与进程生命周期 |
| 序列化 | 大小端、64 位整数／对象引用、类型哈希、脚本引用、外部引用、确定性往返、LZ4 跨块资源流通过；截断包和重复对象编号被拒绝 |
| 导入与几何 | FBX、OBJ、GLB、GLTF；单位和坐标转换、对象镜像、形变、带缩放的层级骨骼矩阵、UV、重心权重转移、权重归一化与新增骨骼通过；缺失 UV、无效索引／骨骼、面数预算和取消检查通过 |
| 原生端到端 | 男女配饰、发型和服装各一例；参考 FBX／JSON／骨架／UV、制作、四视图预览、回读、素材登记和角色包导出 |
| 覆盖贴图 | 脸、身体、服装三类分别完成写入、原生预览、验证与角色包导出 |
| 会话与恢复 | 版本冲突、非法字段、失败批次、草稿写入失败不推进版本；取消、EOF 退出、重启恢复、中断标记、十二轮修正与单素材三次预算通过；旧脚本参数返回迁移诊断 |
| 指纹 | 同输入重复构建保持相同标识；描述、输入内容摘要和模板 CSV 配置变化均产生新标识。指纹还包含生成几何、模板依赖文件内容及构建版本 |
| 发布审计 | 73 个原生 PE 文件通过；无 CLR、Unity 引擎、Agent 编辑器脚本或游戏资源载荷；cgltf／LZ4 许可证随包提供 |
| 模型导出回归 | 保留的 Blender 5.3.0 RC 导出通过：13 个网格／材质、纹理打包、重复导入隔离与既有场景保留；产出 `.blend` 与四张渲染图 |
| 源码检查 | 本次修改范围的 C++／Python 格式、PowerShell 分析和 `git diff --check` 通过 |
| 进程回收 | Agent、游戏验收、Blender 导出及本次构建产生的 MSBuild 复用节点均已退出／回收；原有用户游戏进程保留 |

## 真实 HS2

在有标记的独立游戏副本中执行 `HoneySelect2.exe`，使用游戏 `ChaFileControl`、`ChaControl`、Sideloader、BonesFramework 和 Overlay 插件完成装配与 D3D11 渲染。测试插件通过独立 Visual Studio Roslyn 编译器编译，不读取 Unity Editor 安装目录。

最终九例全部通过（`game/agent-test-result.json` 的 `passed=true`）。首个游戏进程启动超时后由 Windows Job 回收，第二次启动完成全部检查并退出。

| 样本 | 男性 | 女性 |
| --- | --- | --- |
| 配饰 | 117 顶点，加载、Shader、世界几何及可见像素通过 | 117 顶点，同项通过 |
| 发型 | 34 顶点，姿态、实际头部大小扰动、新增动态骨骼及可见像素通过 | 34 顶点，同项通过 |
| 服装 | 全穿／半脱合计 14896 顶点，姿态、实际肩宽扰动、遮罩及三种状态通过 | 合计 21204 顶点，同项通过 |
| 脸／身体／服装覆盖 | 三例分别由游戏解码为 1024 × 1024 | 本轮覆盖贴图不重复性别组合 |

每个网格样本检查生成清单来源、有效网格、世界尺寸／位置、材质及受支持的 Shader，并单独拍摄素材检查可见像素。发型／服装检查游戏骨架姿态扰动和体型变化后的有效蒙皮；发型检查新增动态根与子骨骼；服装检查生成遮罩的游戏解码和全穿／半脱／脱下可见性。三类覆盖图由对应游戏插件解码后检查尺寸。

初次联合检查发现静态配饰在 BonesFramework 启用时不能使用需要重新绑定蒙皮的清单标志，已改为 `Weights=0`；发型和服装保留 `Weights=1`。服装清单显式启用半脱并清除源模板贴图覆盖字段。协议测试直接读取最终 `.zipmod` 的 CSV 检查这些值，构建版本更新使早期本地试验包失效。

## 证据与复现

测试产物不进入发布包，位于本机 `artifacts/native-assets`：

- `final-build.log`：最终构建、19 项 CTest 和发布审计。
- `{accessory,hair,clothes}-{male,female}-report.json`：六个网格的 MCP 协议报告；`overlay-{face,body,clothes}-report.json` 为覆盖图报告。
- `game/agent-test-result.json`：真实游戏逐例结果；`game/agent-*.png`：游戏画面及独立素材画面。
- `fingerprints/build-result.json`：确定性与指纹变化检查。
- `blender-export-regression/validation.json`、`character.blend` 和 `*.png`：独立的模型导出回归。
- `process-audit.json`：最终进程所有权与清理记录。

主要入口：

```powershell
./build-cpp.ps1 -Jobs 4
python tests/agent/Test-AgentProtocol.py --executable <HS2Agent.exe> --config <agent.json> --image <PNG> --report <report.json> --sex male --build-fixture hair --render --finalize
python tests/agent/Prepare-GameSandbox.py --game <HS2目录> --output <独立验收目录> --reports <report.json>
./tests/agent/Test-GameAssets.ps1 -Game <HS2目录> -Sandbox <独立验收目录> -Runner <hs2_agent_tool_runner.exe>
```

协议脚本用 `--sex female` 验证女性，用 `--build-fixture accessory/clothes` 切换网格，用 `--overlay --overlay-target face/body/clothes` 验证覆盖图。游戏脚本自动将验收插件编译到独立副本的 `BepInEx/plugins`；已有同源副本可用 `--reuse-base` 更新角色包。

`hs2_asset_inspector --fingerprints <游戏> <classdata.tpk> <缓存> <基准卡> <spec.json> <输出目录>` 在本机兼容模板上检查指纹，不修改源游戏资源。模型导出回归入口为 `tests/export/Test-BlenderExport.py`。

## 适用边界

- 样本是固定配方生成的简单配饰、带三个新增骨骼的发束和身体表面偏移服装。它们验证制作与交付行为，不代表参考人像相似度或成品美术质量。
- 半脱复用完整服装几何；复杂发型／裙摆动态、全部服装槽位和所有遮罩组合仍需按具体素材验证。
- 新增骨骼在包中声明 BonesFramework 依赖。发型新分支接到 `N_hair_Root` 并保留绑定姿态；Unity 内置资源保留游戏外部引用。
- 原生写入面向现有 HS2 的 Unity 2018 类型布局；不将未知版本 MOD 自动转换为兼容模板。测试中一张原有女性 MOD 卡的参考生成遇到现有读取器不支持的序列化版本，因此端到端女性样本使用游戏默认数据另存的独立基准卡，未修改用户原卡。
- 原生回读和真实游戏结果独立记录。Agent 新构建的 `game_validation` 仍为 `not_run`，不会把本次固定样本验收套用到任意新素材。
- 游戏副本省略部分场景资源；标题场景地图初始化和不相关的配置管理器依赖日志单独保留，验收按实际人物、素材与渲染检查判定。旧加载器偶有启动停滞，入口以有界重试及 Windows Job 回收进程。
