using System.Numerics;
using System.Text;
using HS2Manager.Preview.Contracts;

namespace HS2Manager.Core;

public sealed record NativeAnimationBinding(uint PathHash, uint Attribute, int ClassId, int CustomType, int Offset, int Components);

/// <summary>Unity 2018 serialized curve data, evaluated independently of Animator and UnityEngine.</summary>
public sealed class NativeAnimationClip
{
    private sealed record StreamKey(float Time, float A, float B, float C, float D);
    private readonly StreamKey[][] streamed;
    private readonly float[] dense, constants;
    private readonly int denseCount, denseFrames;
    private readonly float denseRate, denseStart;
    public string Name { get; }
    public float Start { get; }
    public float Duration { get; }
    public IReadOnlyList<NativeAnimationBinding> Bindings { get; }
    public int CurveCount { get; }

    public NativeAnimationClip(Parameter data)
    {
        Name = data.String("m_Name");
        foreach (string field in new[] { "m_RotationCurves", "m_CompressedRotationCurves", "m_EulerCurves", "m_PositionCurves", "m_ScaleCurves", "m_FloatCurves", "m_PPtrCurves" })
            if (data.Get(field).Children.Length != 0) throw new NotSupportedException("Legacy animation curves require their own evaluator: " + Name + "/" + field);
        var muscle = data.Get("m_MuscleClip"); var clip = muscle.Get("m_Clip").Get("data");
        Start = muscle.Float("m_StartTime"); Duration = muscle.Float("m_StopTime") - Start;
        if (!(Duration > 0) || !float.IsFinite(Duration)) throw new InvalidDataException("Animation duration is invalid.");
        var stream = clip.Get("m_StreamedClip"); int count = (int)stream.Float("curveCount");
        if (count is < 0 or > 100000) throw new InvalidDataException("Animation curve count is invalid.");
        var lists = Enumerable.Range(0, count).Select(_ => new List<StreamKey>()).ToArray();
        uint[] words = stream.Get("data").Children.Select(p => checked((uint)p.Number)).ToArray();
        int cursor = 0; float previous = float.NegativeInfinity;
        while (cursor < words.Length)
        {
            if (words.Length - cursor < 2) throw new InvalidDataException("Animation stream frame is truncated.");
            float time = BitConverter.UInt32BitsToSingle(words[cursor++]); int keys = checked((int)words[cursor++]);
            if (float.IsPositiveInfinity(time) && keys == 0 && cursor == words.Length) break;
            if (!float.IsFinite(time) || time < previous || keys > count || words.Length - cursor < (long)keys * 5)
                throw new InvalidDataException("Animation stream frame is invalid.");
            previous = time;
            var seen = new HashSet<int>();
            for (int i = 0; i < keys; i++)
            {
                int index = checked((int)words[cursor++]);
                if ((uint)index >= count || !seen.Add(index)) throw new InvalidDataException("Animation stream curve index is invalid.");
                float a = Read(), b = Read(), c = Read(), d = Read();
                lists[index].Add(new(time, a, b, c, d));
            }
        }
        streamed = lists.Select(l => l.ToArray()).ToArray();
        if (streamed.Any(s => s.Length == 0)) throw new InvalidDataException("Animation stream has an empty curve.");
        var denseData = clip.Get("m_DenseClip"); denseCount = (int)denseData.Float("m_CurveCount"); denseFrames = (int)denseData.Float("m_FrameCount");
        denseRate = denseData.Float("m_SampleRate"); denseStart = denseData.Float("m_BeginTime");
        dense = denseData.Get("m_SampleArray").Children.Select(p => (float)p.Number).ToArray();
        if (denseCount < 0 || denseFrames < 0 || (long)denseCount * denseFrames != dense.Length || denseCount > 0 && (denseFrames == 0 || !(denseRate > 0)))
            throw new InvalidDataException("Dense animation samples are invalid.");
        constants = clip.Get("m_ConstantClip").Get("data").Children.Select(p => (float)p.Number).ToArray();
        if (dense.Concat(constants).Any(v => !float.IsFinite(v))) throw new InvalidDataException("Non-finite animation samples.");
        int offset = 0; var bindings = new List<NativeAnimationBinding>();
        foreach (var binding in data.Get("m_ClipBindingConstant").Get("genericBindings").Children)
        {
            int type = (int)binding.Float("typeID"); uint attribute = checked((uint)binding.Get("attribute").Number);
            int components = type == 4 ? attribute switch { 1 or 3 or 4 => 3, 2 => 4, _ => throw new NotSupportedException("Unknown transform animation attribute.") } : 1;
            if (binding.Float("isPPtrCurve") != 0) throw new NotSupportedException("Object-reference animation requires explicit material/node binding.");
            bindings.Add(new(checked((uint)binding.Get("path").Number), attribute, type, (int)binding.Float("customType"), offset, components)); offset += components;
        }
        CurveCount = count + denseCount + constants.Length;
        if (offset != CurveCount) throw new InvalidDataException("Animation bindings do not account for all curve data.");
        Bindings = bindings.AsReadOnly();
        float Read()
        {
            float value = BitConverter.UInt32BitsToSingle(words[cursor++]);
            if (!float.IsFinite(value)) throw new InvalidDataException("Animation coefficient is not finite.");
            return value;
        }
    }

    public float[] Evaluate(float time, bool loop = true)
    {
        if (!float.IsFinite(time)) throw new ArgumentOutOfRangeException(nameof(time));
        time = Start + (loop ? Math.Clamp(time - MathF.Floor(time / Duration) * Duration, 0, Duration) : Math.Clamp(time, 0, Duration));
        float[] values = new float[CurveCount];
        for (int curve = 0; curve < streamed.Length; curve++)
        {
            var keys = streamed[curve]; int low = 0, high = keys.Length;
            while (low + 1 < high) { int mid = (low + high) / 2; if (keys[mid].Time <= time) low = mid; else high = mid; }
            var key = keys[low]; float dt = Math.Max(0, time - key.Time);
            // Coefficients already encode the cubic segment in seconds. Keep operation order explicit.
            values[curve] = ((key.A * dt + key.B) * dt + key.C) * dt + key.D;
        }
        if (denseCount > 0)
        {
            float frame = Math.Clamp((time - denseStart) * denseRate, 0, denseFrames - 1); int first = (int)frame, second = Math.Min(first + 1, denseFrames - 1); float t = frame - first;
            for (int curve = 0; curve < denseCount; curve++)
            {
                float a = dense[first * denseCount + curve], b = dense[second * denseCount + curve];
                values[streamed.Length + curve] = a + (b - a) * t;
            }
        }
        constants.CopyTo(values, streamed.Length + denseCount); return values;
    }

    public static uint PathHash(string path)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in Encoding.UTF8.GetBytes(path))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0);
        }
        return ~crc;
    }
}

/// <summary>Instance-owned curve bindings. Root-motion values are retained, but do not move the preview root.</summary>
public sealed class NativeAnimationPlayer
{
    private readonly NativeAnimationClip clip;
    public float Duration => clip.Duration;
    private readonly (NativeAnimationBinding Binding, NativeSceneNode Node)[] tracks;
    private readonly (int Offset,NativeSceneRenderer Renderer,int Shape)[] morphs;
    public IReadOnlyList<uint> UnboundPaths { get; }
    public IReadOnlyDictionary<uint, float> RootMotion => rootMotion;
    private readonly Dictionary<uint, float> rootMotion = [];
    public NativeAnimationPlayer(NativeAnimationClip clip, NativeSceneNode root,IEnumerable<NativeSceneNode>? bindingNodes=null,IEnumerable<NativeSceneRenderer>? rendererBindings=null)
    {
        this.clip = clip;
        var paths = new Dictionary<uint, NativeSceneNode>();
        var requiredPaths=clip.Bindings.Where(b=>b.ClassId is 4 or 137).Select(b=>b.PathHash).ToHashSet();
        var allowed=bindingNodes?.ToHashSet();
        Walk(root, "");
        var tracks = new List<(NativeAnimationBinding, NativeSceneNode)>(); var unbound = new HashSet<uint>();
        var morphs=new List<(int,NativeSceneRenderer,int)>();
        foreach (var binding in clip.Bindings)
        {
            if (binding.ClassId == 95 && binding.CustomType == 8 && binding.Attribute is >= 7 and <= 13) continue;
            if(binding.ClassId==137&&rendererBindings!=null)
            {
                if(!paths.TryGetValue(binding.PathHash,out var rendererNode)){unbound.Add(binding.PathHash);continue;}
                var renderer=rendererBindings.SingleOrDefault(r=>r.Node==rendererNode)??throw new InvalidDataException("Animated skinned renderer is missing: "+rendererNode.Name);
                int shape=Array.FindIndex(renderer.Mesh.BlendShapes,s=>s.Hash==binding.Attribute);
                if(shape<0)throw new NotSupportedException("Blend-shape animation binding is unresolved: "+binding.Attribute+"/"+binding.CustomType+" @ "+rendererNode.Name+" ["+string.Join(",",renderer.Mesh.BlendShapes.Select(s=>s.Name+":"+s.Hash))+"]");
                morphs.Add((binding.Offset,renderer,shape));continue;
            }
            if (binding.ClassId != 4) throw new NotSupportedException("Animation component binding requires an evaluator: " + binding.ClassId + "/" + binding.Attribute);
            if (paths.TryGetValue(binding.PathHash, out var node)) tracks.Add((binding, node)); else unbound.Add(binding.PathHash);
        }
        this.tracks = tracks.ToArray();this.morphs=morphs.ToArray(); UnboundPaths = unbound.ToArray();
        void Walk(NativeSceneNode node, string path)
        {
            if(allowed!=null&&!allowed.Contains(node))return;
            uint hash = NativeAnimationClip.PathHash(path);
            // Grafted MOD branches can repeat decorative names. An unrelated
            // duplicate cannot make an otherwise unambiguous clip invalid.
            if (!paths.TryAdd(hash, node)&&requiredPaths.Contains(hash)) throw new InvalidDataException("Animation path hash collision in an active binding: " + path);
            foreach (var child in node.Children) Walk(child, path.Length == 0 ? child.Name : path + "/" + child.Name);
        }
    }
    public void Sample(float time, bool loop = true)
    {
        var values = clip.Evaluate(time, loop);
        foreach(var morph in morphs)morph.Renderer.BlendWeights[morph.Shape]=values[morph.Offset];
        foreach (var (binding, node) in tracks)
        {
            int i = binding.Offset;
            switch (binding.Attribute)
            {
                case 1: node.LocalPosition = new(values[i], values[i + 1], values[i + 2]); break;
                // Clip evaluation normalizes its quaternion before the transform
                // setter performs its own normalization.
                case 2: node.LocalRotation = NativeSceneNode.NormalizeRotation(new(values[i], values[i + 1], values[i + 2], values[i + 3])); break;
                case 3: node.LocalScale = new(values[i], values[i + 1], values[i + 2]); break;
                case 4: node.LocalRotation = NativeCharacterScene.Euler(new(values[i], values[i + 1], values[i + 2])); break;
            }
        }
        foreach (var binding in clip.Bindings.Where(b => b.ClassId == 95)) rootMotion[binding.Attribute] = values[binding.Offset];
    }
}
