Shader "Hidden/HS2Manager/ModelExportBake"
{
 Properties
 {
  _MainTex("Main",2D)="white"{} _ColorMask("Color mask",2D)="black"{} _ColorMask_B("Color mask B",2D)="black"{}
  _AlphaMask("Alpha",2D)="white"{} _AlphaMask2("Alpha 2",2D)="white"{}
  _DetailMainTex("Detail",2D)="black"{} _MetallicGlossMap("Metallic",2D)="white"{} _DetailMask("Hair detail normal",2D)="bump"{}
  _BumpMap("Normal",2D)="bump"{} _BumpMap2("Detail normal",2D)="bump"{}
  _OcclusionMap("AO",2D)="white"{} _EmissionMap("Emission",2D)="white"{}
  _Texture2("Iris",2D)="black"{} _Texture3("Pupil / brow",2D)="black"{} _Texture4("Highlight",2D)="black"{}
  _MeshColorMask_B("Hair streak",2D)="black"{}
  _Color("Color",Color)=(1,1,1,1) _Color2("Color 2",Color)=(1,1,1,1) _Color3("Color 3",Color)=(1,1,1,1) _Color4("Color 4",Color)=(1,1,1,1)
  _TopColor("Top",Color)=(1,1,1,1) _UnderColor("Under",Color)=(1,1,1,1) _MeshColor_01("Streak",Color)=(1,1,1,1)
  _EmissionColor("Emission color",Color)=(0,0,0,1)
  _texture2uv("Iris UV",Vector)=(1,1,0,0) _texture3uv("Pupil UV",Vector)=(1,1,0,0) _Texture4UV("Highlight UV",Vector)=(1,1,0,0)
  _Texture3UV("Brow UV",Vector)=(0,0,1,1) _Texture3Rotator("Brow rotation",Float)=0 _Texture4Rotator("Highlight rotation",Float)=0
  _BumpScale("Normal scale",Float)=1 _BumpScale2("Detail normal scale",Float)=1
  _alpha_a("Alpha A",Float)=0 _alpha_b("Alpha B",Float)=0 _alpha_c("Alpha C",Float)=0 _alpha_d("Alpha D",Float)=0
  _Emission("Eye emission",Float)=0
  _eyebaseoffset("Eye base offset",Vector)=(0,0,0,0)
  _TopGradation("Top gradation",Float)=1 _UnderGradation("Under gradation",Float)=1 _ColorMask_A0B1("Hair mask selector",Float)=0
  _DetailMainScale("Packed detail scale",Float)=1 _DetailScale("Hair detail scale",Float)=0
  _ShadowScale("Eye shadow falloff",Float)=0.5 _NamidaScale("Tear scale",Float)=0 _OcclusionStrength("AO strength",Float)=1
 }
 SubShader
 {
  Cull Off ZWrite Off ZTest Always
  Pass
  {
   CGPROGRAM
   #pragma vertex vert_img
   #pragma fragment frag
   #include "UnityCG.cginc"
   sampler2D _MainTex,_ColorMask,_ColorMask_B,_AlphaMask,_AlphaMask2,_DetailMainTex,_DetailMask,_BumpMap,_BumpMap2,_MetallicGlossMap,_OcclusionMap,_EmissionMap,_Texture2,_Texture3,_Texture4,_MeshColorMask_B;
   float4 _MainTex_ST,_ColorMask_ST,_ColorMask_B_ST,_BumpMap_ST,_BumpMap2_ST,_OcclusionMap_ST,_EmissionMap_ST,_MeshColorMask_B_ST,_DetailMainTex_ST,_MetallicGlossMap_ST,_DetailMask_ST;
   float4 _Color,_Color2,_Color3,_Color4,_TopColor,_UnderColor,_MeshColor_01,_EmissionColor;
   float4 _texture2uv,_texture3uv,_Texture4UV,_Texture3UV;
   float _Texture3Rotator,_Texture4Rotator,_BumpScale,_BumpScale2,_alpha_a,_alpha_b,_alpha_c,_alpha_d,_Emission;
   float _BakeKind,_MaterialKind,_BakeAlpha,_BaseMetallic,_BaseSmoothness,_IgnoreMainAlpha;
   float _OriginalHair,_OriginalEye,_PackedKind,_TopGradation,_UnderGradation,_ColorMask_A0B1,_DetailMainScale,_DetailScale,_ShadowScale,_NamidaScale,_OcclusionStrength,_EmissionGain;
   float4 _eyebaseoffset;
   float _HasMainTex,_HasColorMask,_HasAlphaMask,_HasAlphaMask2,_HasDetailMainTex,_HasBumpMap,_HasBumpMap2,_HasMetallicGlossMap,_HasOcclusionMap,_HasEmissionMap,_HasTexture2,_HasTexture3,_HasTexture4,_HasMeshColorMask_B;
   float _HasColorMask_B,_HasDetailMask;
   float2 layout(float2 uv,float4 st,float angle)
   {
    float2 p=uv-.5;float s=sin(angle*UNITY_PI),c=cos(angle*UNITY_PI);
    return float2(p.x*c-p.y*s,p.x*s+p.y*c)*st.xy+.5+st.zw;
   }
   float inside(float2 uv){return step(0,uv.x)*step(0,uv.y)*step(uv.x,1)*step(uv.y,1);}
   float4 baseColor(float2 uv)
   {
    float4 main=tex2D(_MainTex,uv*_MainTex_ST.xy+_MainTex_ST.zw);
    float4 color=main*_Color;
    if(_MaterialKind==1)
    {
     float4 mask=tex2D(_ColorMask,uv*_ColorMask_ST.xy+_ColorMask_ST.zw);
     if(_OriginalHair>0)
     {
      // hair_07/08 use the B/G channels of two masks, not vertical UV gradients.
      float2 b=pow(saturate(tex2D(_ColorMask_B,uv*_ColorMask_B_ST.xy+_ColorMask_B_ST.zw).bg),max(float2(_TopGradation,_UnderGradation),.001));
      float3 tint=saturate(lerp(_Color.rgb,lerp(lerp(_Color.rgb,_TopColor.rgb,b.x),_UnderColor.rgb,b.y),(b.x+b.y)*_ColorMask_A0B1*_HasColorMask_B));
      float3 shade=lerp(tint*.745283,tint+float3(.084,.049,.049),main.b);
      shade=lerp(shade,tint+float3(.070588,.066353,.066353),main.g);
      float2 a=pow(saturate(mask.bg),max(float2(_TopGradation,_UnderGradation),.001));
      shade=lerp(shade,lerp(lerp(tint,_TopColor.rgb,a.x),_UnderColor.rgb,a.y),(a.x+a.y)*(1-_ColorMask_A0B1)*_HasColorMask);
      color=float4(saturate(shade),main.a);
     }
     else
     {
      float3 tint=lerp(lerp(_UnderColor.rgb,_Color.rgb,saturate(uv.y*2)),_TopColor.rgb,saturate(uv.y*2-1));
      color=float4(tint*(.75+.25*main.b),main.a*_Color.a);
      if(_HasMeshColorMask_B>0)color.rgb=lerp(color.rgb,_MeshColor_01.rgb,tex2D(_MeshColorMask_B,uv*_MeshColorMask_B_ST.xy+_MeshColorMask_B_ST.zw).r);
      if(_HasColorMask>0)color.a*=mask.a;
     }
    }
    else if(_MaterialKind==2)
    {
     if(_OriginalEye>0)
     {
      // AIT iris offset includes a 0.06 vertical correction. Pupil z is a
      // zoom multiplier, and pupil coverage is stored in alpha, not red.
      float2 offset=_texture2uv.zw+_eyebaseoffset.xy;
      float2 irisUV=(uv-.5)*_texture2uv.xy+.5+offset+float2(0,(_texture2uv.y-1)*.06);
      float4 iris=tex2D(_Texture2,irisUV);
      float2 pupilUV=(uv-.5)*_texture2uv.xy*_texture3uv.xy*_texture3uv.z+.5+offset*_texture3uv.z;
      float4 pupil=tex2D(_Texture3,pupilUV);
      color=float4(lerp(main.rgb*_Color.rgb,iris.r*_Color2.rgb,saturate(iris.b*_Color2.a)*inside(irisUV)),1);
      color.rgb=lerp(color.rgb,_Color3.rgb,saturate(pupil.a*_Color3.a)*inside(pupilUV));
      float2 p=uv+_Texture4UV.zw-.5;float s=sin(_Texture4Rotator*UNITY_PI),c=cos(_Texture4Rotator*UNITY_PI);
      float2 hlUV=float2(p.x*c+p.y*s,-p.x*s+p.y*c)*_Texture4UV.xy+.5;
      float highlight=tex2D(_Texture4,hlUV).r*inside(hlUV)*_Color4.a*_BaseSmoothness*_HasTexture4;
      color.rgb=lerp(color.rgb,_Color4.rgb,saturate(highlight));
     }
     else
     {
     float2 irisUV=layout(uv,_texture2uv,0);float4 iris=tex2D(_Texture2,irisUV);
     float2 pupilUV=layout(irisUV,_texture3uv,0);float4 pupil=tex2D(_Texture3,pupilUV);
     float3 tint=lerp(_Color2.rgb*(.2+.9*iris.r),_Color3.rgb,pupil.r*inside(pupilUV));
     color=float4(lerp(_Color.rgb,tint,iris.b*inside(irisUV)),1);
     float2 hlUV=layout(uv,_Texture4UV,_Texture4Rotator);float highlight=tex2D(_Texture4,hlUV).r*inside(hlUV)*_Color4.a;
     color.rgb=lerp(color.rgb,_Color4.rgb,highlight*_HasTexture4);
     }
    }
    else if(_MaterialKind==3)
    {
     // Skin color, makeup and overlays have already been composed by the original creators.
     color=main;
     if(_HasTexture3>0)
     {
      float side=uv.x<.5?1:-1;
      float2 brow=uv-float2(.5-side*(.145+_Texture3UV.x),.445+_Texture3UV.y);brow.x*=side;
      float s=sin(_Texture3Rotator*UNITY_PI),c=cos(_Texture3Rotator*UNITY_PI);
      brow=float2(brow.x*c-brow.y*s,brow.x*s+brow.y*c)*4*_Texture3UV.zw+float2(.5,.56);
      color.rgb=lerp(color.rgb,_Color3.rgb,tex2D(_Texture3,brow).b*inside(brow)*_Color3.a);
     }
    }
    else if(_MaterialKind==4)color=float4(_Color.rgb,main.r*_Color.a);
    else if(_MaterialKind==5)color=float4(lerp(float3(1,1,1),_Color.rgb,saturate(pow(saturate(main.a),max(_ShadowScale,.001))*_Color.a)),1);
    else if(_MaterialKind==6)
    {
     float exponent=8-7*saturate(_NamidaScale);
     color=float4(1,1,1,saturate(pow(saturate(main.g),exponent)*exponent));
    }
    else if(_HasColorMask>0)
    {
     float3 mask=tex2D(_ColorMask,uv*_ColorMask_ST.xy+_ColorMask_ST.zw).rgb;
     float3 tint=lerp(lerp(lerp(_Color.rgb,_Color2.rgb,mask.r),_Color3.rgb,mask.g),_Color4.rgb,mask.b);
     color.rgb=main.rgb*tint;
    }
    if(_IgnoreMainAlpha>0)color.a=1;
    if(_HasAlphaMask>0)
    {
     float3 mask=tex2D(_AlphaMask,uv).rgb;
     color.a*=lerp(1,mask.r,saturate(_alpha_a))*lerp(1,mask.g,saturate(_alpha_b))*lerp(1,mask.b,saturate(_alpha_c));
    }
    if(_HasAlphaMask2>0)color.a*=lerp(1,tex2D(_AlphaMask2,uv).r,saturate(_alpha_d));
    if(_BakeAlpha==0)color.a=1;
    return color;
   }
   float4 frag(v2f_img input):SV_Target
   {
    float2 uv=input.uv;
    if(_BakeKind==6){float mask=tex2D(_MeshColorMask_B,uv*_MeshColorMask_B_ST.xy+_MeshColorMask_B_ST.zw).r;return float4(mask,mask,mask,1);}
    if(_BakeKind==0)return baseColor(uv);
    if(_BakeKind==1)
    {
     float3 n=float3(0,0,1);
     if(_HasBumpMap>0){n=UnpackNormal(tex2D(_BumpMap,uv*_BumpMap_ST.xy+_BumpMap_ST.zw));n.xy*=_BumpScale;n=normalize(n);}
     if(_OriginalHair>0&&_HasDetailMask>0){float3 d=UnpackNormal(tex2D(_DetailMask,uv*_DetailMask_ST.xy*16+_DetailMask_ST.zw));n=normalize(lerp(n,d,saturate(_DetailScale)));}
     if(_HasBumpMap2>0){float3 d=UnpackNormal(tex2D(_BumpMap2,uv*_BumpMap2_ST.xy+_BumpMap2_ST.zw));d.xy*=_BumpScale2;n=normalize(float3(n.xy+d.xy,n.z*d.z));}
     return float4(n*.5+.5,1);
    }
    if(_BakeKind==2||_BakeKind==3)
    {
     float metal=_BaseMetallic,smooth=_BaseSmoothness;
     float4 packed=tex2D(_MetallicGlossMap,uv*_MetallicGlossMap_ST.xy+_MetallicGlossMap_ST.zw);
     float4 detail=tex2D(_DetailMainTex,uv*_DetailMainTex_ST.xy+_DetailMainTex_ST.zw);
     if(_PackedKind==1&&_HasDetailMainTex>0){metal=max(metal,detail.r*_DetailMainScale);smooth=max(smooth,detail.b*_DetailMainScale);}
     else if(_PackedKind==2&&_HasDetailMainTex>0){metal=detail.r*packed.r;smooth=detail.b*packed.a;}
     else
     {
      if(_HasMetallicGlossMap>0){metal=packed.r;smooth*=packed.a;}
      // Unity Standard's Detail Albedo is color data, not a metallic/gloss map.
     }
     metal=saturate(metal);smooth=saturate(smooth);
     float value=_BakeKind==2?metal:1-smooth;return float4(value,value,value,1);
    }
    if(_BakeKind==4){float ao=_HasOcclusionMap>0?tex2D(_OcclusionMap,uv*_OcclusionMap_ST.xy+_OcclusionMap_ST.zw).g:1;ao=lerp(1,ao,saturate(_OcclusionStrength));return float4(ao,ao,ao,1);}
    float3 emission=_EmissionColor.rgb*_EmissionGain;
    if(_HasEmissionMap>0)emission*=tex2D(_EmissionMap,uv*_EmissionMap_ST.xy+_EmissionMap_ST.zw).rgb;
    if(_MaterialKind==2)emission+=baseColor(uv).rgb*_Emission;
    return float4(emission,1);
   }
   ENDCG
  }
 }
}
