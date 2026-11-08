using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace HS2Manager.UnityPreview.Editor
{
    public static class PreviewBuild
    {
        public static void Build()
        {
            try
            {
                if (Application.unityVersion != "2018.4.11f1")
                    throw new InvalidOperationException("Expected Unity 2018.4.11f1, got " + Application.unityVersion);
                var output = Environment.GetEnvironmentVariable("HS2_PREVIEW_BUILD_OUTPUT");
                if (string.IsNullOrEmpty(output)) throw new InvalidOperationException("Missing build output directory.");
                Directory.CreateDirectory(output);
                Directory.CreateDirectory("Assets/Generated");
                Directory.CreateDirectory("Assets/Generated/Resources");
                const string postResources="Assets/Generated/Resources/IndependentPostProcessResources.asset";
                if(!File.Exists(postResources))
                    if(!AssetDatabase.CopyAsset("Packages/com.unity.postprocessing/PostProcessing/PostProcessResources.asset",postResources))
                        throw new IOException("Post-processing resources could not be included.");
                AssetDatabase.Refresh();
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                EditorSceneManager.SaveScene(scene, "Assets/Generated/Bootstrap.unity");
                PlayerSettings.companyName = "HS2Manager";
                PlayerSettings.productName = "HS2Manager Independent Preview";
                PlayerSettings.runInBackground = true;
                PlayerSettings.displayResolutionDialog = ResolutionDialogSetting.Disabled;
                PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
                PlayerSettings.defaultScreenWidth = 640;
                PlayerSettings.defaultScreenHeight = 480;
                PlayerSettings.colorSpace = ColorSpace.Linear;
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);
                PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.Standalone, ApiCompatibilityLevel.NET_4_6);
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { "Assets/Generated/Bootstrap.unity" },
                    locationPathName = Path.Combine(output, "HS2Manager.UnityPreview.exe"),
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.None
                });
                if (report.summary.result != BuildResult.Succeeded)
                    throw new InvalidOperationException("Unity player build failed: " + report.summary.result);
                // Do not let the application mistake the feasibility probe for a finished renderer.
                File.WriteAllText(Path.Combine(output, "kernel-status.json"),
                    "{\"stage\":\"resource-probe\",\"engineVersion\":\"2018.4.11f1\",\"visualParityVerified\":false}");
                EditorApplication.Exit(0);
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                EditorApplication.Exit(1);
            }
        }
    }
}
