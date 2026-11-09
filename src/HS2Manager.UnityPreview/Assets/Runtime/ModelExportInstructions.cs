namespace HS2Manager.UnityPreview
{
    internal static class ModelExportInstructions
    {
        internal const string Text=@"FBX 可动画角色导入

文件：character.fbx、Textures、materials.json、骨骼映射.json、Blender导入.py。
请一起移动整个角色文件夹；贴图使用相对路径。FBX 为右手 Y-up、厘米，HS2 源单位按 10 厘米换算。
体形已经转换为角色的参考形态；保留原骨架、额外骨骼、权重和表情。文件不包含待机动画或物理组件。
保存的默认表情已烘焙到基准网格，导入后所有形变权重应为 0，以避免重复叠加。
如需沿用原始游戏表情控制值，先将 HS2_ResetExpressions（若存在）设为 100，再设置原始形变通道。model-result.json 同时记录导出 DefaultWeights（全 0）与 SourceDefaultWeights。

Blender 4.0 及以上（实测版本见项目验证记录）
1. 保持整个角色文件夹完整。打开 Blender 的 Scripting 工作区，在文本编辑器中选择 Open，打开本包的 Blender导入.py，然后 Run Script（Alt+P）。
2. 脚本会导入 character.fbx 到一个新的角色集合，自动连接 PBR 贴图、设置 sRGB / Non-Color、OpenGL 法线、透明阈值和双面材质。无需先手动导入 FBX；重复运行会新增一个角色。
3. 使用 Material Preview 或 Rendered 查看；Solid 模式不显示完整材质。脚本保留现有场景、灯光和色彩管理。
4. Skin 使用少量皮肤散射，Hair 使用 UV 切线各向异性，Eye 使用清漆层，EyeShadow 使用乘色透明节点。这些均是可编辑近似；AO influence 节点控制额外遮蔽，0 可关闭。
5. 灯光、曝光和 AgX / Standard 等视图变换会改变观感。做颜色对照时先统一灯光和曝光；不能把材质预览默认 HDRI 当作游戏环境。
6. 导入后另存 .blend。需要独立携带时使用 File > External Data > Pack Resources 将贴图打包。
命令行也可使用：blender --background --python Blender导入.py -- 角色文件夹

Unity 2022.3 LTS / Unity 6 URP
1. 将整个角色文件夹复制到 Assets。Model 保持 Convert Units，Scale Factor=1；开启 Import BlendShapes。
2. Rig 选择 Humanoid、Create From This Model，按骨骼映射.json 的 UnityHumanoid 列配置。保留辅助骨骼；在 Configure 中检查并校正 T-Pose，然后 Apply。
3. 新建 URP/Lit 材质，并依照 materials.json 的 Id 对应 FBX 材质槽。
4. BaseColor 接 Base Map，使用 sRGB；Normal 设置 Texture Type=Normal map，关闭 Flip Green Channel。
5. Metallic、Roughness、Occlusion 为线性数据，取消 sRGB。Unity 需要将 Roughness 取反为 Smoothness；将 Metallic 写入金属贴图 R、Smoothness 写入 A。Occlusion 使用 G 通道（本包灰度图各 RGB 相同）。
6. AlphaMode=Mask 开启 Alpha Clipping，阈值见 AlphaCutoff；Blend 选择 Transparent；DoubleSided 开启 Render Face Both。
7. Emission 为 sRGB，接 Emission Map 并启用 Emission。使用 SkinnedMeshRenderer 的 BlendShapes 检查眨眼和嘴部形变。

Unreal Engine 5（验收基线 5.6）
1. 导入 character.fbx，开启 Skeletal Mesh、Import Morph Targets，创建新 Skeleton；导入法线和切线。Import Uniform Scale=1，开启 Convert Scene / Convert Scene Unit。
2. 将 BaseColor、Normal、Metallic、Roughness、Occlusion、Emission 分别接入同名材质输入。BaseColor/Emission 开启 sRGB，其余关闭。
3. 法线来自 OpenGL +Y，Normal 贴图开启 Flip Green Channel，避免凹凸反向。Mask 选择 Masked，并将 BaseColor 的 A 接 Opacity Mask；Blend 选择 Translucent 并接 Opacity；DoubleSided 开启 Two Sided。
4. 按骨骼映射.json 的 Unreal 列创建 IK Rig 链，Hips/pelvis 为重定向根；建立 IK Retargeter，校正源与目标参考姿态后再使用现成人形动画。
5. 在 Skeletal Mesh 编辑器测试 Morph Targets。额外骨骼由自己的动画或物理方案控制。

材质说明
本包是通用 metallic/roughness PBR 近似。专用皮肤散射、眼球折射、头发各向异性、描边、MatCap 和屏幕后处理需要在目标引擎另行设置。
导出报告列出缺失和未知 Shader；存在警告时请检查对应部件。未发现目标编辑器时不代表已经通过目标引擎验收。
";
    }
}
