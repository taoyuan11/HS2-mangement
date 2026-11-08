using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HS2Manager.Preview.Contracts;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace HS2Manager.UnityPreview
{
    // Only loads asset data; never instantiates a game prefab or executes its MonoBehaviours.
    public sealed class ResourceProbeRunner : MonoBehaviour
    {
        private readonly Dictionary<string, AssetBundle> bundles = new Dictionary<string, AssetBundle>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> errors = new List<string>();
        private readonly List<ResourceProbeResult> results = new List<ResourceProbeResult>();
        private string reportPath;
        private string screenshotDirectory;
        private Camera cameraComponent;
        private GameObject specimen;
        private RenderTexture target;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--session") >= 0)
            { new GameObject("Independent preview session").AddComponent<KernelRunner>(); return; }
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--character") >= 0)
            { new GameObject("Independent character check").AddComponent<CharacterCheckRunner>(); return; }
            new GameObject("Independent resource probe").AddComponent<ResourceProbeRunner>();
        }

        private IEnumerator Start()
        {
            Application.logMessageReceived += OnLog;
            var arguments = Environment.GetCommandLineArgs();
            var manifestPath = Argument(arguments, "--resource-probe");
            reportPath = Argument(arguments, "--report");
            if (string.IsNullOrEmpty(manifestPath) || string.IsNullOrEmpty(reportPath))
            {
                Debug.LogError("This build is a resource probe, not the finished preview. Use --resource-probe <manifest> --report <report>.");
                Application.Quit(2);
                yield break;
            }
            ResourceProbeManifest manifest = null;
            try
            {
                reportPath = Path.GetFullPath(reportPath);
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath));
                screenshotDirectory = Path.Combine(Path.GetDirectoryName(reportPath), Path.GetFileNameWithoutExtension(reportPath) + "-materials");
                Directory.CreateDirectory(screenshotDirectory);
                manifest = JsonUtility.FromJson<ResourceProbeManifest>(File.ReadAllText(manifestPath));
                if (manifest == null || manifest.SchemaVersion != 1 || manifest.EngineVersion != Application.unityVersion)
                    throw new IOException("Resource probe schema/Unity version mismatch.");
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
                    throw new NotSupportedException("The probe requires a Direct3D 11 graphics device; do not use -nographics.");
                if (manifest.BundlePaths == null || manifest.BundlePaths.Length == 0 || manifest.Cases == null || manifest.Cases.Length == 0)
                    throw new IOException("An empty probe cannot pass resource verification.");
                // Load every bundle before resolving references; dependency order may contain cycles.
                foreach (var path in manifest.BundlePaths)
                {
                    var full = Path.GetFullPath(path);
                    if (bundles.ContainsKey(full)) continue;
                    var bundle = AssetBundle.LoadFromFile(full);
                    if (bundle == null) throw new IOException("Cannot load original AssetBundle: " + full);
                    bundles.Add(full, bundle);
                }
                CreateRenderingFixture();
            }
            catch (Exception error) { errors.Add(error.ToString()); }
            if (errors.Count == 0 && manifest != null)
            {
                foreach (var probe in manifest.Cases)
                {
                    Probe(probe);
                    yield return null;
                }
            }
            Finish();
        }

        private void Probe(ResourceProbeCase probe)
        {
            try
            {
                AssetBundle bundle;
                if (!bundles.TryGetValue(Path.GetFullPath(probe.BundlePath), out bundle))
                    throw new IOException("Probe bundle is absent from the manifest.");
                Material[] materials;
                if (probe.AssetKind == "Material")
                {
                    var material = bundle.LoadAsset<Material>(probe.AssetName);
                    if (material == null) throw new IOException("Original material not found: " + probe.AssetName);
                    materials = new[] { material };
                }
                else if (probe.AssetKind == "GameObject")
                {
                    var prefab = bundle.LoadAsset<GameObject>(probe.AssetName);
                    if (prefab == null) throw new IOException("Original prefab not found: " + probe.AssetName);
                    foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
                        Debug.Log("Probe renderer " + probe.AssetName + "/" + renderer.name + " enabled=" + renderer.enabled + " active=" + renderer.gameObject.activeSelf + " materials=" + string.Join(",", renderer.sharedMaterials.Select(m => m == null ? "<null>" : m.name).ToArray()));
                    // Inspect the asset, not an instantiated game object. No Awake/Start from the game runs.
                    materials = prefab.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Distinct().ToArray();
                    if (materials.Length == 0) throw new IOException("Prefab has no materials.");
                }
                else throw new IOException("Unsupported probe asset kind: " + probe.AssetKind);
                foreach (var material in materials) ProbeMaterial(probe, material);
            }
            catch (Exception error)
            {
                results.Add(new ResourceProbeResult { Category = probe.Category, BundlePath = probe.BundlePath, AssetName = probe.AssetName, Error = error.ToString() });
            }
        }

        private void ProbeMaterial(ResourceProbeCase probe, Material material)
        {
            var result = new ResourceProbeResult { Category = probe.Category, BundlePath = probe.BundlePath, AssetName = probe.AssetName };
            results.Add(result);
            try
            {
                if (material == null) throw new IOException("Serialized renderer has a missing material.");
                result.MaterialName = material.name;
                if (material.shader == null || !material.shader.isSupported || material.shader.name == "Hidden/InternalErrorShader")
                    throw new IOException("Original shader is missing or unsupported for material " + material.name);
                result.ShaderName = material.shader.name;
                if (!string.IsNullOrEmpty(probe.ExpectedShaderName) && result.ShaderName != probe.ExpectedShaderName)
                    throw new IOException("Original shader was substituted: expected " + probe.ExpectedShaderName + ", got " + result.ShaderName);
                result.Keywords = material.shaderKeywords;
                result.RenderQueue = material.renderQueue;
                result.PassCount = material.passCount;
                result.TextureProperties = material.GetTexturePropertyNames();
                foreach (var property in probe.RequiredTextureProperties ?? new string[0])
                    if (!material.HasProperty(property) || material.GetTexture(property) == null)
                        throw new IOException("Serialized texture dependency not loaded: " + material.name + "." + property);
                if (result.PassCount == 0) throw new IOException("Original material has no render passes.");
                specimen.GetComponent<Renderer>().sharedMaterial = material;
                var previous = RenderTexture.active;
                Texture2D pixels = null;
                try
                {
                    cameraComponent.Render();
                    RenderTexture.active = target;
                    pixels = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
                    pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                    pixels.Apply();
                    result.Screenshot = Path.Combine(screenshotDirectory, results.Count.ToString("D4") + ".png");
                    File.WriteAllBytes(result.Screenshot, pixels.EncodeToPNG());
                }
                finally
                {
                    RenderTexture.active = previous;
                    if (pixels != null) Destroy(pixels);
                }
                result.Passed = true;
            }
            catch (Exception error) { result.Error = error.ToString(); }
        }

        private void CreateRenderingFixture()
        {
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = Color.gray;
            cameraComponent = new GameObject("Probe camera").AddComponent<Camera>();
            cameraComponent.transform.position = new Vector3(0, 0, -3);
            cameraComponent.clearFlags = CameraClearFlags.SolidColor;
            cameraComponent.backgroundColor = new Color(0.12f, 0.12f, 0.12f, 1);
            cameraComponent.enabled = false;
            target = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32);
            target.Create();
            cameraComponent.targetTexture = target;
            specimen = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            var collider = specimen.GetComponent<Collider>();
            if (collider != null) Destroy(collider);
            var light = new GameObject("Probe light").AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1;
            light.transform.rotation = Quaternion.Euler(30, -30, 0);
        }

        private void Finish()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).OrderBy(n => n).ToArray();
            // Our own runtime uses an asmdef; Assembly-CSharp therefore always indicates a game assembly here.
            foreach (var name in assemblies)
                if (name == "Assembly-CSharp" || name == "Assembly-CSharp-firstpass" || name.StartsWith("BepInEx", StringComparison.OrdinalIgnoreCase) || name == "HS2API")
                    errors.Add("Forbidden game/plugin assembly loaded: " + name);
            var report = new ResourceProbeReport
            {
                EngineVersion = Application.unityVersion,
                GraphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                Results = results.ToArray(),
                Errors = errors.ToArray(),
                LoadedAssemblies = assemblies,
                ResourceCompatibilityPassed = errors.Count == 0 && results.Count > 0 && results.All(r => r.Passed),
                VisualParityVerified = false
            };
            try { File.WriteAllText(reportPath, JsonUtility.ToJson(report, true)); }
            catch (Exception error) { Debug.LogException(error); report.ResourceCompatibilityPassed = false; }
            Cleanup();
            Application.Quit(report.ResourceCompatibilityPassed ? 0 : 1);
        }

        private void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                errors.Add(message + "\n" + stack);
        }

        private void Cleanup()
        {
            Application.logMessageReceived -= OnLog;
            if (specimen != null) Destroy(specimen);
            if (cameraComponent != null) { cameraComponent.targetTexture = null; Destroy(cameraComponent.gameObject); }
            if (target != null) { target.Release(); Destroy(target); target = null; }
            foreach (var bundle in bundles.Values) bundle.Unload(true);
            bundles.Clear();
        }

        private void OnDestroy() { Cleanup(); }
        private static string Argument(string[] args, string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }
}
