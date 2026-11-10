Shader "Hidden/HS2Manager/ModelSceneBake"
{
 Properties { _Cube("Environment",Cube)=""{} _Lut3D("Grading",3D)=""{} }
 SubShader
 {
  Cull Off ZWrite Off ZTest Always
  Pass
  {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #pragma target 3.0
   #include "UnityCG.cginc"
   samplerCUBE _Cube;
   sampler3D _Lut3D;
   float _Kind,_Size,_DecodeCube;
   float4 _Decode;
   float4 frag(v2f_img i):SV_Target
   {
    if(_Kind>0.5)
    {
     float2 p=floor(i.uv*float2(_Size*_Size,_Size));
     float3 xyz=float3(fmod(p.x,_Size),p.y,floor(p.x/_Size));
     return float4(tex3Dlod(_Lut3D,float4((xyz+.5)/_Size,0)).rgb,1);
    }
    // Portable equirectangular convention: +Z at u=.5, +X at u=.75, +Y at v=1.
    float longitude=(i.uv.x-.5)*2*UNITY_PI,latitude=(i.uv.y-.5)*UNITY_PI;
    float3 direction=float3(sin(longitude)*cos(latitude),sin(latitude),cos(longitude)*cos(latitude));
    float4 value=texCUBElod(_Cube,float4(direction,0));
    return float4(_DecodeCube>.5?DecodeHDR(value,_Decode):value.rgb,1);
   }
   ENDCG
  }
 }
}
