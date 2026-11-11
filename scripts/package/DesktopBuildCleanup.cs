using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace HS2Manager.Build
{
    // Only for disposable desktop build directories, never the installation or its Data.
    public static class DesktopBuildCleanup
    {
        public static void Remove(string path, string parent, string expectedName)
        {
            var root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var allowedParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Regex.IsMatch(expectedName, @"\AHS2Manager-(?:staging|backup)-[0-9a-f]{32}\z") ||
                !string.Equals(Path.GetDirectoryName(root), allowedParent, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(root), expectedName, StringComparison.Ordinal))
                throw new ArgumentException("Desktop cleanup path is outside the owned build directory: " + root);

            // Validate the entire tree before deleting anything; never follow junctions or symbolic links.
            if (!ValidateTree(root))
                return;
            for (var attempt = 0; ; ++attempt)
            {
                var errors = new List<Exception>();
                RemoveTree(root, errors);
                if (errors.Count == 0)
                    return;
                if (attempt == 2)
                    throw new AggregateException("Some old build files are still in use or inaccessible: " + root, errors);
                Thread.Sleep(250);
            }
        }

        private static bool TryGetAttributes(string path, out FileAttributes attributes)
        {
            try
            {
                attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Desktop cleanup refuses links: " + path);
                return true;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            attributes = 0;
            return false;
        }

        private static bool ValidateTree(string path)
        {
            FileAttributes attributes;
            if (!TryGetAttributes(path, out attributes))
                return false;
            if ((attributes & FileAttributes.Directory) == 0)
                throw new IOException("Desktop build cleanup requires a directory: " + path);
            foreach (var entry in Directory.GetFileSystemEntries(path))
            {
                FileAttributes childAttributes;
                if (TryGetAttributes(entry, out childAttributes) && (childAttributes & FileAttributes.Directory) != 0)
                    ValidateTree(entry);
            }
            return true;
        }

        private static void RemoveTree(string path, List<Exception> errors)
        {
            try
            {
                FileAttributes attributes;
                if (!TryGetAttributes(path, out attributes))
                    return;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    foreach (var entry in Directory.GetFileSystemEntries(path))
                        RemoveTree(entry, errors);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                    Directory.Delete(path, false);
                }
                else
                {
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                    File.Delete(path);
                }
            }
            catch (IOException error) { errors.Add(error); }
            catch (UnauthorizedAccessException error) { errors.Add(error); }
        }
    }
}
