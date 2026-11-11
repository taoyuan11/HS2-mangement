using System.Buffers.Binary;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;
using NQuaternion = System.Numerics.Quaternion;
using NMatrix4x4 = System.Numerics.Matrix4x4;
using System.Reflection;
using System.Runtime.InteropServices;
using HS2Manager.Preview.Contracts;
using Microsoft.Win32.SafeHandles;

namespace HS2Manager.Core.Simulation;

/// <summary>Instance-owned CPU PhysX cloth. Particle topology and authored constraints remain explicit.</summary>
public sealed class NativePhysXClothSimulation : INativeClothSimulation
{
    private readonly List<Entry> entries=[];
    private ClothNative.SceneHandle? native;
    private bool reset=true,disposed;
    private sealed class Entry
    {
        public required NativeSceneRenderer Renderer;
        public required Parameter Data;
        public required int[] ParticleForVertex,FirstVertices;
        public required float[] MaximumDistances,SeparationDistances;
        public int Index=-1;
        public NativeDeformedVertices? Animation;
    }
    public bool HasCloth=>entries.Count>0;
    internal Action<string,object>? Trace {get;set;}
    public NativePhysXClothSimulation(NativeCharacterScene scene)
    {
        foreach(var part in scene.Parts)
        foreach(var component in part.Definition.Components.Where(c=>c.Type=="Cloth"&&c.Data.Float("m_Enabled",1)!=0))
        {
            var renderer=scene.Renderers.Single(r=>r.Part==part.Definition&&r.Node.Name==component.Node);var data=component.Data;
            foreach(string field in new[]{"m_CapsuleColliders","m_SphereColliders"})
                if(data.Get(field).Children.Length>0)throw new NotSupportedException("Cloth collider node mapping requires validation: "+component.Node+"/"+field);
            if(Vector(data.Get("m_RandomAcceleration"))!=NVector3.Zero)throw new NotSupportedException("Cloth random acceleration needs its original seeded update sequence.");
            var map=new Dictionary<NVector3,int>();var first=new List<int>();var particles=new int[renderer.Mesh.VertexCount];
            for(int i=0;i<particles.Length;i++)
            {
                var point=new NVector3(renderer.Mesh.Read(0,i,0),renderer.Mesh.Read(0,i,1),renderer.Mesh.Read(0,i,2));
                if(!map.TryGetValue(point,out int particle)){particle=map.Count;map.Add(point,particle);first.Add(i);}particles[i]=particle;
            }
            var coefficients=data.Get("m_Coefficients").Children;
            if(coefficients.Length!=first.Count)throw new InvalidDataException("Cloth welded topology differs from its authored coefficients: "+component.Node);
            entries.Add(new(){Renderer=renderer,Data=data,ParticleForVertex=particles,FirstVertices=first.ToArray(),MaximumDistances=coefficients.Select(c=>c.Float("maxDistance")).ToArray(),SeparationDistances=coefficients.Select(c=>c.Float("collisionSphereDistance",float.MaxValue)).ToArray()});
        }
    }
    public void ResetMotion(){ObjectDisposedException.ThrowIf(disposed,this);reset=true;foreach(var entry in entries)entry.Renderer.SimulatedVertices=null;}
    public void Step(float seconds)
    {
        ObjectDisposedException.ThrowIf(disposed,this);if(entries.Count==0)return;
        if(!float.IsFinite(seconds)||seconds<=0||seconds>.333334f)throw new ArgumentOutOfRangeException(nameof(seconds));
        native??=new ClothNative.SceneHandle();
        foreach(var e in entries)
        {
            if(!e.Renderer.Node.ActiveInHierarchy)continue;
            var renderer=e.Renderer;var animation=renderer.DeformAnimation();e.Animation=animation;
            var rotation=renderer.Node.Rotation;var inverse=NQuaternion.Inverse(rotation);var position=renderer.Node.Position;
            NVector3 LocalPoint(NVector3 value)=>NVector3.Transform(renderer.Node.TransformPoint(value)-position,inverse);
            NVector3 LocalNormal(NVector3 value)=>NVector3.Transform(NVector3.TransformNormal(value,NMatrix4x4.Transpose(renderer.Node.InverseWorldMatrix)),inverse);
            var points=e.FirstVertices.SelectMany(i=>Values(LocalPoint(animation.Positions[i]))).ToArray();
            var normals=e.FirstVertices.SelectMany(i=>Values(Unit(LocalNormal(animation.Normals[i])))).ToArray();
            float[] pose=[position.X,position.Y,position.Z,rotation.X,rotation.Y,rotation.Z,rotation.W];
            var rootWorld=(renderer.RootBone??renderer.Node).WorldMatrix;
            var rootScale=new NVector3(NVector3.TransformNormal(NVector3.UnitX,rootWorld).Length(),NVector3.TransformNormal(NVector3.UnitY,rootWorld).Length(),NVector3.TransformNormal(NVector3.UnitZ,rootWorld).Length());
            float scale=rootScale.X;
            if(e.Index<0)
            {
                var source=renderer.Mesh;var triangles=new List<uint>();
                foreach(var sub in source.Submeshes)
                {
                    if(sub.Topology!=0)throw new NotSupportedException("Cloth requires triangular mesh topology.");
                    for(int index=sub.FirstIndex;index<sub.FirstIndex+sub.IndexCount;index++)
                    {int vertex=sub.BaseVertex+(source.IndexSize==2?BinaryPrimitives.ReadUInt16LittleEndian(source.IndexBytes.AsSpan(index*2,2)):BinaryPrimitives.ReadInt32LittleEndian(source.IndexBytes.AsSpan(index*4,4)));triangles.Add((uint)e.ParticleForVertex[vertex]);}
                }
                var d=e.Data;
                float[] parameters=[d.Float("m_StretchingStiffness",1),d.Float("m_BendingStiffness",1),d.Float("m_Damping"),d.Float("m_Friction",.5f),d.Float("m_CollisionMassScale"),d.Float("m_UseContinuousCollision",1),
                    d.Float("m_SolverFrequency",120),d.Float("m_StiffnessFrequency",10),d.Float("m_UseTethers",1),d.Float("m_SleepThreshold",.1f),d.Float("m_SelfCollisionDistance"),d.Float("m_SelfCollisionStiffness",1),
                    d.Float("m_WorldVelocityScale",.5f),d.Float("m_WorldAccelerationScale",1),d.Float("m_UseGravity",1)];
                var maximum=e.MaximumDistances.Select(v=>ScaleDistance(v,scale)).ToArray();
                // Fabric rest lengths come from the source mesh, scaled with
                // the skin root. The currently animated shape supplies motion
                // constraints and initial particles, not the fabric rest pose.
                var cooked=e.FirstVertices.SelectMany(i=>Values(new NVector3(source.Read(0,i,0),source.Read(0,i,1),source.Read(0,i,2))*rootScale)).ToArray();
                Trace?.Invoke("add",new{Count=e.FirstVertices.Length,Positions=points,Cooked=cooked,Rest=e.FirstVertices.SelectMany(i=>new[]{source.Read(0,i,0),source.Read(0,i,1),source.Read(0,i,2)}).ToArray(),Triangles=triangles,Maximum=maximum,Parameters=parameters,Pose=pose});
                e.Index=ClothNative.Check(ClothNative.Add(native,e.FirstVertices.Length,cooked,triangles.Count,triangles.ToArray(),maximum,parameters,pose));
                var self=d.Get("m_SelfAndInterCollisionIndices").Children.Select(v=>checked((uint)v.Number)).ToArray();
                var virtualIndices=d.Get("m_VirtualParticleIndices").Children.Select(v=>checked((uint)v.Number)).ToArray();
                var virtualWeights=d.Get("m_VirtualParticleWeights").Children.SelectMany(v=>Values(Vector(v))).ToArray();
                if(virtualIndices.Length%4!=0)throw new InvalidDataException("Cloth virtual particle index tuple is truncated.");
                Trace?.Invoke("particles",new{Index=e.Index,Self=self,VirtualIndices=virtualIndices,VirtualWeights=virtualWeights});
                ClothNative.Check(ClothNative.Particles(native,e.Index,self.Length,self,virtualIndices.Length/4,virtualIndices,virtualWeights.Length/3,virtualWeights));
            }
            var acceleration=Vector(e.Data.Get("m_ExternalAcceleration"));
            Trace?.Invoke("update",new{Index=e.Index,Positions=points,Normals=normals,Separation=e.SeparationDistances.Select(v=>ScaleDistance(v,scale)).ToArray(),Pose=pose,Acceleration=Values(acceleration),Reset=reset});
            ClothNative.Check(ClothNative.Update(native,e.Index,points,normals,e.SeparationDistances.Select(v=>ScaleDistance(v,scale)).ToArray(),pose,Values(acceleration),reset?1:0));
        }
        ClothNative.Check(ClothNative.Step(native,seconds));Trace?.Invoke("step",new{Seconds=seconds});reset=false;
        foreach(var e in entries.Where(e=>e.Index>=0&&e.Renderer.Node.ActiveInHierarchy))
        {
            var renderer=e.Renderer;var points=new float[e.FirstVertices.Length*3];ClothNative.Check(ClothNative.Read(native,e.Index,e.FirstVertices.Length,points));
            Trace?.Invoke("read",new{Index=e.Index,Positions=points,ParticleForVertex=e.ParticleForVertex,Pose=new[]{renderer.Node.Position.X,renderer.Node.Position.Y,renderer.Node.Position.Z,renderer.Node.Rotation.X,renderer.Node.Rotation.Y,renderer.Node.Rotation.Z,renderer.Node.Rotation.W},World=HS2Matrix(renderer.RenderWorldToLocal)});
            var rotation=renderer.Node.Rotation;var position=renderer.Node.Position;var vertices=new NVector3[renderer.Mesh.VertexCount];
            for(int i=0;i<vertices.Length;i++)
            {int p=e.ParticleForVertex[i]*3;var world=NVector3.Transform(new NVector3(points[p],points[p+1],points[p+2]),rotation)+position;vertices[i]=renderer.Node.InverseTransformPoint(world);}
            // Welded particles also share the simulated normal. Accumulate faces
            // before scattering back to UV seams, as cloth has one particle there.
            var normals=new NVector3[e.FirstVertices.Length];var mesh=renderer.Mesh;
            foreach(var sub in mesh.Submeshes)
            for(int i=sub.FirstIndex;i<sub.FirstIndex+sub.IndexCount;i+=3)
            {
                int Index(int index)=>sub.BaseVertex+(mesh.IndexSize==2?BinaryPrimitives.ReadUInt16LittleEndian(mesh.IndexBytes.AsSpan(index*2,2)):BinaryPrimitives.ReadInt32LittleEndian(mesh.IndexBytes.AsSpan(index*4,4)));
                int a=Index(i),b=Index(i+1),c=Index(i+2);var normal=NVector3.Cross(vertices[b]-vertices[a],vertices[c]-vertices[a]);
                normals[e.ParticleForVertex[a]]+=normal;normals[e.ParticleForVertex[b]]+=normal;normals[e.ParticleForVertex[c]]+=normal;
            }
            renderer.SimulatedVertices=new(vertices,e.ParticleForVertex.Select(p=>Unit(normals[p])).ToArray(),(NVector4[])e.Animation!.Tangents.Clone());
        }
    }
    public void WriteDiagnostics(string path)
    {
        var result=entries.Select(e=>new{Node=e.Renderer.Node.Name,Particles=e.FirstVertices.Length,Vertices=e.Renderer.Mesh.VertexCount,e.Index,ConstraintMapping="first occurrence of exact source position",CharacterParityVerified=false});
        File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(result));
    }
    private static float ScaleDistance(float value,float scale)=>value>=float.MaxValue*.5f?float.MaxValue:value*scale;
    private static NVector3 Vector(Parameter value)=>new(value.Float("x"),value.Float("y"),value.Float("z"));
    private static float[] Values(NVector3 value)=>[value.X,value.Y,value.Z];
    private static float[] HS2Matrix(NMatrix4x4 m)=>[m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44];
    private static NVector3 Unit(NVector3 value)=>value.LengthSquared()>1e-20f?NVector3.Normalize(value):NVector3.UnitY;
    public void Dispose(){if(disposed)return;foreach(var e in entries)e.Renderer.SimulatedVertices=null;native?.Dispose();native=null;disposed=true;}
}

internal static class ClothNative
{
    private const string Library="HS2Manager.Physics";
    private static readonly List<nint> dependencies=[];
    private static readonly object libraryLock=new();
    private static nint module;
    static ClothNative()=>NativeLibrary.SetDllImportResolver(typeof(ClothNative).Assembly,Resolve);
    private static nint Resolve(string name,Assembly assembly,DllImportSearchPath? search)
    {
        if(name!=Library)return 0;
        lock(libraryLock)
        {
        if(module!=0)return module;
        string directory=Environment.GetEnvironmentVariable("HS2_PHYSICS_NATIVE_DIRECTORY")??Path.Combine(AppContext.BaseDirectory,"native","win-x64");directory=Path.GetFullPath(directory);
        foreach(string dependency in new[]{"PxFoundation_x64.dll","PhysX3Common_x64.dll","PxPvdSDK_x64.dll","PhysX3_x64.dll"})dependencies.Add(NativeLibrary.Load(Path.Combine(directory,dependency),assembly,DllImportSearchPath.UseDllDirectoryForDependencies|DllImportSearchPath.SafeDirectories));
        return module=NativeLibrary.Load(Path.Combine(directory,Library+".dll"),assembly,DllImportSearchPath.UseDllDirectoryForDependencies|DllImportSearchPath.SafeDirectories);
        }
    }
    internal static int Check(int result){if(result<0)throw new InvalidDataException(Marshal.PtrToStringUTF8(Error())??"Native cloth operation failed.");return result;}
    [DllImport(Library,EntryPoint="hs2_cloth_error",CallingConvention=CallingConvention.Cdecl)]private static extern nint Error();
    [DllImport(Library,EntryPoint="hs2_cloth_create",CallingConvention=CallingConvention.Cdecl)]private static extern nint Create();
    [DllImport(Library,EntryPoint="hs2_cloth_destroy",CallingConvention=CallingConvention.Cdecl)]private static extern void Destroy(nint value);
    internal sealed class SceneHandle:SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SceneHandle():base(true){SetHandle(Create());if(IsInvalid)throw new InvalidDataException(Marshal.PtrToStringUTF8(Error())??"Native cloth initialization failed.");}
        protected override bool ReleaseHandle(){Destroy(handle);return true;}
    }
    [DllImport(Library,EntryPoint="hs2_cloth_add",CallingConvention=CallingConvention.Cdecl)]internal static extern int Add(SceneHandle scene,int count,float[] vertices,int indexCount,uint[] indices,float[] maximum,float[] parameters,float[] transform);
    [DllImport(Library,EntryPoint="hs2_cloth_update",CallingConvention=CallingConvention.Cdecl)]internal static extern int Update(SceneHandle scene,int index,float[] positions,float[] normals,float[] separation,float[] transform,float[] acceleration,int reset);
    [DllImport(Library,EntryPoint="hs2_cloth_particles",CallingConvention=CallingConvention.Cdecl)]internal static extern int Particles(SceneHandle scene,int index,int selfCount,uint[] self,int virtualCount,uint[] indices,int weightCount,float[] weights);
    [DllImport(Library,EntryPoint="hs2_cloth_step",CallingConvention=CallingConvention.Cdecl)]internal static extern int Step(SceneHandle scene,float seconds);
    [DllImport(Library,EntryPoint="hs2_cloth_read",CallingConvention=CallingConvention.Cdecl)]internal static extern int Read(SceneHandle scene,int index,int count,[Out]float[] positions);
}
