using System.Numerics;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace HS2Manager.Core;

/// <summary>
/// Reads the installed game's shape formula metadata as data. No game assembly is loaded or invoked.
/// The evaluator accepts only numeric operations, local variables and a fixed set of bone setters.
/// This keeps version-specific bone names and formulas out of the distributed application.
/// </summary>
internal sealed class NativeShapeRig : IDisposable
{
    private readonly FileStream stream;
    private readonly PEReader pe;
    private readonly MetadataReader metadata;
    private static readonly Dictionary<short, OpCode> Opcodes = typeof(OpCodes).GetFields().Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);
    public NativeShapeRig(string game)
    {
        stream = File.OpenRead(Path.Combine(game, "HoneySelect2_Data", "Managed", "Assembly-CSharp.dll"));
        try { pe = new PEReader(stream); metadata = pe.GetMetadataReader(); }
        catch { stream.Dispose(); throw; }
    }
    public void Apply(NativePrefab prefab, string recipe, byte[] animation, byte[] categories, float[] values, CancellationToken cancellation)
    {
        var typeHandle = metadata.TypeDefinitions.FirstOrDefault(h => metadata.GetString(metadata.GetTypeDefinition(h).Name) == recipe);
        if (typeHandle.IsNil) throw new NotSupportedException("此版本缺少形变规则：" + recipe);
        var type = metadata.GetTypeDefinition(typeHandle);
        var sources = Enum("SrcName"); var destinations = Enum("DstName");
        var keys = ReadKeys(animation); var parameters = sources.ToDictionary(p => p.Key, p => new ShapeValue());
        var sourceIds = sources.ToDictionary(p => p.Value, p => p.Key);
        foreach (string row in Encoding.UTF8.GetString(categories).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellation.ThrowIfCancellationRequested();
            var cells = row.Trim().Split('\t');
            if (cells.Length < 11 || !int.TryParse(cells[0], out int index) || index < 0 || index >= values.Length || !sourceIds.TryGetValue(cells[1], out int id) || !keys.TryGetValue(cells[1], out var frames)) continue;
            float rate = Math.Clamp(values[index], 0, 1) * (frames.Length - 1); int a = (int)rate, b = Math.Min(a + 1, frames.Length - 1); float t = rate - a;
            for (int i = 0; i < 9; i++)
            {
                if (cells[i + 2] == "0") continue;
                float delta = frames[b][i] - frames[a][i];
                if (i is >= 3 and < 6) delta = ((delta % 360) + 540) % 360 - 180;
                parameters[id].Values[i] = frames[a][i] + delta * t;
            }
        }
        var bones = destinations.ToDictionary(p => p.Key, p => prefab.Bones.GetValueOrDefault(p.Value) ?? new NativeBone());
        var method = type.GetMethods().Select(metadata.GetMethodDefinition).Single(m => metadata.GetString(m.Name) == "Update");
        Evaluate(pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!, parameters, bones, cancellation);

        Dictionary<int, string> Enum(string name)
        {
            var nested = type.GetNestedTypes().Select(metadata.GetTypeDefinition).Single(t => metadata.GetString(t.Name) == name);
            return nested.GetFields().Select(metadata.GetFieldDefinition).Where(f => !f.GetDefaultValue().IsNil)
                .ToDictionary(f => metadata.GetBlobReader(metadata.GetConstant(f.GetDefaultValue()).Value).ReadInt32(), f => metadata.GetString(f.Name));
        }
    }
    internal static Dictionary<string, float[][]> ReadKeys(byte[] bytes)
    {
        using var input = new MemoryStream(bytes); using var r = new BinaryReader(input);
        int count = r.ReadInt32(); if (count is < 1 or > 4096) throw new InvalidDataException("形变数据数量越界。");
        var result = new Dictionary<string, float[][]>();
        for (int i = 0; i < count; i++)
        {
            string name = r.ReadString(); int frames = r.ReadInt32();
            if (name.Length > 256 || frames is < 1 or > 1024 || (long)frames * 40 > input.Length - input.Position) throw new InvalidDataException("形变关键帧越界。");
            var keys = new float[frames][];
            for (int j = 0; j < frames; j++)
            {
                _ = r.ReadInt32(); keys[j] = new float[9];
                for (int k = 0; k < 9; k++) { float n = r.ReadSingle(); if (!float.IsFinite(n)) throw new InvalidDataException("形变数据包含无效数值。"); keys[j][k] = n; }
            }
            result.Add(name, keys);
        }
        return result;
    }
    private sealed class ShapeValue { public float[] Values { get; } = [0,0,0,0,0,0,1,1,1]; }
    private sealed record VectorValue(float[] Values, int Offset);
    private void Evaluate(byte[] il, Dictionary<int, ShapeValue> src, Dictionary<int, NativeBone> dst, CancellationToken cancellation)
    {
        if (il.Length > 128 * 1024) throw new InvalidDataException("形变规则过大。");
        using var input = new MemoryStream(il); using var r = new BinaryReader(input);
        var stack = new Stack<object>(); var locals = new object[256]; int steps = 0;
        while (input.Position < input.Length)
        {
            if (++steps > 100000) throw new InvalidDataException("形变规则运算超限。");
            if ((steps & 1023) == 0) cancellation.ThrowIfCancellationRequested();
            int op = r.ReadByte(); if (op == 0xfe) op = 0xfe00 | r.ReadByte();
            if (!Opcodes.TryGetValue(unchecked((short)op), out var code)) throw new NotSupportedException("未知形变指令。");
            object? arg = code.OperandType switch
            {
                OperandType.InlineNone => null, OperandType.ShortInlineI => (int)r.ReadSByte(), OperandType.ShortInlineR => r.ReadSingle(),
                OperandType.ShortInlineVar => (int)r.ReadByte(), OperandType.InlineVar => (int)r.ReadUInt16(),
                OperandType.InlineI or OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineBrTarget => r.ReadInt32(),
                OperandType.ShortInlineBrTarget => (int)r.ReadSByte(),
                _ => throw new NotSupportedException("不支持的形变数据操作：" + code.Name)
            };
            string name = code.Name!;
            if (name == "ret") return;
            if (name == "nop") continue;
            if (name == "ldarg.0") { stack.Push(this); continue; }
            if (name.StartsWith("ldc.i4.") && int.TryParse(name[7..], out int small)) { stack.Push((float)small); continue; }
            if (name is "ldc.i4" or "ldc.i4.s" or "ldc.r4") { stack.Push(Convert.ToSingle(arg)); continue; }
            if (name.StartsWith("stloc")) { locals[Local()] = stack.Pop(); continue; }
            if (name.StartsWith("ldloc")) { stack.Push(locals[Local()]); continue; }
            if (name is "add" or "sub" or "mul" or "div" or "and")
            {
                float b = Number(), a = Number();
                float n = name switch { "add" => a + b, "sub" => a - b, "mul" => a * b, "div" => a / b, _ => (int)a & (int)b };
                if (!float.IsFinite(n)) throw new InvalidDataException("形变公式产生无效数值。"); stack.Push(n); continue;
            }
            if (name == "neg") { stack.Push(-Number()); continue; }
            if (name is "brfalse" or "brfalse.s" or "brtrue" or "brtrue.s")
            { bool condition = Number() != 0; if (condition == name.StartsWith("brtrue")) input.Position = checked(input.Position + (int)arg!); continue; }
            if (name is "ldfld" or "ldflda")
            {
                string member = Member((int)arg!); var instance = stack.Pop();
                object value = member switch
                {
                    "dictSrc" when ReferenceEquals(instance, this) => src, "dictDst" when ReferenceEquals(instance, this) => dst,
                    "updateMask" when ReferenceEquals(instance, this) => 31f,
                    "trfBone" when instance is NativeBone => instance,
                    "vctPos" when instance is ShapeValue p => new VectorValue(p.Values, 0),
                    "vctRot" when instance is ShapeValue p => new VectorValue(p.Values, 3),
                    "vctScl" when instance is ShapeValue p => new VectorValue(p.Values, 6),
                    "x" or "y" or "z" when instance is VectorValue v => v.Values[v.Offset + (member == "x" ? 0 : member == "y" ? 1 : 2)],
                    _ => throw new NotSupportedException("不支持的形变字段：" + member)
                };
                stack.Push(value); continue;
            }
            if (name is "call" or "callvirt")
            {
                string member = Member((int)arg!);
                if (member == "get_InitEnd" && ReferenceEquals(stack.Pop(), this)) { stack.Push(1f); continue; }
                if (member == "get_Count") { stack.Pop(); stack.Push((float)src.Count); continue; }
                if (member == "get_Item")
                {
                    int id = checked((int)Number()); var dictionary = stack.Pop();
                    stack.Push(dictionary is Dictionary<int, ShapeValue> s ? s[id] : ((Dictionary<int, NativeBone>)dictionary)[id]); continue;
                }
                if (member is "SetLocalPosition" or "SetLocalRotation" or "SetLocalScale")
                {
                    float z = Number(), y = Number(), x = Number(); var bone = (NativeBone)stack.Pop();
                    if (member == "SetLocalRotation") { var q = NativeMath.Euler(new(x, y, z)); bone.Rotation = [q.X,q.Y,q.Z,q.W]; }
                    else if (member == "SetLocalScale") bone.Scale = [x,y,z]; else bone.Position = [x,y,z];
                    continue;
                }
                if (member is "SetLocalPositionX" or "SetLocalPositionY" or "SetLocalPositionZ")
                { float n = Number(); var bone = (NativeBone)stack.Pop(); bone.Position[member[^1] == 'X' ? 0 : member[^1] == 'Y' ? 1 : 2] = n; continue; }
                throw new NotSupportedException("不支持的形变运算：" + member);
            }
            throw new NotSupportedException("不支持的形变指令：" + name);
            int Local() => arg is int index ? index : int.Parse(name[(name.LastIndexOf('.') + 1)..]);
        }
        float Number() => Convert.ToSingle(stack.Pop());
    }
    private string Member(int token)
    {
        var handle = MetadataTokens.EntityHandle(token);
        return handle.Kind switch
        {
            HandleKind.FieldDefinition => metadata.GetString(metadata.GetFieldDefinition((FieldDefinitionHandle)handle).Name),
            HandleKind.MethodDefinition => metadata.GetString(metadata.GetMethodDefinition((MethodDefinitionHandle)handle).Name),
            HandleKind.MemberReference => metadata.GetString(metadata.GetMemberReference((MemberReferenceHandle)handle).Name),
            _ => throw new NotSupportedException("形变规则包含不支持的引用。")
        };
    }
    public static Dictionary<string, Matrix4x4> World(NativePrefab prefab, Matrix4x4 placement, IReadOnlyDictionary<string, Matrix4x4>? shared = null)
    {
        var result = new Dictionary<string, Matrix4x4>();
        foreach (var name in prefab.Bones.Keys) Resolve(name, new HashSet<string>());
        return result;
        Matrix4x4 Resolve(string name, HashSet<string> parents)
        {
            if (shared != null && shared.TryGetValue(name, out var mapped)) return result[name] = mapped;
            if (result.TryGetValue(name, out var value)) return value;
            if (!parents.Add(name) || parents.Count > 128) throw new InvalidDataException("模型骨架循环。");
            var b = prefab.Bones[name]; var world = NativeMath.Local(b);
            world *= prefab.Bones.ContainsKey(b.Parent) ? Resolve(b.Parent, parents) : placement;
            parents.Remove(name); return result[name] = world;
        }
    }
    public static void Skin(NativeMesh mesh, IReadOnlyDictionary<string, Matrix4x4> bones, Matrix4x4 placement, CancellationToken cancellation)
    {
        bool skinned = mesh.Bones.Length > 0 && mesh.BindPoses.Length == mesh.Bones.Length && mesh.Weights.Length == mesh.Positions.Length / 3 * 4;
        if (!Matrix4x4.Invert(NativeMath.Matrix(mesh.Transform), out var inverse)) throw new InvalidDataException("无效网格变换。");
        string node = mesh.Name.Split('/')[0];
        var rigid = bones.TryGetValue(node, out var rigidWorld) ? inverse * rigidWorld : placement;
        var matrices = skinned ? mesh.Bones.Select((name, i) => bones.TryGetValue(name, out var b) ? inverse * NativeMath.Matrix(mesh.BindPoses[i]) * b : placement).ToArray() : [];
        var normals = matrices.Select(m => Matrix4x4.Invert(m, out var inv) ? Matrix4x4.Transpose(inv) : Matrix4x4.Identity).ToArray();
        Matrix4x4.Invert(rigid, out var placementInverse); var normalPlacement = Matrix4x4.Transpose(placementInverse);
        for (int i = 0; i < mesh.Positions.Length / 3; i++)
        {
            if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
            var p = new Vector3(mesh.Positions[i*3], mesh.Positions[i*3+1], -mesh.Positions[i*3+2]);
            var n = new Vector3(mesh.Normals[i*3], mesh.Normals[i*3+1], -mesh.Normals[i*3+2]);
            Vector3 pp = default, nn = default; float total = 0;
            if (skinned) for (int j = 0; j < 4; j++)
            {
                float w = mesh.Weights[i*4+j]; if (w <= 0) continue; int b = mesh.BoneIndices[i*4+j];
                if (b < 0 || b >= matrices.Length) throw new InvalidDataException("蒙皮权重引用无效骨骼。");
                pp += Vector3.Transform(p, matrices[b]) * w; nn += Vector3.TransformNormal(n, normals[b]) * w; total += w;
            }
            if (total < .0001f) { pp = Vector3.Transform(p, rigid); nn = Vector3.TransformNormal(n, normalPlacement); }
            else pp /= total;
            if (nn.LengthSquared() > 1e-12f) nn = Vector3.Normalize(nn);
            mesh.Positions[i*3]=pp.X; mesh.Positions[i*3+1]=pp.Y; mesh.Positions[i*3+2]=-pp.Z;
            mesh.Normals[i*3]=nn.X; mesh.Normals[i*3+1]=nn.Y; mesh.Normals[i*3+2]=-nn.Z;
        }
    }
    public void Dispose() { pe.Dispose(); stream.Dispose(); }
}
