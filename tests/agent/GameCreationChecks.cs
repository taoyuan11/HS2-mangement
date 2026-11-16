using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using AIChara;
using BepInEx;
using UnityEngine;

// 仅在独立副本检验新制作类型；按人物卡分组加载，避免每张贴图重复装配人物。
[BepInPlugin("hs2manager.agent.creation.acceptance", "HS2 Creation Acceptance", "1.0.0")]
[BepInDependency("com.bepis.bepinex.sideloader")]
[BepInDependency("com.bepis.bepinex.extendedsave")]
public class GameCreationChecks : BaseUnityPlugin
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool TerminateProcess(IntPtr process, uint exitCode);

    static string Quote(string value)
    {
        return "\"" +
               (value ?? "")
                   .Replace("\\", "\\\\")
                   .Replace("\"", "\\\"")
                   .Replace("\r", "\\r")
                   .Replace("\n", "\\n")
                   .Replace("\t", "\\t") +
               "\"";
    }

    static object Value(ChaControl character, string path)
    {
        var parts = path.Split('.');
        object value = parts[0] == "face"      ? (object)character.fileFace
                       : parts[0] == "body"    ? character.fileBody
                       : parts[0] == "hair"    ? (object)character.fileHair
                       : parts[0] == "clothes" ? (object)character.nowCoordinate.clothes
                                               : character.nowCoordinate.accessory;
        foreach (var part in parts.Skip(1))
        {
            var match = Regex.Match(part, @"^(\w+)(?:\[(\d+)\])?$");
            var name = match.Groups[1].Value;
            var field = value.GetType().GetField(name);
            var property = value.GetType().GetProperty(name);
            if (field == null && property == null)
                throw new Exception("Unknown card field: " + path + " at " + name);
            value = field != null ? field.GetValue(value) : property.GetValue(value, null);
            if (match.Groups[2].Success)
                value = ((IList)value)[Int32.Parse(match.Groups[2].Value)];
        }
        return value;
    }

    static string Check(ChaControl character, string[] input)
    {
        var kind = input[1];
        var path = input[2];
        var category = Int32.Parse(input[3]);
        var textures = 0;
        var vertices = 0;
        var expressions = 0;
        if (kind == "layout" && category != 8)
        {
            // 游戏使用 Vector4 存储此类布局，能读取四个分量即证明卡片字段装配成功。
            var layout = (Vector4)Value(character, path);
            for (var i = 0; i < 4; ++i)
                if (Single.IsNaN(layout[i]) || layout[i] < 0 || layout[i] > 1)
                    throw new Exception("Invalid generated layout: " + path);
        }
        else
        {
            var id = Convert.ToInt32(Value(character, path), CultureInfo.InvariantCulture);
            var info = character.lstCtrl.GetListInfo((ChaListDefine.CategoryNo)category, id);
            if (info == null)
                throw new Exception("Generated list entry missing: " + path);
            if (path == "face.skinId" && info.GetInfoInt((ChaListDefine.KeyType)Enum.Parse(
                                             typeof(ChaListDefine.KeyType), "HeadID")) != character.fileFace.headId)
                throw new Exception("Generated face skin is not bound to the active head");
            if (kind == "layout")
            {
                foreach (var key in new[] { "CenterScale", "AddScale", "CenterX", "CenterY", "MoveX", "MoveY" })
                {
                    var text = info.GetInfo((ChaListDefine.KeyType)Enum.Parse(typeof(ChaListDefine.KeyType), key));
                    float value;
                    if (!Single.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                        Single.IsNaN(value))
                        throw new Exception("Generated body layout missing " + key);
                }
            }
            else
            {
                var bundle = info.GetInfo(ChaListDefine.KeyType.MainAB);
                if (!bundle.StartsWith("hs2agent/"))
                    throw new Exception("Game selected an existing asset: " + path);
                if (kind == "catalog_texture")
                    foreach (var field in input[4].Split(','))
                    {
                        var key = (ChaListDefine.KeyType)Enum.Parse(typeof(ChaListDefine.KeyType), field);
                        var name = info.GetInfo(key);
                        var texture = CommonLib.LoadAsset<Texture2D>(bundle, name, false, "abdata");
                        if (texture == null || texture.width == 0 || texture.height == 0)
                            throw new Exception("Game failed to decode " + path + " / " + field);
                        ++textures;
                    }
                else
                {
                    var slotMatch = Regex.Match(path, @"parts\[(\d+)\]");
                    var slot = slotMatch.Success ? Int32.Parse(slotMatch.Groups[1].Value) : 0;
                    var part = kind == "head"      ? character.objHead
                               : kind == "hair"    ? character.objHair[slot]
                               : kind == "clothes" ? character.objClothes[slot]
                                                   : character.objAccessory[slot];
                    if (part == null)
                        throw new Exception("Generated object missing: " + path);
                    foreach (var renderer in part.GetComponentsInChildren<Renderer>(true))
                    {
                        if (renderer.sharedMaterials.Any(
                                material =>
                                    material == null || material.shader == null ||
                                    (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null &&
                                     !material.shader.isSupported)))
                            throw new Exception("Unsupported generated material: " + path);
                        var skin = renderer as SkinnedMeshRenderer;
                        var filter = renderer.GetComponent<MeshFilter>();
                        var mesh = skin != null ? skin.sharedMesh : filter != null ? filter.sharedMesh : null;
                        if (mesh == null)
                            continue;
                        vertices += mesh.vertexCount;
                        expressions += mesh.blendShapeCount;
                        if (skin != null)
                        {
                            var baked = new Mesh();
                            skin.BakeMesh(baked);
                            if (baked.vertexCount != mesh.vertexCount ||
                                baked.vertices.Any(vertex => Single.IsNaN(vertex.sqrMagnitude) ||
                                                             Single.IsInfinity(vertex.sqrMagnitude)))
                                throw new Exception("Generated skin could not deform: " + path);
                            UnityEngine.Object.Destroy(baked);
                        }
                    }
                    if (vertices == 0)
                        throw new Exception("Generated mesh is empty: " + path);
                    if (kind == "head" && expressions == 0)
                        throw new Exception("Generated head lost facial expressions");
                }
            }
        }
        return ",\"textures\":" + textures + ",\"vertices\":" + vertices + ",\"expressions\":" + expressions;
    }

    IEnumerator Start()
    {
        Application.runInBackground = true;
        var root = Path.GetDirectoryName(Application.dataPath);
        var rows = File.ReadAllLines(Path.Combine(root, "agent-creation-input.tsv"))
                       .Where(line => !String.IsNullOrEmpty(line))
                       .Select(line => line.Split('\t'))
                       .ToArray();
        var results = new List<string>();
        var passed = true;
        var characterErrors = new List<string>();
        Application.logMessageReceived += (message, trace, type) =>
        {
            if ((type == LogType.Exception || type == LogType.Error) && trace.Contains("AIChara.ChaControl"))
                characterErrors.Add(message + "\n" + trace);
        };
        yield return new WaitForSecondsRealtime(5);
        var listType = AppDomain.CurrentDomain.GetAssemblies()
                           .Select(assembly => assembly.GetType("Sideloader.ListLoader.Lists"))
                           .First(type => type != null);
        listType.GetMethod("LoadAllLists", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { Manager.Character.Instance.chaListCtrl });
        foreach (var group in rows.GroupBy(row => row[0]))
        {
            characterErrors.Clear();
            ChaControl character = null;
            var error = "";
            try
            {
                var card = new ChaFileControl();
                if (!card.LoadCharaFile(group.Key, 255, false, true))
                    throw new Exception("HS2 rejected generated card");
                character = Manager.Character.Instance.CreateChara(card.parameter.sex, null, -1, card);
                if (character == null)
                    throw new Exception("HS2 could not create generated character");
            }
            catch (Exception failure)
            {
                error = failure.ToString();
            }
            if (character != null && error.Length == 0)
            {
                var stack = new Stack<IEnumerator>();
                stack.Push(character.LoadAsync(false, false));
                while (stack.Count > 0 && error.Length == 0)
                {
                    object current = null;
                    var moved = false;
                    try
                    {
                        moved = stack.Peek().MoveNext();
                        if (moved)
                            current = stack.Peek().Current;
                    }
                    catch (Exception failure)
                    {
                        error = failure.ToString();
                    }
                    if (!moved)
                    {
                        stack.Pop();
                        continue;
                    }
                    var nested = current as IEnumerator;
                    if (nested != null)
                        stack.Push(nested);
                    else
                        yield return current;
                }
                character.visibleAll = true;
                yield return new WaitForSecondsRealtime(1);
                if (characterErrors.Count > 0)
                    error = String.Join("\n", characterErrors.ToArray());
            }
            foreach (var row in group)
            {
                var itemError = error;
                var details = "";
                try
                {
                    if (itemError.Length == 0)
                    {
                        if (character.objBody == null)
                            throw new Exception("HS2 did not assemble the body");
                        details = Check(character, row);
                    }
                }
                catch (Exception failure)
                {
                    itemError = failure.ToString();
                }
                passed &= itemError.Length == 0;
                results.Add("{\"path\":" + Quote(row[0]) + ",\"kind\":" + Quote(row[1]) +
                            ",\"target\":" + Quote(row[2]) + ",\"error\":" + Quote(itemError) + details + "}");
            }
            if (character != null)
                Manager.Character.Instance.DeleteChara(character, true);
        }
        File.WriteAllText(Path.Combine(root, "agent-test-result.json"),
                          "{\"graphics_device\":" + Quote(SystemInfo.graphicsDeviceType.ToString()) +
                              ",\"passed\":" + (passed && rows.Length > 0).ToString().ToLowerInvariant() +
                              ",\"items\":[" + String.Join(",", results.ToArray()) + "]}");
        TerminateProcess(new IntPtr(-1), 0);
    }
}
