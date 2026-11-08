namespace HS2Manager.App;

public static class DataLocation
{
    public static string Resolve(string baseDirectory, string[] arguments)
    {
        int index = Array.IndexOf(arguments, "--data");
        if (index >= 0)
        {
            if (index + 1 >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index + 1]))
                throw new ArgumentException("--data 后需要指定本地数据目录。");
            return Path.GetFullPath(arguments[index + 1]);
        }
        string current = Path.Combine(baseDirectory, "Data");
        if (Directory.Exists(current)) return current;
        // Source builds gained a Windows-version TFM and RID directory. Reuse their
        // existing data in place; portable installations still use the adjacent Data.
        var output = new DirectoryInfo(baseDirectory);
        var framework = output.Name == "win-x64" ? output.Parent : output;
        var configuration = framework?.Parent;
        var bin = configuration?.Parent;
        var project = bin?.Parent;
        if (bin?.Name == "bin" && project != null && File.Exists(Path.Combine(project.FullName, "HS2Manager.App.csproj")))
        {
            foreach (string relative in new[] { "net10.0-windows/Data", "net10.0-windows/win-x64/Data" })
            {
                string legacy = Path.GetFullPath(Path.Combine(configuration!.FullName, relative));
                if (File.Exists(Path.Combine(legacy, "library.db")) || File.Exists(Path.Combine(legacy, "settings.json"))) return legacy;
            }
        }
        return current;
    }
}
