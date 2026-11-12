using System;
using System.Collections;
using System.IO;
using System.Linq;
using AIChara;
using BepInEx;
using UnityEngine;

// 仅放入独立验收副本，验证真实 HS2 的读卡和角色装配，不进入发布包。
[BepInPlugin("hs2manager.agent.acceptance", "HS2 Agent Acceptance", "1.0.0")]
[BepInDependency("com.bepis.bepinex.sideloader")]
[BepInDependency("com.bepis.bepinex.extendedsave")]
public class GameAssetChecks : BaseUnityPlugin
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [Serializable] public class Item { public string path, kind, target; public int slot; }
    [Serializable] public class Input { public Item[] items; }
    [Serializable] public class Result { public string path, kind, target, sex, error, preview; public bool card_loaded, character_loaded, generated_part_loaded, deformation_checked, geometry_checked, dynamic_checked, body_variation_checked, masks_checked; public int renderers, vertices, clothing_states, overlay_width, overlay_height; }
    [Serializable] public class Report { public bool passed; public Result[] items; }

    static string Quote(string value)
    {
        return "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
    }

    static Vector3[] Sample(SkinnedMeshRenderer renderer)
    {
        var used = renderer.sharedMesh.boneWeights.SelectMany(weight => new[] {
            weight.weight0 > 0 ? weight.boneIndex0 : -1, weight.weight1 > 0 ? weight.boneIndex1 : -1,
            weight.weight2 > 0 ? weight.boneIndex2 : -1, weight.weight3 > 0 ? weight.boneIndex3 : -1 }).Where(index => index >= 0).Distinct().ToArray();
        if (used.Length == 0 || used.Any(index => index >= renderer.bones.Length || renderer.bones[index] == null))
            throw new Exception("Generated skin has unresolved weighted bones: " + String.Join(",", used.Where(index => index >= renderer.bones.Length || renderer.bones[index] == null).Select(index => index.ToString()).ToArray()));
        var mesh = new Mesh();
        try
        {
            renderer.BakeMesh(mesh);
            var points = mesh.vertices.Select(point => renderer.transform.TransformPoint(point)).ToArray();
            if (points.Length == 0 || points.Any(point => Single.IsNaN(point.sqrMagnitude) || Single.IsInfinity(point.sqrMagnitude)))
                throw new Exception("Generated skin has invalid deformed vertices");
            return points;
        }
        finally { UnityEngine.Object.Destroy(mesh); }
    }

    static Bounds BoundsOf(System.Collections.Generic.IEnumerable<Vector3> points)
    {
        var values = points.ToArray();
        var bounds = new Bounds(values[0], Vector3.zero);
        foreach (var point in values) bounds.Encapsulate(point);
        return bounds;
    }

    static IEnumerator Load(ChaControl character, Result result)
    {
        var stack = new System.Collections.Generic.Stack<IEnumerator>();
        stack.Push(character.LoadAsync(false, false));
        while (stack.Count > 0)
        {
            object current = null;
            var moved = false;
            try { moved = stack.Peek().MoveNext(); if (moved) current = stack.Peek().Current; }
            catch (Exception error) { result.error = error.ToString(); yield break; }
            if (!moved) { stack.Pop(); continue; }
            var nested = current as IEnumerator;
            if (nested != null) stack.Push(nested);
            else yield return current;
        }
    }

    static void CheckDeformation(ChaControl character, GameObject part)
    {
        if (part.GetComponent<CmpClothes>() != null)
            File.WriteAllLines(Path.Combine(Path.GetDirectoryName(Application.dataPath), "game-bones.txt"), character.transform.GetComponentsInChildren<Transform>(true).Where(bone => !bone.IsChildOf(part.transform)).Select(bone => bone.name).ToArray());
        var skins = part.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        if (skins.Length == 0) throw new Exception("Generated part has no skinned mesh");
        var jointName = part.GetComponent<CmpClothes>() != null ? "cf_J_ArmUp00_L" : "cf_J_Head";
        var joint = character.transform.GetComponentsInChildren<Transform>(true)
            .FirstOrDefault(bone => bone.name == jointName && !bone.IsChildOf(part.transform));
        if (joint == null) throw new Exception("Body deformation joint was not found: " + jointName);
        var original = joint.localRotation;
        try
        {
            var before = skins.Select(Sample).ToArray();
            joint.localRotation = original * Quaternion.Euler(0, 25, 0);
            var moved = skins.Select(Sample).ToArray();
            if (!before.SelectMany((points, index) => points.Select((point, vertex) => (point - moved[index][vertex]).sqrMagnitude)).Any(distance => distance > 0.00000001f))
                throw new Exception("Generated skin did not follow the game skeleton");
        }
        finally { joint.localRotation = original; }
    }

    static void CheckBodyVariation(ChaControl character, GameObject part)
    {
        var method = typeof(ChaControl).GetMethod("SetShapeBodyValue", new[] { typeof(int), typeof(float) });
        if (method == null) throw new Exception("Game body shape API is missing");
        var skins = part.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        // HS2 fixes male height (index 0), so perturb a supported shape for each fixture.
        var index = part.GetComponent<CmpHair>() != null ? 9 : 12;
        var original = character.chaFile.custom.body.shapeValueBody[index];
        var before = skins.Select(Sample).ToArray();
        var changed = false;
        try
        {
            foreach (var offset in new[] { -0.1f, 0.1f })
            {
                method.Invoke(character, new object[] { index, Mathf.Clamp01(original + offset) });
                character.UpdateShapeBodyCalcForce();
                var after = skins.Select(Sample).ToArray();
                changed |= before.SelectMany((points, skin) => points.Select((point, vertex) => (point - after[skin][vertex]).sqrMagnitude)).Any(distance => distance > 0.00000001f);
            }
            if (!changed) throw new Exception("Generated skin did not follow game body shape changes: " + index);
        }
        finally { method.Invoke(character, new object[] { index, original }); character.UpdateShapeBodyCalcForce(); }
    }

    static void CheckDynamic(GameObject part)
    {
        var dynamics = part.GetComponentsInChildren<Component>(true).Where(value => value != null && value.GetType().Name == "DynamicBone").ToArray();
        if (dynamics.Length == 0) throw new Exception("Fixture dynamic bone component is missing");
        foreach (var dynamic in dynamics)
        {
            var root = dynamic.GetType().GetField("m_Root").GetValue(dynamic) as Transform;
            if (root == null || !root.name.StartsWith("native_strand_") || root.childCount == 0)
                throw new Exception("Fixture dynamic root was not preserved");
        }
    }

    static Texture2D ReadOverlay(ChaControl character, string target)
    {
        var typeName = target == "clothes" ? "KoiClothesOverlayX.KoiClothesOverlayController" : "KoiSkinOverlayX.KoiSkinOverlayController";
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(typeName)).FirstOrDefault(found => found != null);
        if (type == null) throw new Exception("Game overlay plugin is missing");
        var controller = character.GetComponent(type);
        if (controller == null) throw new Exception("Game overlay controller was not attached");
        if (target == "clothes")
        {
            var data = type.GetMethod("GetOverlayTex").Invoke(controller, new object[] { "ct_clothesTop", false });
            return data == null ? null : (Texture2D)data.GetType().GetProperty("Texture").GetValue(data, null);
        }
        var storage = type.GetProperty("OverlayStorage").GetValue(controller, null);
        var method = storage.GetType().GetMethod("GetTexture");
        var texType = Enum.Parse(method.GetParameters()[0].ParameterType, target == "body" ? "BodyOver" : "FaceOver");
        return (Texture2D)method.Invoke(storage, new object[] { texType });
    }

    static string Capture(ChaControl character, string path, GameObject onlyPart = null)
    {
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return null;
        var bounds = character.objBody.GetComponentsInChildren<Renderer>(true).First().bounds;
        foreach (var renderer in character.GetComponentsInChildren<Renderer>())
            if (renderer.enabled && renderer.gameObject.activeInHierarchy) bounds.Encapsulate(renderer.bounds);
        if (onlyPart != null)
            bounds = BoundsOf(onlyPart.GetComponentsInChildren<Renderer>(true).Where(renderer => renderer.gameObject.activeInHierarchy)
                .SelectMany(renderer => renderer is SkinnedMeshRenderer ? Sample((SkinnedMeshRenderer)renderer) :
                    renderer.GetComponent<MeshFilter>().sharedMesh.vertices.Select(renderer.transform.TransformPoint).ToArray()));
        var cameraObject = new GameObject("AgentAcceptanceCamera");
        var lightObject = new GameObject("AgentAcceptanceLight");
        var camera = cameraObject.AddComponent<Camera>();
        var light = lightObject.AddComponent<Light>();
        var target = new RenderTexture(512, 768, 24);
        var texture = new Texture2D(512, 768, TextureFormat.RGB24, false);
        var previous = RenderTexture.active;
        var objects = character.GetComponentsInChildren<Transform>(true).Select(transform => transform.gameObject).ToArray();
        var layers = objects.Select(obj => obj.layer).ToArray();
        try
        {
            // 为此角色单独设置拍摄层，避免启动场景中的默认角色遮住测试结果。
            foreach (var obj in objects) obj.layer = onlyPart == null || obj.transform.IsChildOf(onlyPart.transform) ? 26 : 27;
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            lightObject.transform.rotation = Quaternion.Euler(35, -25, 0);
            camera.enabled = false;
            camera.fieldOfView = 35;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.18f, 0.2f, 0.24f);
            camera.cullingMask = 1 << 26;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = 100;
            camera.transform.position = bounds.center + new Vector3(0, 0, Mathf.Max(bounds.size.y * 1.9f, 1));
            camera.transform.LookAt(bounds.center);
            camera.targetTexture = target;
            camera.Render();
            RenderTexture.active = target;
            texture.ReadPixels(new Rect(0, 0, 512, 768), 0, 0);
            texture.Apply();
            File.WriteAllBytes(path, texture.EncodeToPNG());
            if (onlyPart != null)
            {
                var pixels = texture.GetPixels32();
                var background = pixels[0];
                if (pixels.Count(pixel => Math.Abs(pixel.r - background.r) + Math.Abs(pixel.g - background.g) + Math.Abs(pixel.b - background.b) > 20) < 40)
                    throw new Exception("Generated part produced no visible game pixels: " + path);
            }
            return path;
        }
        finally
        {
            RenderTexture.active = previous;
            for (var i = 0; i < objects.Length; ++i) if (objects[i] != null) objects[i].layer = layers[i];
            camera.targetTexture = null;
            target.Release();
            UnityEngine.Object.Destroy(target);
            UnityEngine.Object.Destroy(texture);
            UnityEngine.Object.Destroy(cameraObject);
            UnityEngine.Object.Destroy(lightObject);
        }
    }

    IEnumerator Start()
    {
        Application.runInBackground = true;
        var root = Path.GetDirectoryName(Application.dataPath);
        var input = new Input { items = File.ReadAllLines(Path.Combine(root, "agent-test-input.tsv"))
            .Where(line => !String.IsNullOrEmpty(line)).Select(line => line.Split('\t'))
            .Select(columns => new Item { path = columns[0], kind = columns[1], slot = Int32.Parse(columns[2]), target = columns.Length > 3 ? columns[3] : "face" }).ToArray() };
        var results = new System.Collections.Generic.List<Result>();
        yield return new WaitForSecondsRealtime(5);
        // Optional fixture creation uses the game's default data, never a user's card.
        var bootstrapImage = Path.Combine(root, "bootstrap-thumbnail.png");
        if (File.Exists(bootstrapImage))
        {
            var baseline = new ChaFileControl();
            baseline.parameter.sex = 1;
            baseline.parameter.fullname = "Native asset female baseline";
            baseline.custom.hair.parts[0].id = 1;
            baseline.coordinate.clothes.parts[0].id = 1;
            baseline.coordinate.clothes.parts[1].id = 1;
            baseline.pngData = File.ReadAllBytes(bootstrapImage);
            if (!baseline.SaveCharaFile(Path.Combine(root, "stock-female.png"), 1, true))
                throw new Exception("Could not save the default female fixture");
        }
        // 在标题场景初始化插件后重新载入清单，避免旧版 BepInEx 在首个场景加载中竞争初始化。
        var listType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("Sideloader.ListLoader.Lists")).First(type => type != null);
        listType.GetMethod("LoadAllLists", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Invoke(null, new object[] { Manager.Character.Instance.chaListCtrl });
        foreach (var item in input.items)
        {
            var result = new Result { path = item.path, kind = item.kind, target = item.target };
            ChaControl character = null;
            try
            {
                var card = new ChaFileControl();
                result.card_loaded = card.LoadCharaFile(item.path, 255, false, true);
                if (!result.card_loaded) throw new Exception("HS2 LoadCharaFile returned false");
                result.sex = card.parameter.sex == 0 ? "male" : "female";
                character = Manager.Character.Instance.CreateChara(card.parameter.sex, null, -1, card);
                if (character == null) throw new Exception("HS2 CreateChara returned null");
            }
            catch (Exception error) { result.error = error.ToString(); }
            if (character != null && String.IsNullOrEmpty(result.error))
            {
                yield return Load(character, result);
                character.visibleAll = true;
                yield return new WaitForSecondsRealtime(1);
            }
            try
            {
                if (!String.IsNullOrEmpty(result.error)) throw new Exception(result.error);
                result.character_loaded = character.objBody != null;
                if (!result.character_loaded) throw new Exception("HS2 asynchronous body assembly failed");
                File.AppendAllText(Path.Combine(root, "agent-test-diagnostics.txt"), "Agent card " + item.kind + " hair ids=" + String.Join(",", character.fileHair.parts.Select(hairPart => hairPart.id.ToString()).ToArray()) + " hair objects=" + String.Join(",", character.objHair.Select(obj => obj == null ? "null" : obj.name).ToArray()) + " accessory ids=" + String.Join(",", character.nowCoordinate.accessory.parts.Select(accessoryPart => accessoryPart.id.ToString()).ToArray()) + "\n");
                var part = item.kind == "hair" ? character.objHair[item.slot] :
                    item.kind == "clothes" ? character.objClothes[item.slot] :
                    item.kind == "accessory" ? character.objAccessory[item.slot] : item.target == "clothes" ? character.objClothes[0] : item.target == "body" ? character.objBody : character.objHead;
                if (part == null) throw new Exception("Generated part was not instantiated");
                File.AppendAllText(Path.Combine(root, "agent-part-diagnostics.txt"), item.kind + " " + item.target + " " + part.name + " " + String.Join(";", part.GetComponentsInChildren<Renderer>(true).Select(renderer => renderer.name + ":" + renderer.enabled + ":" + renderer.gameObject.activeInHierarchy + ":" + renderer.bounds).ToArray()) + "\n");
                if (item.kind != "texture")
                {
                    var points = new System.Collections.Generic.List<Vector3>();
                    foreach (var renderer in part.GetComponentsInChildren<Renderer>(true))
                    {
                        var skin = renderer as SkinnedMeshRenderer;
                        var sample = skin != null ? Sample(skin) : renderer.GetComponent<MeshFilter>().sharedMesh.vertices.Select(renderer.transform.TransformPoint).ToArray();
                        points.AddRange(sample);
                        var actual = BoundsOf(sample);
                        File.AppendAllText(Path.Combine(root, "agent-part-diagnostics.txt"), "actual=" + actual + " transform=" + renderer.transform.position + " scale=" + renderer.transform.lossyScale + " local=" + renderer.transform.localScale + "\n");
                    }
                    var generated = BoundsOf(points);
                    var body = character.objBody.GetComponentsInChildren<SkinnedMeshRenderer>(true).First(renderer => renderer.name.Contains("o_body"));
                    var bodyBounds = BoundsOf(Sample(body));
                    var ratio = generated.size.magnitude / bodyBounds.size.magnitude;
                    if (ratio < (item.kind == "clothes" ? 0.5f : 0.02f) || ratio > 2 || Vector3.Distance(generated.center, bodyBounds.center) > bodyBounds.size.magnitude)
                        throw new Exception("Generated fixture world size or placement differs from reference body: ratio=" + ratio + " bounds=" + generated);
                    result.geometry_checked = true;
                }
                // 游戏会将 Prefab 根重命名为 ct_hairB、ct_clothesTop 等，按已解析清单确认来源。
                var category = item.kind == "hair" ? 300 + item.slot : item.kind == "clothes" ? (character.chaFile.parameter.sex == 0 ? 140 : 240) + item.slot : character.nowCoordinate.accessory.parts[item.slot].type;
                var id = item.kind == "hair" ? character.fileHair.parts[item.slot].id : item.kind == "clothes" ? character.nowCoordinate.clothes.parts[item.slot].id : character.nowCoordinate.accessory.parts[item.slot].id;
                var info = character.lstCtrl.GetListInfo((ChaListDefine.CategoryNo)category, id);
                result.generated_part_loaded = item.kind == "texture" || (info != null && info.GetInfo(ChaListDefine.KeyType.MainAB).StartsWith("hs2agent/"));
                var renderers = part.GetComponentsInChildren<Renderer>(true);
                result.renderers = renderers.Length;
                foreach (var renderer in renderers)
                {
                    var skinned = renderer as SkinnedMeshRenderer;
                    var filter = renderer.GetComponent<MeshFilter>();
                    var mesh = skinned != null ? skinned.sharedMesh : filter != null ? filter.sharedMesh : null;
                    if (mesh != null) result.vertices += mesh.vertexCount;
                    if (renderer.sharedMaterials.Any(material => material == null || material.shader == null))
                        throw new Exception("Generated material or shader was not resolved");
                    if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null && renderer.sharedMaterials.Any(material => !material.shader.isSupported))
                        throw new Exception("Generated shader is not supported by the game graphics device");
                }
                if (!result.generated_part_loaded || result.renderers == 0 || result.vertices == 0)
                    throw new Exception("Generated part geometry was not loaded");
                if (item.kind == "clothes")
                {
                    var maskName = info.GetInfo((ChaListDefine.KeyType)Enum.Parse(typeof(ChaListDefine.KeyType), "OverBodyMask"));
                    var maskBundle = info.GetInfo((ChaListDefine.KeyType)Enum.Parse(typeof(ChaListDefine.KeyType), "OverBodyMaskAB"));
                    if (String.IsNullOrEmpty(maskName) || !maskBundle.StartsWith("hs2agent/"))
                        throw new Exception("Generated clothing mask reference is missing");
                    var mask = CommonLib.LoadAsset<Texture2D>(maskBundle, maskName, false, "abdata");
                    if (mask == null || mask.width == 0 || mask.height == 0)
                        throw new Exception("Game did not decode the generated clothing mask");
                    result.masks_checked = true;
                }
                if (item.kind == "texture")
                {
                    var texture = ReadOverlay(character, item.target);
                    if (texture == null || texture.width == 0 || texture.height == 0) throw new Exception("Game did not decode the generated overlay");
                    result.overlay_width = texture.width;
                    result.overlay_height = texture.height;
                }
                if (item.kind == "hair" || item.kind == "clothes")
                {
                    CheckDeformation(character, part);
                    result.deformation_checked = true;
                    CheckBodyVariation(character, part);
                    result.body_variation_checked = true;
                    if (item.kind == "hair") { CheckDynamic(part); result.dynamic_checked = true; }
                }
            }
            catch (Exception error) { result.error = error.ToString(); }
            if (item.kind == "clothes" && String.IsNullOrEmpty(result.error))
                for (byte state = 0; state < 3; ++state)
                {
                    character.SetClothesState(item.slot, state, true);
                    yield return null;
                    try
                    {
                        var component = character.cmpClothes[item.slot];
                        var full = component.objTopDef != null ? component.objTopDef : component.objBotDef;
                        var half = component.objTopHalf != null ? component.objTopHalf : component.objBotHalf;
                        if (full == null || half == null || full.activeSelf != (state == 0) || half.activeSelf != (state == 1))
                            throw new Exception("Generated clothing did not switch full/half/off visibility: state=" + state +
                                " full=" + (full == null ? "null" : full.activeSelf.ToString()) + " half=" + (half == null ? "null" : half.activeSelf.ToString()));
                        ++result.clothing_states;
                    }
                    catch (Exception error) { result.error = error.ToString(); break; }
                }
            if (String.IsNullOrEmpty(result.error))
            {
                character.SetClothesState(item.slot, 0, true);
                yield return null;
                try
                {
                    result.preview = Capture(character, Path.Combine(root, "agent-" + item.kind + "-" + item.target + "-" + result.sex + ".png"));
                    if (item.kind != "texture")
                    {
                        var isolated = item.kind == "hair" ? character.objHair[item.slot] : item.kind == "clothes" ? character.objClothes[item.slot] : character.objAccessory[item.slot];
                        Capture(character, Path.Combine(root, "agent-" + item.kind + "-only-" + result.sex + ".png"), isolated);
                    }
                }
                catch (Exception error) { result.error = error.ToString(); }
            }
            if (character != null && Manager.Character.Instance != null) Manager.Character.Instance.DeleteChara(character, true);
            results.Add(result);
            // 游戏的已构建类型树不包含测试 DLL 中的嵌套类型，不能用 JsonUtility 省略数组后冒充完整报告。
            var passed = results.Count == input.items.Length && results.All(row => String.IsNullOrEmpty(row.error));
            var rows = results.Select(row => "{\"path\":" + Quote(row.path) + ",\"kind\":" + Quote(row.kind) + ",\"target\":" + Quote(row.target) + ",\"sex\":" + Quote(row.sex) + ",\"preview\":" + Quote(row.preview) +
                ",\"error\":" + Quote(row.error) + ",\"card_loaded\":" + row.card_loaded.ToString().ToLowerInvariant() +
                ",\"character_loaded\":" + row.character_loaded.ToString().ToLowerInvariant() +
                ",\"generated_part_loaded\":" + row.generated_part_loaded.ToString().ToLowerInvariant() +
                ",\"renderers\":" + row.renderers + ",\"vertices\":" + row.vertices +
                ",\"deformation_checked\":" + row.deformation_checked.ToString().ToLowerInvariant() + ",\"geometry_checked\":" + row.geometry_checked.ToString().ToLowerInvariant() + ",\"dynamic_checked\":" + row.dynamic_checked.ToString().ToLowerInvariant() + ",\"body_variation_checked\":" + row.body_variation_checked.ToString().ToLowerInvariant() + ",\"masks_checked\":" + row.masks_checked.ToString().ToLowerInvariant() + ",\"clothing_states\":" + row.clothing_states + ",\"overlay_width\":" + row.overlay_width + ",\"overlay_height\":" + row.overlay_height + "}");
            File.WriteAllText(Path.Combine(root, "agent-test-result.json"), "{\"passed\":" + passed.ToString().ToLowerInvariant() + ",\"items\":[" + String.Join(",", rows.ToArray()) + "]}");
            yield return null;
        }
        // 独立验收进程只在报告落盘后结束，避免游戏插件退出回调阻塞测试和遗留进程。
        TerminateProcess(new IntPtr(-1), 0);
    }
}
