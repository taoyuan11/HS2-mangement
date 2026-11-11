# C++ / WinUI 3 迁移

生产实现使用 C++20、C++/WinRT、WinUI 3 和 Direct3D 11。人物管理、编辑、预览与模型导出在同一原生进程内运行，不加载 CLR、托管业务程序集或 Unity 引擎。Blender / Unreal 的 Python 文件是传给用户已安装编辑器的自动化资源；管理器自身不依赖 Python 运行时。

## 模块

| 目录 | 职责 |
| --- | --- |
| `src/HS2Manager.NativeApp` | WinUI 窗口、人物库、编辑器、资源选择、预览交互、导出界面、数据位置迁移 |
| `src/HS2Manager.NativeCore` | SQLite、人物卡与 MessagePack、无损编辑、文件事务、MOD / 插件索引、四级人物包导出 |
| `src/HS2Manager.NativeScene` | UnityFS / SerializedFile 只读解析、ZIPMOD、原始资源目录、人物组装、形变、动画、IK、注视、动态骨骼与布料 |
| `src/HS2Manager.NativeRendering` | D3D11 资源、原始着色器、材质合成、GPU 蒙皮、阴影、延迟 / 前向渲染、环境及后处理 |
| `src/HS2Manager.NativeExport` | PBR 烘焙、FBX 骨架与形态、场景 / 灯光 / 环境导出、Blender / Unreal 转换与验证 |
| `native/HS2Manager.Physics`、`native/HS2Manager.FbxBridge` | 独立 PhysX 与 Autodesk FBX SDK 适配层 |

业务接口使用 UTF-8 字符串，文件系统及 Win32 调用使用 UTF-16 路径。后台工作由可取消线程管理；界面关闭时先取消并等待任务退出。外部转换器被放入专属 Windows Job Object，完成、失败、超时或取消均等待所属子进程退出。

## 数据兼容

继续使用原有 `library.db` 表和设置字段。人物 PNG 封面、未知 MessagePack 字段、扩展数据及未编辑块原样保留。保存继续支持另存、备份覆盖、版本冲突检查和备份恢复。文件整理保留隔离、恢复、永久删除确认及中断日志恢复。

源码启动保留原源码资料库的路径标识，NAS / UNC / 映射盘仍将数据库与缓存放到本机。迁移通过 SQLite backup API 读取包含 WAL 的数据库，校验并重定位编辑备份日志，然后发布完整副本；原资料保留。`--data` 的显式本机路径优先。

## 验证边界

旧版 [原生 GPU 验收](NATIVE-MIGRATION-RESULTS.md) 的 84 张卡结果属于 C# 到原生 GPU 的上一轮迁移，不能直接当作本次 C++ 重写的验收数据。本次记录见 [C++ 验证结果](CPP-VALIDATION.md)。

固定场景渲染比较、真实卡准备、动态骨骼数值比较和界面流程检查分别覆盖不同层；不能用固定姿态画面比较替代完整人物准备与动态验证。原版已有的插件专属效果限制及缺失资源诊断继续保留，不将这些情况当作迁移新增功能。

旧 C# 应用、Unity / 桥接实现、托管测试工程及其专用脚本已从工作树移除，可通过 Git 历史查看。冻结数值样本保留在 `tests/fixtures`，场景检查直接读取其中的动画曲线基准。历史文档保留当时的结果与命令；这些命令需在相应历史版本中运行。

## 构建与检查

使用 Visual Studio 2022 的桌面 C++ 工作负载、Windows SDK 和 CMake。`build-cpp.ps1` 还原固定版本的 C++/WinRT / Windows App SDK NuGet 包，并行构建原生模块与 WinUI 程序、运行 CTest、检查发布目录的 PE CLR 数据目录。并行度通过 `-Jobs` 设置（默认最多 4）；GUI 启动和关闭检查改由 `tests/ui/Invoke-CppWinUiSmoke.ps1` 单独执行。

```powershell
.\build-cpp.ps1
.\build-cpp.ps1 -BuildDirectory C:\Build\HS2Manager
# 仅编译原生库及其检查，适合不带 WinUI 的开发验证
.\build-cpp.ps1 -SkipUi
```

CMake 的普通检查不需要游戏。实际资源检查接受本机游戏路径，游戏资源只读，生成数据写入指定本机测试目录。`hs2_character_checks` 检查人物准备，`hs2_runtime_checks` 检查动态，`hs2_render_frame_checks` 检查实际 GPU 预览，`hs2_model_export_checks --real` 检查完整模型导出。固定样本比较脚本在 `tests/native/Run-CppFrozenCatalog.py`；Python 仅用于开发验证。

发布包中不包含人物卡、游戏 / MOD 文件、基准画面、数据库、开发测试程序、C# 源码、CLR 或 Unity 运行时。
