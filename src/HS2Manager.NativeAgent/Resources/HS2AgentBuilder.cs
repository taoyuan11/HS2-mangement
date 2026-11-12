using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

// 只在固定版本 ModdingTool 的 Editor 进程内运行，不进入管理器或 MCP 进程。
public static class HS2AgentBuilder
{
    [Serializable]
    public class TextureSpec { public string path; public string property; }
    [Serializable]
    public class MaskSpec { public string path; public string field; }
    [Serializable]
    public class Spec
    {
        public string kind, model, guid, name, package_directory, template_material;
        public string parent, shader, clothing_name;
        public string[] dynamic_roots;
        public TextureSpec[] textures;
        public MaskSpec[] masks;
        public int category, slot_id, sex;
        public float[] position, rotation, scale;
    }

    static Type FindType(string name)
    {
        return AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(name)).FirstOrDefault(type => type != null);
    }

    static void Set(object target, string field, object value)
    {
        var info = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (info == null) throw new InvalidOperationException("工具组件缺少字段：" + field);
        info.SetValue(target, value);
    }

    static Vector3 Vector(float[] value, Vector3 fallback)
    {
        return value != null && value.Length == 3 ? new Vector3(value[0], value[1], value[2]) : fallback;
    }

    static string Csv(string value)
    {
        // Sideloader 使用 Split(',')，不支持 RFC CSV 的引号或多行转义。
        value = value ?? "";
        if (value.IndexOfAny(new[] { ',', '\r', '\n' }) >= 0)
            throw new ArgumentException("HS2 素材清单字段不能包含逗号或换行");
        return value;
    }

    static void CompactSkin(SkinnedMeshRenderer renderer, Transform root, string path)
    {
        var original = renderer.sharedMesh;
        var weights = original.boneWeights;
        var used = weights.SelectMany(weight => new[] {
            weight.weight0 > 0 ? weight.boneIndex0 : -1, weight.weight1 > 0 ? weight.boneIndex1 : -1,
            weight.weight2 > 0 ? weight.boneIndex2 : -1, weight.weight3 > 0 ? weight.boneIndex3 : -1
        }).Where(index => index >= 0).Distinct().OrderBy(index => index).ToArray();
        if (used.Length == 0 || used.Any(index => index >= renderer.bones.Length || index >= original.bindposes.Length || renderer.bones[index] == null))
            throw new InvalidOperationException("蒙皮引用无效的骨骼或绑定矩阵");
        var remap = used.Select((oldIndex, newIndex) => new { oldIndex, newIndex }).ToDictionary(pair => pair.oldIndex, pair => pair.newIndex);
        for (var i = 0; i < weights.Length; ++i)
        {
            var weight = weights[i];
            weight.boneIndex0 = weight.weight0 > 0 ? remap[weight.boneIndex0] : 0;
            weight.boneIndex1 = weight.weight1 > 0 ? remap[weight.boneIndex1] : 0;
            weight.boneIndex2 = weight.weight2 > 0 ? remap[weight.boneIndex2] : 0;
            weight.boneIndex3 = weight.weight3 > 0 ? remap[weight.boneIndex3] : 0;
            weights[i] = weight;
        }
        // 游戏会压缩无法解析的骨骼数组，却不会重排权重。移除参考 FBX 的未用骨骼，保持索引一一对应。
        var mesh = UnityEngine.Object.Instantiate(original);
        mesh.name = original.name;
        // 游戏会替换骨架。将 FBX 导入器的非单位变换烘焙进顶点，并以当前参考骨架重建绑定矩阵。
        var toRoot = root.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
        var normalMatrix = toRoot.inverse.transpose;
        mesh.vertices = original.vertices.Select(toRoot.MultiplyPoint3x4).ToArray();
        mesh.normals = original.normals.Select(normal => normalMatrix.MultiplyVector(normal).normalized).ToArray();
        mesh.tangents = original.tangents.Select(tangent => {
            var direction = toRoot.MultiplyVector(new Vector3(tangent.x, tangent.y, tangent.z)).normalized;
            return new Vector4(direction.x, direction.y, direction.z, tangent.w);
        }).ToArray();
        if (original.blendShapeCount > 0)
        {
            mesh.ClearBlendShapes();
            for (var shape = 0; shape < original.blendShapeCount; ++shape)
                for (var frame = 0; frame < original.GetBlendShapeFrameCount(shape); ++frame)
                {
                    var positions = new Vector3[original.vertexCount];
                    var normals = new Vector3[original.vertexCount];
                    var tangents = new Vector3[original.vertexCount];
                    original.GetBlendShapeFrameVertices(shape, frame, positions, normals, tangents);
                    mesh.AddBlendShapeFrame(original.GetBlendShapeName(shape), original.GetBlendShapeFrameWeight(shape, frame),
                        positions.Select(toRoot.MultiplyVector).ToArray(), normals.Select(normalMatrix.MultiplyVector).ToArray(), tangents.Select(toRoot.MultiplyVector).ToArray());
                }
        }
        // Blender FBX 的部分末端骨骼带有 100 倍单位补偿；游戏骨架不带该补偿，不能写进逆绑定矩阵。
        mesh.bindposes = used.Select(index => Matrix4x4.TRS(renderer.bones[index].position, renderer.bones[index].rotation, Vector3.one).inverse * root.localToWorldMatrix).ToArray();
        mesh.boneWeights = weights;
        renderer.bones = used.Select(index => renderer.bones[index]).ToArray();
        renderer.transform.SetParent(root, false);
        renderer.transform.localPosition = Vector3.zero;
        renderer.transform.localRotation = Quaternion.identity;
        renderer.transform.localScale = Vector3.one;
        mesh.RecalculateBounds();
        renderer.localBounds = mesh.bounds;
        AssetDatabase.CreateAsset(mesh, path);
        renderer.sharedMesh = mesh;
    }

    public static void Build()
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, "-hs2AgentSpec");
            if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("缺少构建描述");
            var spec = JsonUtility.FromJson<Spec>(File.ReadAllText(args[index + 1]));
            var folder = "Assets/HS2AgentGenerated";
            Directory.CreateDirectory(folder);
            File.Copy(spec.model, folder + "/asset.fbx", true);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var importer = (ModelImporter)AssetImporter.GetAtPath(folder + "/asset.fbx");
            // Native FBX 将一个 HS2 单位写成 10 厘米；Blender 按米回读，导入时恢复 HS2 数值单位。
            importer.globalScale = 10;
            importer.animationType = ModelImporterAnimationType.Generic;
            importer.importAnimation = false;
            importer.isReadable = true;
            importer.SaveAndReimport();
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(folder + "/asset.fbx");
            var instance = UnityEngine.Object.Instantiate(model);
            var root = new GameObject("hs2agent_" + spec.slot_id);
            // 保留 FBX 导入根上的单位和坐标轴变换。
            // 游戏会重置 Prefab 根的挂点变换，所以将规格变换保存在内部节点上。
            var content = new GameObject("asset_transform").transform;
            content.SetParent(root.transform, false);
            content.localPosition = Vector(spec.position, Vector3.zero);
            content.localEulerAngles = Vector(spec.rotation, Vector3.zero);
            content.localScale = Vector(spec.scale, Vector3.one);
            instance.transform.SetParent(content, false);
            foreach (var transform in root.GetComponentsInChildren<Transform>(true)) transform.gameObject.layer = 10;
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) throw new InvalidOperationException("素材没有渲染器");
            var skins = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (var i = 0; i < skins.Length; ++i) CompactSkin(skins[i], root.transform, folder + "/mesh-" + i + ".asset");
            var template = AssetDatabase.LoadAssetAtPath<Material>(spec.template_material);
            if (template == null) throw new InvalidOperationException("需要工程内的 HS2 兼容 template_material");
            var material = new Material(template);
            if (!String.IsNullOrEmpty(spec.shader))
            {
                var shader = Shader.Find(spec.shader);
                if (shader == null) throw new InvalidOperationException("找不到指定的 HS2 Shader");
                material.shader = shader;
            }
            if (spec.textures != null)
                for (var i = 0; i < spec.textures.Length; ++i)
                {
                    var texture = spec.textures[i];
                    var path = folder + "/texture-" + i + Path.GetExtension(texture.path);
                    File.Copy(texture.path, path, true);
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                    if (!material.HasProperty(texture.property)) throw new InvalidOperationException("材质不支持贴图属性：" + texture.property);
                    material.SetTexture(texture.property, AssetDatabase.LoadAssetAtPath<Texture2D>(path));
                }
            AssetDatabase.CreateAsset(material, folder + "/material.mat");
            foreach (var renderer in renderers) renderer.sharedMaterials = renderer.sharedMaterials.Select(old => material).ToArray();
            var componentName = spec.kind == "hair" ? "AIChara.CmpHair" : spec.kind == "clothes" ? "AIChara.CmpClothes" : "AIChara.CmpAccessory";
            var componentType = FindType(componentName);
            if (componentType == null) throw new InvalidOperationException("缺少固定版本 ModdingTool 组件：" + componentName);
            var component = root.AddComponent(componentType);
            if (spec.kind == "accessory")
            {
                Set(component, "rendNormal", renderers);
                Set(component, "rendAlpha", new Renderer[0]);
                var move = new GameObject("N_move").transform;
                move.SetParent(root.transform, false);
                var move2 = new GameObject("N_move2").transform;
                move2.SetParent(move, false);
                foreach (var renderer in renderers) renderer.transform.SetParent(move2, true);
                Set(component, "trfMove01", move);
                Set(component, "trfMove02", move2);
            }
            else if (spec.kind == "hair")
            {
                Set(component, "rendHair", renderers);
                Set(component, "rendAccessory", new Renderer[0]);
                var field = componentType.GetField("boneInfo");
                field.SetValue(component, Array.CreateInstance(field.FieldType.GetElementType(), 0));
            }
            else
            {
                if (renderers.Any(renderer => !(renderer is SkinnedMeshRenderer))) throw new InvalidOperationException("服装必须使用蒙皮网格");
                Set(component, "rendNormal01", renderers);
                Set(component, "rendNormal02", new Renderer[0]);
                Set(component, "rendNormal03", new Renderer[0]);
                var visible = new GameObject("n_clothes_def");
                visible.transform.SetParent(root.transform, false);
                foreach (var renderer in renderers) renderer.transform.SetParent(visible.transform, true);
                if (String.IsNullOrEmpty(spec.clothing_name)) throw new InvalidOperationException("必须指定 clothing_name");
                var half = UnityEngine.Object.Instantiate(visible, root.transform);
                half.name = "n_clothes_half";
                half.SetActive(false);
                var lower = spec.category == 141 || spec.category == 241;
                Set(component, lower ? "objBotDef" : "objTopDef", visible);
                Set(component, lower ? "objBotHalf" : "objTopHalf", half);
                Set(component, "rendNormal01", root.GetComponentsInChildren<Renderer>(true));
            }
            if (spec.dynamic_roots != null && spec.dynamic_roots.Length > 0)
            {
                var dynamicType = FindType("DynamicBone");
                if (dynamicType == null) throw new InvalidOperationException("工程缺少 DynamicBone");
                foreach (var name in spec.dynamic_roots)
                {
                    var bone = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(transform => transform.name == name);
                    if (bone == null) throw new InvalidOperationException("缺少动态骨骼根：" + name);
                    Set(root.AddComponent(dynamicType), "m_Root", bone);
                }
            }
            // Prefab 的文件名必须与清单 MainData 一致；Unity 保存时会用文件名重命名根对象。
            var prefab = folder + "/" + root.name + ".prefab";
            PrefabUtility.SaveAsPrefabAsset(root, prefab);
            // BuildPipeline 会将文件名转为小写；清单须使用相同大小写供 Sideloader 字典查询。
            var bundleName = ("hs2agent/" + spec.guid + ".unity3d").ToLowerInvariant();
            var bundles = Path.Combine(spec.package_directory, "abdata");
            Directory.CreateDirectory(bundles);
            var assets = new System.Collections.Generic.List<string> { prefab };
            var masks = new System.Collections.Generic.Dictionary<string, string>();
            var maskFields = new[] { "OverBodyMask", "OverBraMask", "OverBodyBMask", "OverInnerTBMask", "OverInnerBMask", "OverPanstMask" };
            if (spec.masks != null)
                for (var i = 0; i < spec.masks.Length; ++i)
                {
                    var mask = spec.masks[i];
                    if (!maskFields.Contains(mask.field)) throw new InvalidOperationException("未知服装遮罩字段");
                    var path = folder + "/mask_" + i + Path.GetExtension(mask.path);
                    File.Copy(mask.path, path, true);
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                    assets.Add(path);
                    masks.Add(mask.field, Path.GetFileNameWithoutExtension(path));
                }
            var build = new AssetBundleBuild { assetBundleName = bundleName, assetNames = assets.ToArray() };
            var manifest = BuildPipeline.BuildAssetBundles(bundles, new[] { build }, BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
            if (manifest == null) throw new InvalidOperationException("AssetBundle 构建失败");
            var list = Path.Combine(spec.package_directory, "abdata/list/characustom/" + (spec.sex == 0 ? "00" : "01"));
            Directory.CreateDirectory(list);
            var header = "ID,Kind,Name,Possess,MainManifest,MainAB,MainData,MainData02,Parent,Weights,RingOff,TexManifest,TexAB,TexD,TexC,SetHair,ThumbAB,ThumbTex";
            var row = spec.slot_id + ",0," + Csv(spec.name) + ",1,abdata," + Csv(bundleName) + "," + Csv(root.name) + "," + Csv(root.name) + "," + Csv(spec.parent) + ",1,0,abdata,0,0,0,0,0,0";
            foreach (var mask in masks) { header += "," + mask.Key + "," + mask.Key + "AB"; row += "," + Csv(mask.Value) + "," + Csv(bundleName); }
            // 游戏的 Sideloader 固定读取三行元信息，原生目录解析可以容忍缺行，不能据此省略。
            var csv = spec.category + "\r\n0\r\nhs2agent_generated\r\n" + header + "\r\n" + row + "\r\n";
            File.WriteAllText(Path.Combine(list, "hs2agent.csv"), csv, new UTF8Encoding(false));
            AssetDatabase.SaveAssets();
            UnityEngine.Object.DestroyImmediate(root);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
        }
    }
}
