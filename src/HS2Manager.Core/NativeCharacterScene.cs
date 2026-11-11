using System.Numerics;
using HS2Manager.Preview.Contracts;

namespace HS2Manager.Core;

public sealed class NativeSceneRenderer
{
    public required NativeSceneNode Node { get; init; }
    public required CharacterPart Part { get; init; }
    public required GpuRendererData Source { get; init; }
    public required GpuMeshData Mesh { get; set; }
    public required GpuMaterialData?[] Materials { get; init; }
    public required NativeSceneNode?[] Bones { get; set; }
    public required float[] BlendWeights { get; set; }
    public NativeSceneNode? RootBone {get;init;}
    public NativeRendererBounds? FixedLocalBounds {get;init;}
    public NativeRendererBounds Bounds=>NativeRendererBoundsEvaluator.Evaluate(this);
    // Weighted skins bake hierarchy scale into a rigid-root vertex stream.
    // A SkinnedMeshRenderer with no bind poses retains its full object matrix.
    public Matrix4x4 RenderWorldMatrix=>Source.Skinned&&Mesh.BindPoses.Length>0
        ?Matrix4x4.CreateFromQuaternion((RootBone??Node).Rotation)*Matrix4x4.CreateTranslation((RootBone??Node).Position)
        :Node.WorldMatrix;
    public Matrix4x4 RenderWorldToLocal=>Source.Skinned&&Mesh.BindPoses.Length>0
        ?NativeSceneNode.InverseRigidMatrix((RootBone??Node).Position,(RootBone??Node).Rotation)
        :Node.InverseWorldMatrix;
    public bool Enabled { get; set; }
    public int ShadowCasting { get; set; }
    public bool ReceiveShadows { get; set; }
    public bool Visible => Enabled && Node.ActiveInHierarchy;
    public bool? LastRenderedVisibility {get;set;}
    public bool WasRenderedVisible=>LastRenderedVisibility??Visible;
    public bool HasGeometry=>Mesh.VertexCount>0&&!(Source.Skinned&&Bones.Length>0&&Bones.All(b=>b==null));
    public NativeDeformedVertices? SimulatedVertices {get;set;}
    // The established FBX reference bake omits renderer scale during skinning,
    // then applies the renderer's full transform to those baked local vertices.
    public Matrix4x4 ReferenceBakeWorldToLocal=>NativeSceneNode.InverseRigidMatrix(Node.Position,Node.Rotation);
    public Matrix4x4[] SkinMatrices(Matrix4x4 worldToLocal)=>NativeSkinningMatrices.Build(Bones,Mesh.BindPoses,worldToLocal);
    public NativeDeformedVertices BakeReferenceMesh(CancellationToken cancellation=default)
    {
        if(!HasGeometry)return new([],[],[]);
        var inverse=ReferenceBakeWorldToLocal;
        var matrices=Source.Skinned?SkinMatrices(inverse):[];
        return NativeMeshDeformer.Deform(Mesh,BlendWeights,matrices,cancellation);
    }

    public NativeDeformedVertices Deform(CancellationToken cancellation = default)
        =>SimulatedVertices??DeformAnimation(cancellation);
    public NativeDeformedVertices DeformAnimation(CancellationToken cancellation = default)
    {
        // The reference skinner emits an empty mesh when every skin bone is
        // null. Retain the serialized mesh for diagnostics without drawing a
        // fabricated bind pose. Mixed weighted null bones still fail assembly.
        if(!HasGeometry)return new([],[],[]);
        Matrix4x4[] matrices = [];
        if (Source.Skinned)
        {
            if (Bones.Length != Mesh.BindPoses.Length) throw new InvalidDataException("Renderer bind-pose count differs from its bones: " + Node.Name);
            matrices = new Matrix4x4[Bones.Length];
            for (int i = 0; i < matrices.Length; i++) matrices[i] = Bones[i] is { } bone?Mesh.BindPoses[i] * bone.WorldMatrix * Node.InverseWorldMatrix:Matrix4x4.Identity;
        }
        var vertices=NativeMeshDeformer.Deform(Mesh, BlendWeights, matrices, cancellation);
        if(Source.Skinned&&Mesh.BindPoses.Length>0)
        {
            // The source skinner bakes scale into normals, then renders with a
            // rigid object transform. Our positions remain in the full node's
            // local space, so compensate for the material shader's subsequent
            // inverse-transpose transform. This matters for nonuniform ABMX scale.
            var world=Node.WorldMatrix;var compensation=world*Matrix4x4.Transpose(world);
            for(int i=0;i<vertices.Normals.Length;i++)vertices.Normals[i]=Vector3.TransformNormal(vertices.Normals[i],compensation);
        }
        return vertices;
    }
}

public sealed record NativeScenePart(CharacterPart Definition, NativeSceneNode Root, NativeSceneNode[] Nodes,
    IReadOnlyDictionary<string, NativeSceneNode> ByName, GpuComponentData[] Components, GpuNodeData[] SourceNodes);

/// <summary>Character assembly shared by the native renderer and exporters. Never instantiates game components.</summary>
public sealed class NativeCharacterScene
{
    public CharacterDefinition Definition { get; }
    public NativeSceneNode Root { get; } = new("Independent character", "root");
    public Dictionary<string, NativeSceneNode> Bones { get; } = new(StringComparer.Ordinal);
    public List<NativeScenePart> Parts { get; } = [];
    public List<NativeSceneRenderer> Renderers { get; } = [];
    public List<string> Diagnostics {get;}=[];
    public Dictionary<NativeSceneNode, BoneDefinition> ScopedBaselines { get; } = [];
    public Dictionary<NativeSceneNode, BoneAdjustment> ScopedAdjustments { get; } = [];
    private readonly NativeGpuAssetStore assets;
    private readonly Dictionary<GpuAssetPointer, GpuMeshData> meshes = [];
    private readonly CancellationToken cancellation;

    public NativeCharacterScene(CharacterDefinition definition, NativeGpuAssetStore assets, CancellationToken cancellation = default)
    {
        Definition = definition; this.assets = assets; this.cancellation = cancellation;
        foreach (var part in definition.Parts) { cancellation.ThrowIfCancellationRequested(); AddPart(part); }
        ApplyBoneScopes(); PoseFace(1); ApplyPreviewVisibility();
    }

    private void AddPart(CharacterPart part)
    {
        var prefab = assets.Reader(part.Prefab.Bundle).Prefab(part.Prefab.Asset, cancellation);
        var map = prefab.Nodes.ToDictionary(n => n.Id, n => new NativeSceneNode(n.Name, part.Prefab.Bundle + ":" + n.Id)
        { LocalPosition = n.Position, LocalRotation = n.Rotation, LocalScale = n.Scale, Active = n.Active, Layer = n.Layer });
        foreach (var n in prefab.Nodes)
            if (n.Id != prefab.RootId) map[n.Id].SetParent(map.TryGetValue(n.ParentId, out var parent) ? parent : throw new InvalidDataException("Prefab parent is missing."));
        var root = map[prefab.RootId];
        if (!string.IsNullOrEmpty(part.Parent) && part.Parent != "null" && !Bones.ContainsKey(part.Parent))
            throw new InvalidDataException("Missing attachment node: " + part.Parent);
        root.SetParent(Bones.GetValueOrDefault(part.Parent) ?? Root);
        var nodes = prefab.Nodes.Select(n => map[n.Id]).ToArray();
        var byName = nodes.GroupBy(n => n.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var bone in part.Bones) if (byName.TryGetValue(bone.Name, out var target)) SetBone(target, bone);
        if (part.Role is "rig" or "headRig")
            foreach (var pair in byName) Bones.TryAdd(pair.Key, pair.Value);
        bool bindSkeleton = Bound(part);
        var skinnedAccessories = part.Components.Where(c => c.Type == "SkinnedAccessory" && c.Data.Float("m_Enabled", 1) != 0)
            .SelectMany(c => c.Data.Get("meshRenderers").Children).Select(c => c.Text).ToHashSet(StringComparer.Ordinal);
        var additionalRoots = part.Components.Where(c => c.Type == "BonesFramework" && bindSkeleton)
            .SelectMany(c => c.Data.Children).Select(c => c.Text).Where(n => n.Length != 0).ToHashSet(StringComparer.Ordinal);
        foreach (var branch in nodes.Where(n => additionalRoots.Contains(n.Name)))
        {
            bool nested = false;
            for (var parent = branch.Parent; parent != null && parent != root; parent = parent.Parent)
                if (additionalRoots.Contains(parent.Name)) { nested = true; break; }
            if (nested) continue;
            if (branch.Parent == null || !Bones.TryGetValue(branch.Parent.Name, out var destination) || branch == destination)
            {Diagnostics.Add("Missing authored additional-bone attachment; retained local branch: " + part.Prefab.Asset + "/" + branch.Name);continue;}
            branch.SetParent(destination);
            if (part.Role == "supplement") foreach (var bone in branch.Traverse()) Bones[bone.Name] = bone;
        }
        foreach (var component in part.Components.Where(c => c.Type == "BoneImplantProcess"))
        {
            if (!byName.TryGetValue(component.Data.String("trfSrc"), out var branch) || !Bones.TryGetValue(component.Data.String("trfDst"), out var destination) || branch == destination)
            {Diagnostics.Add("Missing authored implanted-bone attachment; retained local branch: " + part.Prefab.Asset + "/" + component.Node);continue;}
            branch.SetParent(destination);
        }
        foreach (var source in prefab.Renderers)
        {
            cancellation.ThrowIfCancellationRequested();
            var node = map[source.NodeId]; string name = node.Name;
            if (part.Role is "rig" or "headRig") continue;
            if (part.Role == "supplement")
            {
                if (part.Slot == 0 && source.Skinned && name.StartsWith("o_body_", StringComparison.Ordinal))
                {
                    var existing = Renderers.FirstOrDefault(r => r.Part.Role == "body" && r.Node.Name == name);
                    if (existing != null)
                    {
                        existing.Mesh = Mesh(source.Mesh); existing.Bones = MapBones(source,existing.Mesh,true);
                        existing.BlendWeights = new float[existing.Mesh.BlendShapes.Length];
                        Array.Copy(source.BlendWeights, existing.BlendWeights, Math.Min(source.BlendWeights.Length, existing.BlendWeights.Length));
                    }
                }
                continue;
            }
            if (part.Role == "body" && !name.StartsWith("o_body_", StringComparison.Ordinal) || name.StartsWith("O_hit_", StringComparison.OrdinalIgnoreCase) || name.Contains("silhouette", StringComparison.Ordinal)) continue;
            bool shared = part.Role != "accessory" || bindSkeleton || skinnedAccessories.Contains(name);
            var mesh = Mesh(source.Mesh);
            var materials = source.Materials.Select(p => p.PathId == 0 ? null : assets.Material(p)).ToArray();
            if (part.Role == "body" || part.Role == "head" && name == "o_head")
            {
                string key = part.Role == "body" ? "template." + (Definition.Sex == 0 ? "cm" : "cf") + "_m_skin_body_00" : "head.MatData";
                var selection = Definition.Selections.Single(s => s.Key == key).Reference;
                if (materials.Length == 0) throw new InvalidDataException("Skin renderer has no material slot.");
                materials[0] = assets.Material(assets.Reader(selection.Bundle).Find(selection.Asset, 21));
            }
            var weights = new float[mesh.BlendShapes.Length];
            Array.Copy(source.BlendWeights, weights, Math.Min(weights.Length, source.BlendWeights.Length));
            var rootBone=source.Skinned&&source.RootBone.PathId!=0?MapBone(source.RootBone,shared):null;
            if(source.Skinned&&bindSkeleton&&part.Role!="head"&&Bones.TryGetValue("cf_J_Hips",out var bodyRoot)&&!prefab.Components.Any(c=>c.NodeId==source.NodeId&&c.ClassId==183))rootBone=bodyRoot;
            if(source.Skinned&&skinnedAccessories.Contains(name))rootBone=Parts.First(p=>p.Definition.Role=="rig").Root;
            Renderers.Add(new() { Node = node, Part = part, Source = source, Mesh = mesh, Materials = materials,
                Bones = source.Skinned ? MapBones(source,mesh,shared) : [],RootBone=rootBone, BlendWeights = weights, Enabled = source.Enabled,
                FixedLocalBounds=source.Skinned&&skinnedAccessories.Contains(name)?new(new(0,10,0),new(10)):source.Skinned&&bindSkeleton?new(new(0,-2,0),new(10)):null,
                ShadowCasting=(int)source.Settings.Float("m_CastShadows",1),ReceiveShadows=source.Settings.Float("m_ReceiveShadows",1)!=0 });
        }
        foreach (var component in part.Components)
        {
            foreach (string key in new[] { "objTopDef", "objBotDef", "objTopHalf", "objBotHalf" })
                foreach (var node in nodes.Where(n => n.Name == component.Data.String(key))) node.Active = key.EndsWith("Def", StringComparison.Ordinal);
            for (int i = 0; i < part.HideOptions.Length; i++)
                foreach (var reference in component.Data.Get("objOpt0" + (i + 1)).Children)
                    foreach (var node in nodes.Where(n => n.Name == reference.Text)) node.Active = !part.HideOptions[i];
        }
        if (part.Role == "accessory")
        {
            var component = part.Components.FirstOrDefault(c => c.Type == "CmpAccessory");
            if (component != null && part.Adjustments.Length == 18)
                for (int i = 0; i < 2; i++)
                    if (byName.TryGetValue(component.Data.String("trfMove0" + (i + 1)), out var move))
                    {
                        int offset = i * 9;
                        move.LocalPosition = Vector(part.Adjustments, offset) * .1f;
                        move.LocalRotation = Euler(Vector(part.Adjustments, offset + 3));
                        move.LocalScale = Vector(part.Adjustments, offset + 6);
                    }
            if (part.Parameters.Has("show") && part.Parameters.Float("show") == 0) root.Active = false;
        }
        if (part.Role == "hair") HairBones(part, byName);
        Parts.Add(new(part, root, nodes, byName, prefab.Components, prefab.Nodes));
        NativeSceneNode?[] MapBones(GpuRendererData renderer,GpuMeshData mesh,bool useShared)
        {
            var result=new NativeSceneNode?[renderer.Bones.Length];
            if(result.Length>0&&renderer.Bones.All(b=>b.PathId==0))
            {Diagnostics.Add($"Source skinner has no bone transforms and emits no geometry: {part.Prefab.Asset}/{map[renderer.NodeId].Name}");return result;}
            for(int i=0;i<result.Length;i++)
            {
                var pointer=renderer.Bones[i];
                if(pointer.PathId!=0){result[i]=MapBone(pointer,useShared);continue;}
                // Serialized bone arrays can contain unused null slots. Preserve
                // them without inventing a skeleton node. Weighted null bones
                // require separate source-runtime validation and remain failures.
                for(int vertex=0;vertex<mesh.VertexCount;vertex++)for(int influence=0;influence<4;influence++)
                    if(mesh.Read(12,vertex,influence)>0&&(int)mesh.Read(13,vertex,influence)==i)
                        throw new InvalidDataException($"Skinned renderer has a weighted null bone: {part.Prefab.Asset}/{map[renderer.NodeId].Name}, slot {i}, vertex {vertex}");
            }
            return result;
        }
        NativeSceneNode MapBone(GpuAssetPointer pointer, bool useShared)
        {
            if (pointer.PathId == 0) throw new InvalidDataException("Skinned renderer has a null bone: " + part.Prefab.Asset);
            if (pointer.FileId != 0 || !map.TryGetValue(pointer.PathId, out var local)) throw new InvalidDataException("External or missing skin bone: " + pointer);
            return useShared && Bones.TryGetValue(local.Name, out var common) ? common : local;
        }
    }
    private GpuMeshData Mesh(GpuAssetPointer pointer)
    {
        pointer = assets.Resolve(pointer);
        if (!meshes.TryGetValue(pointer, out var mesh)) meshes.Add(pointer, mesh = assets.Mesh(pointer));
        return mesh;
    }
    private static bool Bound(CharacterPart p) => p.Role is "body" or "head" or "supplement" or "clothes" || p.List.String("Weights") is "1" or "2" || p.Role == "accessory" && p.List.String("Parent") == "null";
    private void ApplyBoneScopes()
    {
        var excluded = new HashSet<NativeSceneNode>(); var aliases = new Dictionary<NativeSceneNode, string>();
        foreach (var part in Parts)
        {
            if (part.Definition.RuntimeName.Length != 0) aliases[part.Root] = part.Definition.RuntimeName;
            if (!Bound(part.Definition) && !part.Definition.Components.Any(c => c.Type == "SkinnedAccessory" && c.Data.Float("m_Enabled", 1) != 0)) continue;
            foreach (var node in part.Nodes)
                for (var ancestor = node; ancestor != null && ancestor != Root; ancestor = ancestor.Parent)
                    if (Bones.TryGetValue(ancestor.Name, out var shared) && ancestor != shared) { excluded.Add(node); break; }
        }
        var first = new Dictionary<string, NativeSceneNode>(StringComparer.Ordinal);
        foreach (var node in Root.Traverse().Where(n => !excluded.Contains(n))) first.TryAdd(aliases.GetValueOrDefault(node, node.Name), node);
        foreach (var part in Parts)
            foreach (var bone in part.Definition.UnmodifiedBones ?? [])
                if (part.ByName.TryGetValue(bone.Name, out var node))
                {
                    if (first.GetValueOrDefault(aliases.GetValueOrDefault(node, bone.Name)) == node) ScopedBaselines[node] = bone;
                    else SetBone(node, bone);
                }
        foreach (var adjustment in Definition.BoneModifiers ?? [])
            if (first.TryGetValue(adjustment.Name, out var target) && ScopedBaselines.ContainsKey(target)) ScopedAdjustments[target] = adjustment;
    }
    public void PoseFace(float eyes)
    {
        foreach (var part in Parts.Where(p => p.Definition.Role == "head"))
        {
            var component = part.Definition.Components.FirstOrDefault(c => c.Type == "FaceBlendShape");
            if (component == null) continue;
            foreach (string controller in new[] { "EyesCtrl", "EyebrowCtrl", "MouthCtrl" })
            {
                float amount = controller == "EyesCtrl" ? Math.Clamp(eyes * Definition.EyeOpenMax, 0, 1) : controller == "EyebrowCtrl" ? 1 : 0;
                int percent = (int)(amount * 100);
                foreach (var target in component.Data.Get(controller).Get("FBSTarget").Children)
                {
                    var renderer = Renderers.FirstOrDefault(r => r.Part == part.Definition && r.Source.Skinned && r.Node.Name == target.String("ObjTarget"));
                    if (renderer == null) continue;
                    var pattern = target.Get("PtnSet").At(0); int close = (int)pattern.Float("Close", -1), open = (int)pattern.Float("Open", -1);
                    if ((uint)close < renderer.BlendWeights.Length) renderer.BlendWeights[close] = 100 - percent;
                    if ((uint)open < renderer.BlendWeights.Length) renderer.BlendWeights[open] = open == close ? 100 : percent;
                }
            }
        }
    }
    private void ApplyPreviewVisibility()
    {
        var visibility = Definition.Visibility ?? new CharacterPreviewVisibility();
        foreach (var part in Parts)
        {
            string role = part.Definition.Role;
            if (role == "body" && !Definition.BodyVisible) foreach (var renderer in Renderers.Where(r => r.Part == part.Definition)) renderer.Enabled = false;
            if (role is not ("clothes" or "accessory")) continue;
            bool visible = role == "clothes" ? visibility.ClothesVisible(part.Definition.Slot) : visibility.Accessories && (!part.Definition.Parameters.Has("show") || part.Definition.Parameters.Float("show") != 0);
            part.Root.Active = visible;
            if (!visible) foreach (var renderer in Renderers.Where(r => r.Part == part.Definition)) renderer.Enabled = false;
        }
    }
    private void HairBones(CharacterPart part, Dictionary<string, NativeSceneNode> nodes)
    {
        var component = part.Components.FirstOrDefault(c => c.Type == "CmpHair"); if (component == null) return;
        var bundle = Definition.Hair.Get("parts").At(part.Slot).Get("dictBundle"); var bones = component.Data.Get("boneInfo");
        for (int i = 0; i < bones.Children.Length; i++)
        {
            if (!bundle.Has(i.ToString())) continue;
            var parameters = bundle.Get(i.ToString()); var config = bones.At(i);
            if (!nodes.TryGetValue(config.String("trfCorrect"), out var bone)) continue;
            bone.LocalPosition = Interpolate("posMin", "posMax", "moveRate"); bone.LocalRotation = Euler(Interpolate("rotMin", "rotMax", "rotRate"));
            Vector3 Interpolate(string min, string max, string key)
            {
                float Value(int axis)
                {
                    float t = Coordinate(parameters.Get(key), axis); if (Definition.Hair.Float("unlockedSliders") == 0) t = Math.Clamp(t, 0, 1);
                    float a = Coordinate(config.Get(min), axis), b = Coordinate(config.Get(max), axis); return a + (b - a) * t;
                }
                return new(Value(0), Value(1), Value(2));
            }
        }
    }
    internal static float Coordinate(Parameter p, int axis) { string key = new[] { "x", "y", "z", "w" }[axis]; return p.Has(key) ? p.Float(key) : (float)p.At(axis).Number; }
    public static Quaternion Euler(Vector3 degrees)
    {
        const float degreesToRadians=.0174532924f;
        return Quaternion.CreateFromAxisAngle(Vector3.UnitY,degrees.Y*degreesToRadians)
            *Quaternion.CreateFromAxisAngle(Vector3.UnitX,degrees.X*degreesToRadians)
            *Quaternion.CreateFromAxisAngle(Vector3.UnitZ,degrees.Z*degreesToRadians);
    }
    private static Vector3 Vector(float[] values, int offset = 0) => new(values[offset], values[offset + 1], values[offset + 2]);
    private static void SetBone(NativeSceneNode node, BoneDefinition bone)
    { node.LocalPosition = Vector(bone.Position); node.LocalRotation = new(bone.Rotation[0], bone.Rotation[1], bone.Rotation[2], bone.Rotation[3]); node.LocalScale = Vector(bone.Scale); }
}
