// Shows a plane's 3D-scan texture (WallScanSplat's accumulation: rgb = colour x weight, a = weight). Unlit, alpha-tested:
// what no frame has seen stays a hole, so missing spots are obvious. Single-pass stereo safe (Quest).
Shader "Hidden/XRHouse/WallScanShow"
{
    Properties { _MainTex ("Accumulation", 2D) = "black" {} _MinWeight ("Min weight", Float) = 0.0005 _FillLod ("Pinhole fill (mip level)", Float) = 2 }
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
            sampler2D _MainTex;
            float _MinWeight, _FillLod;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };
            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }
            float4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float4 a = tex2D(_MainTex, i.uv);
                if (a.a < _MinWeight)
                {
                    // a texel no frame saw: a small gap (a few cm) borrows the colour of its neighbourhood from a
                    // coarser mip; a big hole stays a hole, so missing spots are still obvious
                    float4 f = tex2Dlod(_MainTex, float4(i.uv, 0, _FillLod));
                    clip(f.a - _MinWeight);
                    return float4(f.rgb / f.a, 1);
                }
                return float4(a.rgb / a.a, 1);
            }
            ENDCG
        }
    }
}
