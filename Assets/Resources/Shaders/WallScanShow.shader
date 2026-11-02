// Shows a plane's 3D-scan texture (WallScanSplat's accumulation: rgb = colour x weight, a = weight). Unlit, alpha-tested:
// what no frame has seen stays a hole, so missing spots are obvious. Single-pass stereo safe (Quest).
Shader "Hidden/XRHouse/WallScanShow"
{
    Properties { _MainTex ("Accumulation", 2D) = "black" {} _MinWeight ("Min weight", Float) = 0.0005 _FillLod ("Pinhole fill (mip level)", Float) = 2 _Sharpen ("Unsharp amount", Float) = 0.35 _EnvironmentDepthBias ("Environment Depth Bias", Float) = 0.0 }
    SubShader
    {
        Tags { "Queue" = "AlphaTest" "RenderType" = "TransparentCutout" "IgnoreProjector" = "True" }
        Cull Off ZWrite On
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma target 3.0
            #include "UnityCG.cginc"
            // real walls (of other rooms, or anything in front) hide this quad instead of it always drawing on top
            // of passthrough - without this a scanned plane "shines through" any real geometry between it and the eye.
            #include "Packages/com.meta.xr.sdk.core/Shaders/EnvironmentDepth/BiRP/EnvironmentOcclusionBiRP.cginc"
            #pragma multi_compile _ HARD_OCCLUSION SOFT_OCCLUSION
            sampler2D _MainTex;
            float _MinWeight, _FillLod, _Sharpen, _EnvironmentDepthBias;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; META_DEPTH_VERTEX_OUTPUT(1) UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                META_DEPTH_INITIALIZE_VERTEX_OUTPUT(o, v.vertex);
                return o;
            }
            float4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float4 a = tex2D(_MainTex, i.uv);
                float4 outColor;
                if (a.a < _MinWeight)
                {
                    // a texel no frame saw: a small gap (a few cm) borrows the colour of its neighbourhood from a
                    // coarser mip; a big hole stays a hole, so missing spots are still obvious
                    float4 f = tex2Dlod(_MainTex, float4(i.uv, 0, _FillLod));
                    clip(f.a - _MinWeight);
                    outColor = float4(f.rgb / f.a, 1);
                }
                else
                {
                    float3 c = a.rgb / a.a;
                    // unsharp mask against a coarser mip: gives back the crispness the multi-frame blend smooths away
                    // (one extra tap; only where the blurred neighbourhood is covered, so hole edges don't ring)
                    float4 b = tex2Dlod(_MainTex, float4(i.uv, 0, 1.5));
                    if (b.a >= _MinWeight) c = saturate(c + _Sharpen * (c - b.rgb / b.a));
                    outColor = float4(c, 1);
                }
                META_DEPTH_OCCLUDE_OUTPUT_PREMULTIPLY(i, outColor, _EnvironmentDepthBias);
                return outColor;
            }
            ENDCG
        }
    }
}
