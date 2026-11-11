using System.Collections;
using NMatrix = System.Numerics.Matrix4x4;
using NQuaternion = System.Numerics.Quaternion;
using NVector = System.Numerics.Vector3;

namespace HS2Manager.Core.Simulation;

// Small numeric adapter for the established controller equations. Owns no engine objects.
// Keeping the scalar operation order makes the controller migration auditable.
internal struct Vector3(float x, float y, float z)
{
    public float x = x, y = y, z = z;
    public static Vector3 zero => new(0, 0, 0);
    public static Vector3 one => new(1, 1, 1);
    public static Vector3 right => new(1, 0, 0);
    public static Vector3 up => new(0, 1, 0);
    public static Vector3 forward => new(0, 0, 1);
    public float sqrMagnitude => (float)((double)x*x + (double)y*y + (double)z*z);
    public float magnitude => MathF.Sqrt(sqrMagnitude);
    public Vector3 normalized { get { float length = magnitude; return length > .00001f ? this / length : zero; } }
    public float this[int i] { readonly get => i switch { 0 => x, 1 => y, 2 => z, _ => throw new IndexOutOfRangeException() }; set { if (i == 0) x = value; else if (i == 1) y = value; else if (i == 2) z = value; else throw new IndexOutOfRangeException(); } }
    public static implicit operator NVector(Vector3 v) => new(v.x,v.y,v.z);
    public static implicit operator Vector3(NVector v) => new(v.X,v.Y,v.Z);
    public static Vector3 operator +(Vector3 a,Vector3 b) => new(a.x+b.x,a.y+b.y,a.z+b.z);
    public static Vector3 operator -(Vector3 a,Vector3 b) => new(a.x-b.x,a.y-b.y,a.z-b.z);
    public static Vector3 operator -(Vector3 a) => new(-a.x,-a.y,-a.z);
    public static Vector3 operator *(Vector3 a,float b) => new(a.x*b,a.y*b,a.z*b);
    public static Vector3 operator *(float b,Vector3 a) => a*b;
    public static Vector3 operator /(Vector3 a,float b) => new(a.x/b,a.y/b,a.z/b);
    public static bool operator ==(Vector3 a,Vector3 b) => (a-b).sqrMagnitude < 1e-10f;
    public static bool operator !=(Vector3 a,Vector3 b) => !(a==b);
    public override readonly bool Equals(object? obj) => obj is Vector3 v && x==v.x && y==v.y && z==v.z;
    public override readonly int GetHashCode() => HashCode.Combine(x,y,z);
    public static float Dot(Vector3 a,Vector3 b) => (float)((double)a.x*b.x+(double)a.y*b.y+(double)a.z*b.z);
    public static Vector3 Cross(Vector3 a,Vector3 b) => new((float)((double)a.y*b.z-(double)a.z*b.y),(float)((double)a.z*b.x-(double)a.x*b.z),(float)((double)a.x*b.y-(double)a.y*b.x));
    public static float Distance(Vector3 a,Vector3 b) => (a-b).magnitude;
    public static Vector3 Scale(Vector3 a,Vector3 b) => new(a.x*b.x,a.y*b.y,a.z*b.z);
    public static Vector3 Lerp(Vector3 a,Vector3 b,float t)
    {t=Mathf.Clamp01(t);return new((float)(a.x+((double)b.x-a.x)*t),(float)(a.y+((double)b.y-a.y)*t),(float)(a.z+((double)b.z-a.z)*t));}
    public static Vector3 Project(Vector3 v,Vector3 onto) => onto.sqrMagnitude < float.Epsilon ? zero : onto*(Dot(v,onto)/onto.sqrMagnitude);
    public static float Angle(Vector3 a,Vector3 b)
    { float denominator=MathF.Sqrt(a.sqrMagnitude*b.sqrMagnitude);return denominator<1e-15f?0:(float)Math.Acos(Math.Clamp(Dot(a,b)/denominator,-1,1))*Mathf.Rad2Deg; }
    public static void OrthoNormalize(ref Vector3 normal,ref Vector3 tangent)
    {
        float length=MathF.Sqrt((normal.x*normal.x+normal.y*normal.y)+normal.z*normal.z);
        normal=length>1e-5f?normal/length:right;
        float dot=(tangent.y*normal.y+tangent.x*normal.x)+tangent.z*normal.z;
        tangent-=normal*dot;
        length=MathF.Sqrt((tangent.x*tangent.x+tangent.y*tangent.y)+tangent.z*tangent.z);
        if(length>=1e-5f)tangent/=length;
        else if(MathF.Abs(normal.z)>.70710678f)
        {float reciprocal=1/MathF.Sqrt(normal.z*normal.z+normal.y*normal.y);tangent=new(0,-normal.z*reciprocal,normal.y*reciprocal);}
        else
        {float reciprocal=1/MathF.Sqrt(normal.x*normal.x+normal.y*normal.y);tangent=new(-normal.y*reciprocal,normal.x*reciprocal,0);}
    }
    public static Vector3 Slerp(Vector3 a,Vector3 b,float t)
    {
        t=Mathf.Clamp01(t);float al=MathF.Sqrt(a.x*a.x+a.y*a.y+a.z*a.z),bl=MathF.Sqrt(b.x*b.x+b.y*b.y+b.z*b.z);
        float inverseT=1-t;if(al<1e-5f||bl<1e-5f)return a*inverseT+b*t;
        float length=al*inverseT+bl*t,dot=(a.x*b.x+a.y*b.y+a.z*b.z)/(bl*al);
        if(dot>.99999f)return a*inverseT+b*t;
        var n=a/al;Vector3 axis;float angle;
        if(dot<-.99999f)
        {
            if(MathF.Abs(n.z)>.70710678f){float r=1/MathF.Sqrt(n.z*n.z+n.y*n.y);axis=new(0,-n.z*r,n.y*r);}
            else{float r=1/MathF.Sqrt(n.x*n.x+n.y*n.y);axis=new(-n.y*r,n.x*r,0);}
            angle=t*MathF.PI;
        }
        else
        {
            axis=new(a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x);
            axis/=MathF.Sqrt(axis.x*axis.x+axis.y*axis.y+axis.z*axis.z);angle=MathF.Acos(dot)*t;
        }
        float s=MathF.Sin(angle),c=MathF.Cos(angle),h=1-c,xs=axis.x*s,ys=axis.y*s,zs=axis.z*s;
        float xy=(axis.y*axis.x)*h,xz=(axis.z*axis.x)*h,yz=(axis.z*axis.y)*h;
        float xx=(axis.x*axis.x)*h+c,yy=(axis.y*axis.y)*h+c,zz=(axis.z*axis.z)*h+c;
        return new(((n.x*xx+n.y*(xy-zs))+n.z*(xz+ys))*length,
            ((n.x*(xy+zs)+n.y*yy)+n.z*(yz-xs))*length,((n.x*(xz-ys)+n.y*(yz+xs))+n.z*zz)*length);
    }
}

internal struct Vector4(float x,float y,float z,float w)
{ public float x=x,y=y,z=z,w=w; }

internal struct Quaternion(float x,float y,float z,float w)
{
    public float x=x,y=y,z=z,w=w;
    public static Quaternion identity=>new(0,0,0,1);
    public static implicit operator NQuaternion(Quaternion q)=>new(q.x,q.y,q.z,q.w);
    public static implicit operator Quaternion(NQuaternion q)=>new(q.X,q.Y,q.Z,q.W);
    // Mono's managed float expressions retain double intermediates until a
    // local, argument or result is stored. Preserve those original boundaries.
    public static Quaternion operator *(Quaternion a,Quaternion b)=>new(
        (float)((double)a.w*b.x+(double)a.x*b.w+(double)a.y*b.z-(double)a.z*b.y),
        (float)((double)a.w*b.y+(double)a.y*b.w+(double)a.z*b.x-(double)a.x*b.z),
        (float)((double)a.w*b.z+(double)a.z*b.w+(double)a.x*b.y-(double)a.y*b.x),
        (float)((double)a.w*b.w-(double)a.x*b.x-(double)a.y*b.y-(double)a.z*b.z));
    public static Vector3 operator *(Quaternion q,Vector3 p)
    {
        float x=q.x*2,y=q.y*2,z=q.z*2,xx=q.x*x,yy=q.y*y,zz=q.z*z,xy=q.x*y,xz=q.x*z,yz=q.y*z,wx=q.w*x,wy=q.w*y,wz=q.w*z;
        return new((float)((1-((double)yy+zz))*p.x+((double)xy-wz)*p.y+((double)xz+wy)*p.z),
            (float)(((double)xy+wz)*p.x+(1-((double)xx+zz))*p.y+((double)yz-wx)*p.z),
            (float)(((double)xz-wy)*p.x+((double)yz+wx)*p.y+(1-((double)xx+yy))*p.z));
    }
    public static Quaternion Inverse(Quaternion q)=>new(-q.x,-q.y,-q.z,q.w);
    public static Quaternion AngleAxis(float degrees,Vector3 axis)
    {
        float length=MathF.Sqrt(axis.x*axis.x+axis.y*axis.y+axis.z*axis.z);if(length<=1e-6f)return identity;
        float angle=((degrees/360)*2)*MathF.PI*.5f;float scale=MathF.Sin(angle)/length;
        return new(axis.x*scale,axis.y*scale,axis.z*scale,MathF.Cos(angle));
    }
    public static Quaternion Euler(Vector3 degrees)=>NativeCharacterScene.Euler(degrees);
    public static Quaternion FromToRotation(Vector3 from,Vector3 to)
    {
        from=NativeNormalized(from);to=NativeNormalized(to);if(from.sqrMagnitude==0||to.sqrMagnitude==0)return identity;
        return FromToNormalized(from,to);
    }
    private static Quaternion FromToNormalized(Vector3 from,Vector3 to)
    {
        float dot=from.x*to.x+from.y*to.y+from.z*to.z;
        if(dot>.999999d)return identity;
        if(dot<-.999999d)
        {
            var u=NativeCross(from,Vector3.right);if(u.x*u.x+u.y*u.y+u.z*u.z<1e-6f)u=NativeCross(from,Vector3.up);
            float inverse=1/MathF.Sqrt(u.x*u.x+u.y*u.y+u.z*u.z);u*=inverse;var v=NativeCross(u,from);
            return FromMatrix(new NMatrix(-from.x*from.x+v.x*v.x-u.x*u.x,-from.x*from.y+v.x*v.y-u.x*u.y,-from.x*from.z+v.x*v.z-u.x*u.z,0,
                -from.x*from.y+v.x*v.y-u.x*u.y,-from.y*from.y+v.y*v.y-u.y*u.y,-from.y*from.z+v.y*v.z-u.y*u.z,0,
                -from.x*from.z+v.x*v.z-u.x*u.z,-from.y*from.z+v.y*v.z-u.y*u.z,v.z*v.z-from.z*from.z-u.z*u.z,0,0,0,0,1));
        }
        var c=NativeCross(from,to);float h=(1-dot)/(c.y*c.y+c.x*c.x+c.z*c.z);
        float xx=h*c.x,yy=c.y*h,zz=h*c.z,xy=xx*c.y,xz=xx*c.z,yz=zz*c.y;
        return FromMatrix(new NMatrix(xx*c.x+dot,c.z+xy,xz-c.y,0,xy-c.z,yy*c.y+dot,c.x+yz,0,c.y+xz,yz-c.x,c.z*zz+dot,0,0,0,0,1));
    }
    public static Quaternion LookRotation(Vector3 forward,Vector3 upwards)
    {
        // Engine math calls use native single-precision vector operations,
        // separately from the managed controller's extended intermediates.
        float forwardLength=MathF.Sqrt(forward.x*forward.x+forward.y*forward.y+forward.z*forward.z);
        var z=NativeNormalized(forward);if(z.sqrMagnitude==0)return identity;var x=NativeNormalized(NativeCross(upwards,z));
        if(x.sqrMagnitude==0)return forwardLength>1e-5f?FromToNormalized(Vector3.forward,z):identity;var y=NativeCross(z,x);
        return FromMatrix(new NMatrix(x.x,x.y,x.z,0,y.x,y.y,y.z,0,z.x,z.y,z.z,0,0,0,0,1));
    }
    private static Quaternion FromMatrix(NMatrix m)
    {
        float trace=m.M11+m.M22+m.M33;Quaternion q;
        if(trace>0)
        {
            float root=MathF.Sqrt(trace+1),scale=.5f/root;
            q=new((m.M23-m.M32)*scale,(m.M31-m.M13)*scale,(m.M12-m.M21)*scale,root*.5f);
        }
        else
        {
            Span<float> matrix=stackalloc float[]{m.M11,m.M12,m.M13,m.M21,m.M22,m.M23,m.M31,m.M32,m.M33};
            int i=m.M22>m.M11?1:0;if(m.M33>matrix[i*3+i])i=2;int j=(i+1)%3,k=(j+1)%3;
            float root=MathF.Sqrt(matrix[i*3+i]-matrix[j*3+j]-matrix[k*3+k]+1),scale=.5f/root;
            Span<float> values=stackalloc float[4];values[i]=root*.5f;values[3]=(matrix[j*3+k]-matrix[k*3+j])*scale;
            values[j]=(matrix[i*3+j]+matrix[j*3+i])*scale;values[k]=(matrix[i*3+k]+matrix[k*3+i])*scale;
            q=new(values[0],values[1],values[2],values[3]);
        }
        float length=MathF.Sqrt(q.x*q.x+q.y*q.y+q.z*q.z+q.w*q.w);
        return new(q.x/length,q.y/length,q.z/length,q.w/length);
    }
    private static Vector3 NativeNormalized(Vector3 value)
    {float length=MathF.Sqrt(value.x*value.x+value.y*value.y+value.z*value.z);return length>=1e-5f?value/length:Vector3.zero;}
    private static Vector3 NativeCross(Vector3 a,Vector3 b)=>new(a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x);
    public static Quaternion Lerp(Quaternion a,Quaternion b,float t)
    {
        t=Mathf.Clamp01(t);float dot=a.x*b.x+a.y*b.y+a.z*b.z+a.w*b.w;
        Quaternion q=dot<0?new(a.x-(b.x+a.x)*t,a.y-(b.y+a.y)*t,a.z-(b.z+a.z)*t,a.w-(b.w+a.w)*t)
            :new(a.x+(b.x-a.x)*t,a.y+(b.y-a.y)*t,a.z+(b.z-a.z)*t,a.w+(b.w-a.w)*t);
        float length=MathF.Sqrt(q.x*q.x+q.y*q.y+q.z*q.z+q.w*q.w);
        return new(q.x/length,q.y/length,q.z/length,q.w/length);
    }
}

internal struct Matrix4x4(NMatrix value)
{
    internal NMatrix Value=value;
    public Matrix4x4 inverse
    {
        get
        {
            // The controller's setup matrices use scalar elimination with
            // partial pivoting. A cofactor inverse rounds the terminal offsets
            // differently before the first dynamics step.
            Span<float> rows=stackalloc float[32]
            {
                Value.M11,Value.M21,Value.M31,Value.M41,1,0,0,0,
                Value.M12,Value.M22,Value.M32,Value.M42,0,1,0,0,
                Value.M13,Value.M23,Value.M33,Value.M43,0,0,1,0,
                Value.M14,Value.M24,Value.M34,Value.M44,0,0,0,1
            };
            Span<int> order=stackalloc int[]{0,1,2,3};
            for(int column=0;column<3;column++)
            {
                // Select from the bottom up, retaining each losing row's slot.
                for(int candidate=3;candidate>column;candidate--)
                    if(MathF.Abs(rows[order[candidate]*8+column])>MathF.Abs(rows[order[candidate-1]*8+column]))
                        (order[candidate],order[candidate-1])=(order[candidate-1],order[candidate]);
                int pivot=order[column]*8;
                if(rows[pivot+column]==0)throw new InvalidDataException("Singular simulation transform.");
                for(int row=column+1;row<4;row++)
                {
                    int target=order[row]*8;float amount=rows[target+column]/rows[pivot+column];
                    for(int element=column+1;element<8;element++)
                        if(element<4||rows[pivot+element]!=0)rows[target+element]-=rows[pivot+element]*amount;
                }
            }
            for(int column=3;column>=0;column--)
            {
                int pivot=order[column]*8;
                if(rows[pivot+column]==0)throw new InvalidDataException("Singular simulation transform.");
                float reciprocal=1/rows[pivot+column];
                for(int element=4;element<8;element++)rows[pivot+element]*=reciprocal;
                for(int row=column-1;row>=0;row--)
                {
                    int target=order[row]*8;float amount=rows[target+column];
                    for(int element=4;element<8;element++)rows[target+element]-=rows[pivot+element]*amount;
                }
            }
            int a=order[0]*8+4,b=order[1]*8+4,c=order[2]*8+4,d=order[3]*8+4;
            return new(new(rows[a],rows[b],rows[c],rows[d],rows[a+1],rows[b+1],rows[c+1],rows[d+1],
                rows[a+2],rows[b+2],rows[c+2],rows[d+2],rows[a+3],rows[b+3],rows[c+3],rows[d+3]));
        }
    }
    public Vector3 MultiplyPoint3x4(Vector3 p)=>new(
        (float)((double)Value.M11*p.x+(double)Value.M21*p.y+(double)Value.M31*p.z+Value.M41),
        (float)((double)Value.M12*p.x+(double)Value.M22*p.y+(double)Value.M32*p.z+Value.M42),
        (float)((double)Value.M13*p.x+(double)Value.M23*p.y+(double)Value.M33*p.z+Value.M43));
    public Vector3 MultiplyVector(Vector3 v)=>new(
        (float)((double)Value.M11*v.x+(double)Value.M21*v.y+(double)Value.M31*v.z),
        (float)((double)Value.M12*v.x+(double)Value.M22*v.y+(double)Value.M32*v.z),
        (float)((double)Value.M13*v.x+(double)Value.M23*v.y+(double)Value.M33*v.z));
    public void SetColumn(int index,Vector4 v)
    {
        switch(index)
        {
            case 0:Value.M11=v.x;Value.M12=v.y;Value.M13=v.z;Value.M14=v.w;break;
            case 1:Value.M21=v.x;Value.M22=v.y;Value.M23=v.z;Value.M24=v.w;break;
            case 2:Value.M31=v.x;Value.M32=v.y;Value.M33=v.z;Value.M34=v.w;break;
            case 3:Value.M41=v.x;Value.M42=v.y;Value.M43=v.z;Value.M44=v.w;break;
            default:throw new ArgumentOutOfRangeException(nameof(index));
        }
    }
}

internal static class Mathf
{
    internal const float Deg2Rad=.0174532924f,Rad2Deg=57.29578f;
    internal static float Abs(float v)=>MathF.Abs(v);
    internal static float Sign(float v)=>v>=0?1:-1;
    internal static float Sqrt(float v)=>MathF.Sqrt(v);
    internal static float Clamp(float v,float min,float max)=>Math.Clamp(v,min,max);
    internal static float Clamp01(float v)=>Math.Clamp(v,0,1);
    internal static float Min(float a,float b)=>MathF.Min(a,b);
    internal static float Max(float a,float b)=>MathF.Max(a,b);
    internal static int Max(int a,int b)=>Math.Max(a,b);
    internal static int Min(int a,int b)=>Math.Min(a,b);
    internal static float Lerp(float a,float b,float t)=>(float)(a+((double)b-a)*Clamp01(t));
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static float InverseLerp(float a,float b,float v)=>a==b?0:Clamp01((float)(((double)v-a)/((double)b-a)));
}

internal sealed class SimulationGraph
{
    private readonly Dictionary<NativeSceneNode,Transform> nodes=[];
    internal Transform Wrap(NativeSceneNode node)
    { if(!nodes.TryGetValue(node,out var result))nodes.Add(node,result=new(this,node));return result; }
}
internal sealed class Transform(SimulationGraph graph,NativeSceneNode node):IEnumerable<Transform>
{
    internal NativeSceneNode Node=>node;
    internal string name=>node.Name;
    internal Transform? parent=>node.Parent==null?null:graph.Wrap(node.Parent);
    internal int childCount=>node.Children.Count;
    internal Vector3 localPosition{get=>node.LocalPosition;set=>node.LocalPosition=value;}
    internal Vector3 localScale{get=>node.LocalScale;set=>node.LocalScale=value;}
    internal Quaternion localRotation{get=>node.LocalRotation;set=>node.LocalRotation=value;}
    internal Vector3 position{get=>node.Position;set=>node.Position=value;}
    internal Quaternion rotation{get=>node.Rotation;set=>node.Rotation=value;}
    internal Vector3 forward=>rotation*Vector3.forward;
    internal Vector3 right=>rotation*Vector3.right;
    internal Vector3 up=>rotation*Vector3.up;
    internal Vector3 lossyScale=>node.LossyScale;
    internal Matrix4x4 localToWorldMatrix=>new(node.WorldMatrix);
    internal Vector3 TransformPoint(Vector3 p)=>node.TransformPoint(p);
    internal Vector3 InverseTransformPoint(Vector3 p)=>node.InverseTransformPoint(p);
    internal Vector3 TransformVector(Vector3 p)=>node.TransformVector(p);
    internal Vector3 TransformDirection(Vector3 p)=>node.TransformDirection(p);
    internal Vector3 InverseTransformDirection(Vector3 p)=>node.InverseTransformDirection(p);
    internal void SetPositionAndRotation(Vector3 p,Quaternion r){position=p;rotation=r;}
    internal bool IsChildOf(Transform ancestor)
    { for(var current=node;current!=null;current=current.Parent)if(current==ancestor.Node)return true;return false; }
    internal void RotateAround(Vector3 pivot,Vector3 axis,float angle)
    { var q=Quaternion.AngleAxis(angle,axis);position=pivot+q*(position-pivot);node.RotateWorld(axis,angle*.0174532924f); }
    internal int GetInstanceID()=>System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(node);
    public IEnumerator<Transform> GetEnumerator()=>node.Children.Select(graph.Wrap).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
}

internal enum WeightedMode{None=0,In=1,Out=2,Both=3}
internal enum WrapMode{ClampForever,Loop,PingPong}
internal sealed class Keyframe(float time,float value,float inSlope,float outSlope,float inWeight,float outWeight)
{ internal float Time=time,Value=value,InSlope=inSlope,OutSlope=outSlope,InWeight=inWeight,OutWeight=outWeight;internal WeightedMode weightedMode; }
internal sealed class AnimationCurve(Keyframe[] keys)
{
    internal int length=>keys.Length;
    internal WrapMode preWrapMode,postWrapMode;
    internal float Evaluate(float time)
    {
        if(keys.Length==0)return 0;if(keys.Length==1)return keys[0].Value;
        float start=keys[0].Time,end=keys[^1].Time,duration=end-start;
        if(duration<=0)return keys[^1].Value;
        if(time<start||time>=end)
        {
            var wrap=time<start?preWrapMode:postWrapMode;
            if(wrap==WrapMode.ClampForever)return time<start?keys[0].Value:keys[^1].Value;
            float period=wrap==WrapMode.PingPong?duration*2:duration;float offset=((time-start)%period+period)%period;
            time=start+(offset>duration?period-offset:offset);
        }
        int hi=1;while(hi<keys.Length-1&&keys[hi].Time<=time)hi++;
        var a=keys[hi-1];var b=keys[hi];float dt=b.Time-a.Time;
        if(dt<=0)return b.Value;if(float.IsInfinity(a.OutSlope)||float.IsInfinity(b.InSlope))return time<b.Time?a.Value:b.Value;
        float t=Mathf.Clamp01((time-a.Time)/dt);
        if((a.weightedMode&WeightedMode.Out)==0&&(b.weightedMode&WeightedMode.In)==0)
        {
            // Evaluate the cached polynomial in elapsed key time. The
            // normalized Hermite basis changes rounding near curve endpoints.
            dt=MathF.Max(.0001f,dt);float difference=b.Value-a.Value;
            float incoming=dt*b.InSlope,outgoing=dt*a.OutSlope,inverseSquared=1/(dt*dt);
            float cubic=(((incoming+outgoing)-difference)-difference)*inverseSquared/dt;
            float quadratic=((((difference+difference)+difference)-outgoing)-outgoing-incoming)*inverseSquared;
            float elapsed=time-a.Time;
            return ((elapsed*cubic+quadratic)*elapsed+a.OutSlope)*elapsed+a.Value;
        }
        float aw=(a.weightedMode&WeightedMode.Out)!=0?a.OutWeight:1f/3,bw=(b.weightedMode&WeightedMode.In)!=0?b.InWeight:1f/3;
        float low=0,high=1,u=t;
        for(int iteration=0;iteration<24;iteration++){float x=Bezier(0,aw,1-bw,1,u);if(x<t)low=u;else high=u;u=(low+high)*.5f;}
        return Bezier(a.Value,a.Value+a.OutSlope*dt*aw,b.Value-b.InSlope*dt*bw,b.Value,u);
    }
    private static float Bezier(float a,float b,float c,float d,float t){float s=1-t;return s*s*s*a+3*s*s*t*b+3*s*t*t*c+t*t*t*d;}
}
