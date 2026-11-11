# 原生 GPU 预览与无引擎发布

预览、角色编辑、通用 FBX、Blender/Unreal 转换及正式构建均脱离 Unity Editor、Player 和 UnityEngine 程序集。继续读取本机游戏和 MOD 的序列化资源与 PE 元数据；不加载游戏或插件程序集，不执行资源中 MonoBehaviour 的代码。

## 场景和渲染

`NativeCharacterService` 复用卡片、插件参数和资源定位解析，`NativeCharacterScene` 保存共享场景语义。原始顶点通道、UV、切线、完整 BlendShape、蒙皮、纹理 mip、材质 Pass 和动画曲线直接读取。共享缓存只保存不可变资源，人物状态和合成贴图独立持有。

`HS2Manager.Rendering` 使用 Direct3D 11、Vortice 和 WinUI `SwapChainPanel`。后台解析与渲染线程分离，GPU 设备和上下文属于渲染线程；交换链直接连接视口，没有命名管道、共享帧内存或逐帧位图复制。截图按需回读同一完成后处理的 GPU 图像。

材质执行本机资源中的 D3D11 Shader 字节码与变体，绑定原始常量、纹理、渲染状态及内置环境参数。覆盖透明排序、阴影、皮肤、头发、眼睛、反射、覆盖贴图、MaterialEditor、DHH 和 BetterAA。启用效果无法执行时报告迁移缺陷，不以通用预览材质冒充一致性通过。

独立动画与物理管线保留形变、ABMX、动画/Avatar、IK、高跟鞋、视线、眨眼和动态骨骼的求值顺序。布料使用独立 PhysX 3.4.2 C++ 适配层；初始化、约束、碰撞和重置均单独验证。一致性由固定逐帧结果判定，不根据 SDK 版本推断。

## 会话与生命周期

`IPreviewSession` 提供加载、编辑修订、视图、暂停、截图、恢复和异步释放，事件携带加载编号、修订、首帧状态和诊断。快速切卡及连续编辑只提交最新有效结果；失败解析保留已提交场景，修复后可重试。

WinUI 视口处理 DPI、窗口缩放及页面切换；隐藏停止绘制，关闭等待任务结束并释放交换链、设备和人物资源。设备恢复重建当前快照，失败时保留草稿并提供重试；断开后重连保持相机、环境、播放状态及显隐。

新缓存位于 `Data/NativePreview-v1`。原数据库、设置、封面、备份和旧缓存保留；不会把旧格式缓存当作原生缓存读取。

## 导出与发布

`IModelExportBackend` 必须显式提供，正式应用仅使用 `NativeModelExportBackend`；生产 Core 不含旧 Player 后端或通信端点。独立 FBX SDK 2020.3.7 适配层直接从同一场景导出，截图和环境资源由原生 GPU 生成。Blender/Unreal 导入脚本内嵌于 `Core/ModelExport`；沿用参考姿态、未保存快照、服装范围、取消、独立重开和事务发布。跨引擎材质的既有边界见 [模型导出](MODEL-EXPORT.md)。

`Directory.Build.targets` 拒绝生产项目引用引擎或历史宿主。直接发布应用与 `build.ps1` 均执行 PE 审计：检查所有文件的实际程序集身份、程序集引用、P/Invoke、普通与延迟原生导入，以及旧运行端点；文件改名不能绕过检查。发布还实际装载配套 VC143、FBX 和布料 DLL，创建、步进并释放原生实例。包中不含游戏资源、人物卡、MOD、测试截图、Unity 或 Mono 运行时。

微软 C#/WinRT 2.2 的 .NET 运行库保留一个没有调用者的 Mono 兼容类型及六条 P/Invoke 声明，见 [对应上游源码](https://github.com/microsoft/CsWinRT/blob/2.2.0.241111.1/src/WinRT.Runtime/MonoSupport.cs)。审计仅对已检查的未修改二进制 SHA-256 `BCF3A14E8712A90837FC5C0D8C8A24696AF2BD7F74E9767597EC81EDEDFB23DB` 记录这些可选声明；其它 Mono 导入、运行库文件及冒名程序集仍拒绝。实际应用、编辑器、预览和原生 DLL 检查均另外验证没有加载 Unity/Mono 模块。升级该依赖后必须重新核对，不能按文件名放行。

历史实现位于 `tests/HS2Manager.Legacy*` 和原参考工程，必须显式选择，不能发布，不在正式解决方案或启动链路中。没有自动回退入口。所有转换和测试工具通过 Windows Job Object 登记所有权，正常、取消、失败、超时及宿主退出路径均回收本次启动的后代。

## 验收复现

先通过 `scripts/Build-NativeFbx.ps1` 与 `scripts/Build-NativePhysics.ps1` 构建适配层，再以 Release 构建正式解决方案。`tests/Run-NativeCatalog.py` 对冻结基线运行四视角和 24 个动态采样；`Build-NativeCatalogReview.py` 生成逐卡观察证据，`Complete-NativeCatalogReview.py` 汇总数值和人工复核门槛。缺图、执行错误、效果缺失或待复核均不能通过。

复核仅在全部参考与原生图片 SHA-256 均相同时复用先前观察；否则重新观察总览和原始分辨率局部。标准为人物及适用的面部区域 SSIM ≥ 0.99、平均通道误差 ≤ 2/255，种子 1979、30 Hz、360 帧，保留原镜头、1280×720、预热与重置规则。结论限于固定 84 张卡及其资源配置，不表示逐像素相同。

证据与逐卡差异见 [验收结果](NATIVE-MIGRATION-RESULTS.md)，同机顺序测量见 [性能报告](NATIVE-PERFORMANCE.md)。旧文档中的 Unity/CPU 预览描述是隔离历史记录。
