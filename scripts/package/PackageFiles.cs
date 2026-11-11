using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace HS2Manager.Build
{
    public sealed class PackageEntry
    {
        public string Path { get; set; }
        public long Bytes { get; set; }
        public string SHA256 { get; set; }
    }

    // 仅用于构建；正常返回或失败抛出前，等待所有工作线程结束。
    public static class PackageFiles
    {
        public static void Copy(string[] sources, string[] targets, int jobs)
        {
            if (sources.Length != targets.Length)
                throw new ArgumentException("Package source/target counts differ.");
            Parallel.For(0, sources.Length, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(targets[i]));
                File.Copy(sources[i], targets[i], false);
            });
        }

        public static PackageEntry[] Manifest(string root, string[] files, int jobs)
        {
            var prefix = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
            var result = new PackageEntry[files.Length];
            Parallel.For(0, files.Length, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
            {
                var path = System.IO.Path.GetFullPath(files[i]);
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Manifest file is outside the package.");
                using (var stream = File.OpenRead(path))
                using (var sha = SHA256.Create())
                    result[i] = new PackageEntry
                    {
                        Path = path.Substring(prefix.Length),
                        Bytes = stream.Length,
                        SHA256 = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "")
                    };
            });
            return result;
        }
    }
}
