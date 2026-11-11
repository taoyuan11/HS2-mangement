using System.Buffers.Binary;
using System.Security.Cryptography;

namespace HS2Manager.Core;

/// <summary>Extracts length-delimited UnityFS data from installed files. Never loads a CLR assembly.</summary>
internal static class UnityEmbeddedBundles
{
    internal static IReadOnlyList<string> Extract(string source, string cache, CancellationToken cancellation)
    {
        var info = new FileInfo(source);
        if (!info.Exists) return [];
        if (info.Length > 128 * 1024 * 1024) throw new InvalidDataException("Shader resource file exceeds the inspection limit.");
        cancellation.ThrowIfCancellationRequested();
        byte[] data = File.ReadAllBytes(source);
        var result = new List<string>();
        ReadOnlySpan<byte> signature = "UnityFS\0"u8;
        int cursor = 0;
        while (cursor < data.Length - 48)
        {
            cancellation.ThrowIfCancellationRequested();
            int relative = data.AsSpan(cursor).IndexOf(signature);
            if (relative < 0) break;
            int start = cursor + relative; cursor = start + signature.Length;
            if (cursor + 4 >= data.Length) break;
            int version = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(cursor, 4));
            if (version is < 6 or > 8) continue;
            int position = cursor + 4;
            if (!SkipString(ref position) || !SkipString(ref position) || position + 8 > data.Length) continue;
            long length = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(position, 8));
            if (length < 48 || length > data.Length - start) continue;
            var bytes = data.AsSpan(start, (int)length);
            string key = Convert.ToHexString(SHA256.HashData(bytes));
            Directory.CreateDirectory(cache);
            string path = Path.Combine(cache, key + ".unity3d");
            if (!File.Exists(path)) File.WriteAllBytes(path, bytes.ToArray());
            else if (!SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
                File.WriteAllBytes(path, bytes.ToArray());
            result.Add(path); cursor = start + (int)length;
        }
        return result;

        bool SkipString(ref int position)
        {
            int zero = Array.IndexOf(data, (byte)0, position, Math.Min(128, data.Length - position));
            if (zero < 0) return false;
            position = zero + 1; return true;
        }
    }
}
