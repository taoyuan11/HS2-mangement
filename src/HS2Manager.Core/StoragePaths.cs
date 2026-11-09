using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace HS2Manager.Core
{
    // Also compiled by Windows PowerShell 5.1; keep this file compatible with C# 5.
    public static class StoragePaths
    {
        public static bool IsNetwork(string path)
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return true;
            if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) full = full.Substring(4);
            return full.StartsWith(@"\\", StringComparison.Ordinal)
                || new DriveInfo(Path.GetPathRoot(full) ?? full).DriveType == DriveType.Network;
        }

        public static string Identity(string path)
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) full = @"\\" + full.Substring(8);
            else if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) full = full.Substring(4);
            string root = Path.GetPathRoot(full) ?? "";
            if (root.Length == 3 && root[1] == ':')
            {
                string share = MappedShare(root);
                if (share.Length > 0) full = Path.Combine(share, full.Substring(root.Length));
            }
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public static string Key(string path)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))
                    .Replace("-", "").Substring(0, 24);
        }

        public static string LocalCache(string path, string category, string localAppData)
        {
            string identity = Identity(path);
            string parent = Path.Combine(localAppData, "HS2Manager", category);
            string canonical = Path.Combine(parent, Key(identity));
            if (Directory.Exists(Path.Combine(canonical, "Data"))) return canonical;
            // Reuse libraries created before UNC normalization, including when the
            // same checkout is now opened through its UNC name instead of its drive.
            foreach (string drive in Directory.GetLogicalDrives())
            {
                string share = MappedShare(drive);
                if (share.Length == 0) continue;
                string prefix = share.TrimEnd('\\') + "\\";
                if (!identity.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string legacy = Path.Combine(parent, Key(Path.Combine(drive, identity.Substring(prefix.Length))));
                if (Directory.Exists(Path.Combine(legacy, "Data"))) return legacy;
            }
            string previous = Path.Combine(parent, Key(Path.GetFullPath(path).TrimEnd('\\', '/')));
            return Directory.Exists(Path.Combine(previous, "Data")) ? previous : canonical;
        }

        private static string MappedShare(string root)
        {
            if (root.Length != 3 || root[1] != ':') return "";
            int length = 512;
            var buffer = new StringBuilder(length);
            int result = WNetGetConnection(root.Substring(0, 2), buffer, ref length);
            if (result == 234)
            {
                buffer = new StringBuilder(length);
                result = WNetGetConnection(root.Substring(0, 2), buffer, ref length);
            }
            return result == 0 ? buffer.ToString() : "";
        }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);
    }
}
