namespace HS2Manager.Export;

internal static class NativeExportInstructions
{
    internal const string Text="""
        通用 FBX 可动画角色

        请一起移动整个角色文件夹；贴图使用相对路径。FBX 为右手 Y-up、厘米，HS2 源单位按 10 厘米换算。
        体形已经转换为参考形态；保留原骨架、额外骨骼、蒙皮、表情、UV、法线和切线。不包含待机动画或物理组件。
        默认表情已烘焙到基础网格，导入后的形变权重应为 0。沿用游戏原始控制值时，先将 HS2_ResetExpressions（若存在）设为 100，再设置原始形变通道。

        按 materials.json 的 Id 匹配 FBX 材质槽。BaseColor 和 Emission 使用 sRGB；Normal、Metallic、Roughness、Occlusion 使用线性数据。
        Normal 为 OpenGL +Y；Unreal 需翻转绿色通道。AlphaMode=Mask 使用 Alpha Clipping，阈值见 AlphaCutoff；Blend 使用半透明；按 DoubleSided 设置双面。
        HairStreakMask 使用 HairStreakUv 指定的 UV 通道，按 HairStreakColor 和 HairStreakStrength 混合挑染颜色。
        参照骨骼映射.json 配置 Humanoid 或 Unreal IK Rig / Retargeter，并检查参考姿态。

        本包采用通用 PBR 材质。需要自动建立专用材质、灯光和相机时，请选择“Blender 3D 模型”或“Unreal 3D 模型”导出。
        未知 Shader、眼球折射、MatCap、描边及后处理可能存在差异；缺失资源与效果近似见导出报告。预览参考.png 保留导出参考姿态和所选视图。
        """;
}
