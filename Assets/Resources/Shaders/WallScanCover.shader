// While the 3D scan records over passthrough: a blue veil with a 25 cm grid over every scanned surface that fades as
// frames see it (WallScanSplat's accumulated weight) - what is still blue has not been scanned yet. Transparent,
// single-pass stereo safe (Quest).
Shader "Hidden/XRHouse/WallScanCover"
{
    Properties { _MainTex ("Accumulation", 2D) = "black" {} _Full ("Weight that counts as done", Float) = 0.35 _Size ("Plane size (m)", Vector) = (1, 1, 0, 0) }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Cull Off ZWrite Off Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float _Full;
            float4 _Size;
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
                float w = tex2D(_MainTex, i.uv).a;
                float todo = 1 - saturate(w / _Full);            // 1 = never seen, 0 = done
                float2 m = i.uv * _Size.xy / 0.25;                // 25 cm grid
                float2 g = abs(frac(m) - 0.5);
                float gridLine = step(0.47, max(g.x, g.y));
                float3 col = lerp(float3(0.15, 0.45, 1.0), float3(0.75, 0.9, 1.0), gridLine);
                float a = todo * (0.38 + 0.35 * gridLine);
                return float4(col, a);
            }
            ENDCG
        }
    }
}
