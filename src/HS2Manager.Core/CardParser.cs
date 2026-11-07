using System.Buffers.Binary;
using System.Text;

namespace HS2Manager.Core;

public sealed class CardParser
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    public CardRecord Parse(string path, CancellationToken cancellation = default)
    {
        var result = new CardRecord { Path = System.IO.Path.GetFullPath(path), Name = System.IO.Path.GetFileNameWithoutExtension(path) };
        try
        {
            var file = new FileInfo(path); result.Size = file.Length; result.ModifiedUtcTicks = file.LastWriteTimeUtc.Ticks;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.RandomAccess);
            result.PngLength = FindPngEnd(stream, cancellation);
            if (stream.Position == stream.Length) { result.Status = CardStatus.ImageOnly; return result; }
            using var binary = new BinaryReader(stream, Encoding.UTF8, true);
            if (stream.Length - stream.Position < 5) throw new InvalidDataException("人物卡附加数据截断");
            int envelope = binary.ReadInt32();
            if (envelope < 0 || envelope > 1000) { result.Status = CardStatus.Unsupported; result.Error = "无法识别的附加数据"; return result; }
            string marker = ReadString(binary);
            if (marker != "【AIS_Chara】" && marker != "〖AIS_Chara〗") { result.Status = CardStatus.Unsupported; result.Error = "不是 HS2 / AI 人物卡：" + marker; return result; }
            result.Version = ReadString(binary); _ = binary.ReadInt32(); _ = ReadString(binary); _ = ReadString(binary);
            int tableSize = binary.ReadInt32();
            if (tableSize < 1 || tableSize > 1024 * 1024 || tableSize > stream.Length - stream.Position) throw new InvalidDataException("人物数据块目录损坏");
            long tableEnd = stream.Position + tableSize;
            var header = new BoundedMessagePack(stream, tableEnd).Read() as Dictionary<string, object?> ?? throw new InvalidDataException("人物块目录无效");
            stream.Position = tableEnd;
            long dataLength = binary.ReadInt64(), dataStart = stream.Position;
            if (dataLength < 0 || dataLength > stream.Length - dataStart) throw new InvalidDataException("人物数据长度越界");
            if (!header.TryGetValue("lstInfo", out var value) || value is not List<object?> blocks) throw new InvalidDataException("缺少人物数据块目录");
            bool hasParameter = false;
            foreach (var raw in blocks)
            {
                cancellation.ThrowIfCancellationRequested();
                if (raw is not Dictionary<string, object?> block) throw new InvalidDataException("人物数据块无效");
                string name = GetText(block, "name"); long offset = GetLong(block, "pos"), length = GetLong(block, "size");
                if (offset < 0 || length < 0 || offset > dataLength || length > dataLength - offset) throw new InvalidDataException("人物数据块越界");
                stream.Position = dataStart + offset;
                if (name == "Parameter")
                {
                    var parameter = new BoundedMessagePack(stream, stream.Position + length, 1024 * 1024).Read() as Dictionary<string, object?>;
                    if (parameter == null) throw new InvalidDataException("无法读取人物参数");
                    result.Name = GetText(parameter, "fullname"); if (string.IsNullOrWhiteSpace(result.Name)) result.Name = System.IO.Path.GetFileNameWithoutExtension(path);
                    result.Sex = (int)GetLong(parameter, "sex"); hasParameter = true;
                }
                else if (name == "KKEx")
                {
                    try { ReadDependencies(stream, stream.Position + length, result, cancellation); }
                    catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or OverflowException)
                    { result.DependenciesComplete = false; result.Error = "部分扩展依赖无法解析：" + ex.Message; }
                }
            }
            if (!hasParameter) throw new InvalidDataException("缺少 Parameter 数据块");
            result.Status = CardStatus.Valid;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            result.Status = ex is UnauthorizedAccessException || (ex is IOException && ex is not InvalidDataException && ex is not EndOfStreamException) ? CardStatus.Unreadable : CardStatus.Corrupt;
            result.Error = ex.Message;
        }
        return result;
    }
    public static long FindPngEnd(Stream stream, CancellationToken cancellation = default)
    {
        Span<byte> signature = stackalloc byte[8]; stream.ReadExactly(signature);
        if (!signature.SequenceEqual(PngSignature)) throw new InvalidDataException("PNG 文件头无效");
        Span<byte> chunk = stackalloc byte[8]; bool first = true;
        for (int i = 0; i < 100000; i++)
        {
            cancellation.ThrowIfCancellationRequested(); stream.ReadExactly(chunk);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(chunk);
            string type = Encoding.ASCII.GetString(chunk[4..]);
            if (length > stream.Length - stream.Position - 4) throw new InvalidDataException("PNG 数据块截断");
            if (first && (type != "IHDR" || length != 13)) throw new InvalidDataException("PNG 缺少 IHDR");
            first = false;
            stream.Seek((long)length + 4, SeekOrigin.Current);
            if (type == "IEND") { if (length != 0) throw new InvalidDataException("PNG IEND 无效"); return stream.Position; }
        }
        throw new InvalidDataException("PNG 数据块过多");
    }
    public static async Task CopyCoverAsync(string path, long pngLength, string output, CancellationToken cancellation)
    {
        if (pngLength < 1 || pngLength > 32 * 1024 * 1024) return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output)!);
        var temporary = output + ".tmp";
        try
        {
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, true);
            await using (var destination = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            {
                byte[] buffer = new byte[65536]; long left = pngLength;
                while (left > 0)
                {
                    int n = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), cancellation);
                    if (n == 0) throw new EndOfStreamException();
                    await destination.WriteAsync(buffer.AsMemory(0, n), cancellation); left -= n;
                }
            }
            File.Move(temporary, output, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string ReadString(BinaryReader reader)
    {
        int length = 0, shift = 0;
        while (true)
        {
            byte b = reader.ReadByte(); if (shift > 28) throw new InvalidDataException("字符串长度损坏");
            length |= (b & 127) << shift; if ((b & 128) == 0) break; shift += 7;
        }
        if (length < 0 || length > 65536 || length > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("字符串长度越界");
        return Encoding.UTF8.GetString(reader.ReadBytes(length));
    }
    private static void ReadDependencies(Stream stream, long end, CardRecord result, CancellationToken cancellation)
    {
        var reader = new BoundedMessagePack(stream, end);
        int count = reader.MapCount();
        for (int i = 0; i < count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            string key = reader.Read() as string ?? throw new InvalidDataException("扩展标识无效"); result.PluginDataIds.Add(key);
            if (key is "com.bepis.sideloader.universalautoresolver" or "EC.Core.Sideloader.UniversalAutoResolver") ExtractReferences(reader.Read(), result.Dependencies, 0);
            else reader.Skip();
        }
        result.Dependencies = result.Dependencies.Distinct().ToList();
    }
    private static void ExtractReferences(object? value, List<ModReference> output, int depth)
    {
        if (depth > 20) return;
        if (value is byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            ExtractReferences(new BoundedMessagePack(stream, stream.Length).Read(), output, depth + 1);
        }
        else if (value is Dictionary<string, object?> dict)
        {
            if (dict.TryGetValue("ModID", out var guid) && guid is string id && id.Length > 0)
                output.Add(new ModReference(id, (int)GetLong(dict, "Slot", -1), (int)GetLong(dict, "CategoryNo", -1), GetText(dict, "Property")));
            else foreach (var child in dict.Values) ExtractReferences(child, output, depth + 1);
        }
        else if (value is List<object?> list) foreach (var child in list) ExtractReferences(child, output, depth + 1);
    }
    private static string GetText(Dictionary<string, object?> map, string key) => map.TryGetValue(key, out var value) ? value?.ToString() ?? "" : "";
    private static long GetLong(Dictionary<string, object?> map, string key, long fallback = 0) => map.TryGetValue(key, out var value) && value is long number ? number : fallback;
}
