using System.Diagnostics;
using System.Text.Json;
using HS2Manager.BuildTools;
using HS2Manager.Preview.Contracts;
using Microsoft.Win32;

namespace HS2Manager.Core;

public enum ModelExportTarget { GenericFbx, Blender, Unreal }

public static class ModelExportTools
{
    public static string Name(ModelExportTarget target) => target switch
    {
        ModelExportTarget.GenericFbx => "通用 3D 模型（FBX）",
        ModelExportTarget.Blender => "Blender 3D 模型",
        ModelExportTarget.Unreal => "Unreal 3D 模型",
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    public static string Detect(ModelExportTarget target)
    {
        if (target == ModelExportTarget.GenericFbx) return "";
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new List<string>();
        try
        {
            string root = Path.Combine(programFiles, target == ModelExportTarget.Blender ? "Blender Foundation" : "Epic Games");
            if (Directory.Exists(root))
                candidates.AddRange(Directory.EnumerateDirectories(root).OrderDescending(StringComparer.OrdinalIgnoreCase)
                    .Select(d => target == ModelExportTarget.Blender ? Path.Combine(d, "blender.exe") : Path.Combine(d, "Engine", "Binaries", "Win64", "UnrealEditor-Cmd.exe")));
            if (target == ModelExportTarget.Unreal)
            {
                using var builds = Registry.CurrentUser.OpenSubKey(@"Software\Epic Games\Unreal Engine\Builds");
                if (builds != null)
                    foreach (string name in builds.GetValueNames())
                        if (builds.GetValue(name) is string location) candidates.Add(Path.Combine(location, "Engine", "Binaries", "Win64", "UnrealEditor-Cmd.exe"));
            }
            else
            {
                using var uninstall = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall != null)
                    foreach (string name in uninstall.GetSubKeyNames())
                    {
                        using var entry = uninstall.OpenSubKey(name);
                        if (entry?.GetValue("DisplayName") is string display && display.StartsWith("Blender", StringComparison.OrdinalIgnoreCase)
                            && entry.GetValue("InstallLocation") is string location) candidates.Add(Path.Combine(location, "blender.exe"));
                    }
                foreach (string location in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") })
                    if (Directory.Exists(location))
                        candidates.AddRange(Directory.EnumerateDirectories(location, "blender*", SearchOption.TopDirectoryOnly)
                            .OrderDescending(StringComparer.OrdinalIgnoreCase).Select(d => Path.Combine(d, "blender.exe")));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        foreach (string path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(path.Trim('"'), target == ModelExportTarget.Blender ? "blender.exe" : "UnrealEditor-Cmd.exe"));
        return candidates.FirstOrDefault(File.Exists) ?? "";
    }

    public static string Resolve(ModelExportOptions options)
    {
        if (options.Target == ModelExportTarget.GenericFbx) return "";
        string configured = options.Target == ModelExportTarget.Blender ? options.BlenderPath : options.UnrealEditorPath;
        string path = string.IsNullOrWhiteSpace(configured) ? Detect(options.Target) : configured.Trim().Trim('"');
        if (options.Target == ModelExportTarget.Unreal && Path.GetFileName(path).Equals("UnrealEditor.exe", StringComparison.OrdinalIgnoreCase))
            path = Path.Combine(Path.GetDirectoryName(path)!, "UnrealEditor-Cmd.exe");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException($"未找到 {Name(options.Target)} 所需的软件。请在设置 → 模型导出中选择{(options.Target == ModelExportTarget.Blender ? " blender.exe" : " UnrealEditor-Cmd.exe")}。", path);
        return Path.GetFullPath(path);
    }
}

internal sealed class NativeModelExporter(ModelExportOptions options, string executable, string workspace, string logDirectory, string? cacheDirectory = null)
{
    private readonly string scripts = Path.Combine(workspace, "converter");
    private string ScriptName => options.Target == ModelExportTarget.Blender ? "BlenderNative.py" : "UnrealNative.py";
    private readonly JsonSerializerOptions json = new(UnityCharacterService.JsonOptions) { WriteIndented = true };

    internal async Task ProbeAsync(CancellationToken cancellation)
    {
        Directory.CreateDirectory(scripts);
        foreach (string name in new[] { ScriptName, options.Target == ModelExportTarget.Blender ? "BlenderImport.py" : "UnrealImport.py" })
        {
            using var resource = typeof(NativeModelExporter).Assembly.GetManifestResourceStream("HS2Manager.Core.ModelExport." + name)
                ?? throw new IOException("原生导出组件缺失：" + name);
            await using var output = File.Create(Path.Combine(scripts, name));
            await resource.CopyToAsync(output, cancellation);
        }
        string probe = Path.Combine(workspace, "probe");
        Directory.CreateDirectory(probe);
        if (options.Target == ModelExportTarget.Unreal) CreateProject(probe);
        await RunAsync("probe", "", probe, null, cancellation);
    }

    internal async Task ConvertAsync(string source, string output, ModelExportResult result, CancellationToken cancellation)
    {
        if (options.Target == ModelExportTarget.Unreal) CreateProject(output);
        using var materials = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(source, "materials.json"), cancellation));
        string[] ids = materials.RootElement.GetProperty("Materials").EnumerateArray().Select(m => m.GetProperty("Id").GetString()!).ToArray();
        await RunAsync("convert", source, output, ids, cancellation);
        // Reopen in a fresh process. The validator is given no source package path.
        var verified = await RunAsync("verify", "", output, ids, cancellation);
        result.Target = options.Target.ToString();
        result.ToolVersion = verified.GetProperty("ToolVersion").GetString() ?? "";
        result.EntryPoint = options.Target == ModelExportTarget.Blender ? "character.blend" : "HS2Character.uproject";
        if (verified.TryGetProperty("Warnings", out var warnings))
            result.Warnings = result.Warnings.Concat(warnings.EnumerateArray().Select(w => w.GetString()!)).Distinct().ToArray();
        await File.WriteAllTextAsync(Path.Combine(output, "native-result.json"), verified.GetRawText(), cancellation);
        foreach (string name in new[] { "骨骼映射.json", "预览参考.png" })
            if (File.Exists(Path.Combine(source, name))) File.Copy(Path.Combine(source, name), Path.Combine(output, name));
        if (options.Target == ModelExportTarget.Unreal)
        {
            foreach (string name in new[] { "Saved", "Intermediate", "DerivedDataCache" }) DeleteOwnedDirectory(output, Path.Combine(output, name));
            string configPath = Path.Combine(output, "Config", "DefaultEngine.ini");
            if (!string.IsNullOrEmpty(cacheDirectory))
                File.WriteAllText(configPath, File.ReadAllText(configPath).Replace(Path.GetFullPath(cacheDirectory).Replace('\\', '/'), "%ENGINEVERSIONAGNOSTICUSERDIR%DerivedDataCache/HS2Manager", StringComparison.Ordinal));
        }
        string instructions = options.Target == ModelExportTarget.Blender
            ? "打开 character.blend。贴图和环境资源已打包；默认使用 Eevee 和场景灯光。角色、骨架与预览场景分组，可单独隐藏灯光和相机。材质节点、骨架和表情均可编辑。\n"
            : "使用生成版本的 Unreal Engine 打开 HS2Character.uproject，默认关卡为 Preview。角色资产位于 Content/HS2Character，可通过 Asset Actions → Migrate 迁移到自己的项目。预览设置只属于本项目。\n";
        await File.WriteAllTextAsync(Path.Combine(output, "导入说明.txt"), instructions + "生成版本：" + result.ToolVersion +
            "\n保留参考姿态、骨架、蒙皮和默认表情；不包含待机动画或物理组件。专用材质及跨引擎光照存在近似，见导出报告。无需运行 Python 脚本。\n", cancellation);
    }

    internal Task<JsonElement> VerifyAsync(string output, string[] materialIds, CancellationToken cancellation)
        => RunAsync("verify", "", output, materialIds, cancellation);

    private async Task<JsonElement> RunAsync(string mode, string source, string output, string[]? materialIds, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        string resultPath = Path.Combine(scripts, mode + "-result.json");
        if (File.Exists(resultPath)) File.Delete(resultPath);
        await File.WriteAllTextAsync(Path.Combine(scripts, "request.json"), JsonSerializer.Serialize(new
        {
            Mode = mode, Source = source, Output = output, Result = resultPath, MaterialIds = materialIds ?? [],
        }, json), cancellation);
        Directory.CreateDirectory(logDirectory);
        string log = Path.Combine(logDirectory, options.Target + "-" + mode + "-" + Guid.NewGuid().ToString("N") + ".log");
        string script = Path.Combine(scripts, ScriptName);
        string[] args = options.Target == ModelExportTarget.Blender
            ? ["--background", "--factory-startup", "--python-exit-code", "1", "--log-file", log, "--python", script]
            : [Path.Combine(output, "HS2Character.uproject"), .. (mode == "convert" ? new[] { "-ExecutePythonScript=" + script, "-windowed", "-ResX=1280", "-ResY=960" } : new[] { "-run=pythonscript", "-script=" + script }), "-unattended", "-nop4", "-nosplash", "-nosound", "-NoLiveCoding", "-NoZenAutoLaunch", "-notrace", "-notraceserver", "-ddc=DerivedDataBackendGraph", "-abslog=" + log];
        int code;
        try
        {
            code = await Task.Run(() => OwnedToolProcess.Run(executable, args, output, options.ToolTimeoutSeconds, null!, null, cancellation), CancellationToken.None);
        }
        catch (TimeoutException error) { throw new IOException("原生模型转换超时，已回收转换进程。日志：" + log, error); }
        if (!File.Exists(resultPath)) throw new IOException($"{ModelExportTools.Name(options.Target)} {mode} 失败（退出码 {code}）。日志：{log}");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath, cancellation));
        var result = document.RootElement;
        if (code != 0 || !result.TryGetProperty("Passed", out var passed) || !passed.GetBoolean())
            throw new IOException($"{ModelExportTools.Name(options.Target)} {mode} 失败：{(result.TryGetProperty("Error", out var error) ? error.GetString() : "进程退出码 " + code)}\n日志：{log}");
        return result.Clone();
    }

    private void CreateProject(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, "Config"));
        File.WriteAllText(Path.Combine(directory, "HS2Character.uproject"), "{\"FileVersion\":3,\"Plugins\":[{\"Name\":\"PythonScriptPlugin\",\"Enabled\":true},{\"Name\":\"EditorScriptingUtilities\",\"Enabled\":true}]}");
        string config = """
            [/Script/EngineSettings.GameMapsSettings]
            EditorStartupMap=/Game/HS2Character/Preview
            GameDefaultMap=/Game/HS2Character/Preview
            [/Script/Engine.RendererSettings]
            r.Substrate=False
            r.AntiAliasingMethod=4
            r.RayTracing=False
            r.AllowStaticLighting=False
            r.DefaultFeature.AutoExposure=False
            r.UsePreExposure=False
            r.DynamicGlobalIlluminationMethod=0
            r.ReflectionMethod=0
            [Zen.AutoLaunch]
            AutoLaunch=False
            [DerivedDataBackendGraph]
            Root=(Type=KeyLength, Length=120, Inner=Local)
            Local=(Type=FileSystem, ReadOnly=false, Clean=false, Flush=false, Path="%ENGINEVERSIONAGNOSTICUSERDIR%DerivedDataCache/HS2Manager")
            """;
        // A local cache avoids rebuilding engine shaders for every character. It is never
        // shipped in the project, and no external cache service is launched.
        if (!string.IsNullOrEmpty(cacheDirectory))
        {
            string cache = Path.GetFullPath(cacheDirectory).Replace('\\', '/');
            if (cache.IndexOfAny(['"', '\r', '\n']) >= 0) throw new IOException("无效的原生导出缓存路径。");
            Directory.CreateDirectory(cache);
            config = config.Replace("%ENGINEVERSIONAGNOSTICUSERDIR%DerivedDataCache/HS2Manager", cache, StringComparison.Ordinal);
        }
        File.WriteAllText(Path.Combine(directory, "Config", "DefaultEngine.ini"), config);
    }

    internal static void DeleteOwnedDirectory(string owner, string path)
    {
        string root = Path.GetFullPath(owner).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(path);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("拒绝清理工作目录之外的文件：" + target);
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }
}
