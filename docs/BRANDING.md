# HS2 Manager 标识

以两张交叠的收藏卡为轮廓，前卡嵌入独立绘制的 H 字形，向上倾斜的横笔呼应卡片的展开方向。两张卡暗合 HS2 的「2」，同时表达人物卡整理与收藏。

墨绿底色承接界面的中性绿调；象牙白代表人物卡，暖金代表收藏。标识不随系统强调色变化，在浅色、深色主题中保持一致。外框之外为透明区域。

| 用途 | 文件 |
| --- | --- |
| 可编辑矢量源稿 | `src/HS2Manager.NativeApp/Assets/Brand/hs2-manager.svg` |
| 1024 × 1024 透明 PNG | `src/HS2Manager.NativeApp/Assets/Brand/hs2-manager.png` |
| 64 × 64 PNG | `src/HS2Manager.NativeApp/Assets/Brand/hs2-manager-64.png` |
| Windows 图标 | `src/HS2Manager.NativeApp/Assets/Brand/hs2-manager.ico` |

ICO 包含 16、20、24、32、40、48、64、128、256 px 九个尺寸，均保留透明通道。程序左上角使用 PNG；可执行文件、窗口标题栏与任务栏使用 ICO。构建与发布自动携带所需资产。

原稿由 SVG 路径绘制，无外部字体或图片依赖。内置图像生成服务本次调用失败，最终资产使用本地矢量绘制完成，未调用备用付费 API。设计规格为「叠放收藏卡、定制 H 字形、墨绿 / 象牙白 / 暖金、简洁几何形状、透明外边缘」。

修改 SVG 后，在 Windows PowerShell 中运行以下命令可重新生成 PNG 和 ICO，无需安装第三方库：

```powershell
./scripts/Export-BrandAssets.ps1
```

导出脚本支持本原稿使用的路径、旋转与渐变。更复杂的 SVG 元素需要同步扩展脚本。请保持图标比例和留白，不添加外置阴影或拉伸。

历史 C# 界面验证（2026-11-08）：`LogoAudit` 配置构建成功（0 警告、0 错误），已有 WinUI 布局检查通过（浅色、深色、系统主题，三种窗口尺寸）。目视确认左上角标识与 EXE 提取图标正常；校验 PNG 尺寸、透明边缘和 ICO 的九个尺寸。检查使用独立测试数据，结束后关闭测试进程。

标识资产由 C++ 版本继续使用；本次应用构建和界面检查结果见 [C++ 验证结果](CPP-VALIDATION.md)。
