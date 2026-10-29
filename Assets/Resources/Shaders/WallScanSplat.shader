// Paints one camera frame onto one scanned plane (wall / floor / ceiling) of the house - the "3D scan" texture.
// The target is the plane's accumulation texture (ARGBHalf): rgb = colour x weight, a = weight, summed over frames
// (Blend One One). The weight favours frames that look straight at the texel, from close, sharp, near the image middle -
// raised to a high power so the best views win and seams stay soft. WallScanShow divides rgb by a.
Shader "Hidden/XRHouse/WallScanSplat"
{
    Properties { _MainTex ("Photo", 2D) = "white" {} }
    SubShader
    {
        ZTest Always Cull Off ZWrite Off

        // Pass 0: splat a photo
        Pass
        {
            Blend One One
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _Origin, _AxisU, _AxisV, _Normal, _CamPos, _Intr; // _Intr = fu, fv, cu, cv (viewport units)
            float4x4 _WorldToCam;
            float _Sharp;
            // the room's other planes: a texel hidden behind one of them (an L-shaped room's corner, a wall jutting
            // out) is not painted from this frame
            float4 _OccO[24], _OccU[24], _OccV[24];
            int _OccCount;

            bool Occluded(float3 cam, float3 p)
            {
                float3 d = p - cam;
                for (int k = 0; k < _OccCount; k++)
                {
                    float3 o = _OccO[k].xyz, u = _OccU[k].xyz, v = _OccV[k].xyz;
                    float3 n = cross(u, v);
                    float den = dot(n, d);
                    if (abs(den) < 1e-6) continue;
                    float t = dot(n, o - cam) / den;
                    if (t <= 0.01 || t >= 0.985) continue;
                    float3 x = cam + t * d - o;
                    float a = dot(x, u) / dot(u, u), b = dot(x, v) / dot(v, v);
                    if (a > 0.01 && a < 0.99 && b > 0.01 && b < 0.99) return true;
                }
                return false;
            }

            float4 frag(v2f_img i) : SV_Target
            {
                float3 p = _Origin.xyz + i.uv.x * _AxisU.xyz + i.uv.y * _AxisV.xyz;
                float3 c = mul(_WorldToCam, float4(p, 1)).xyz;
                if (c.z < 0.15) return 0;
                float2 img = float2(_Intr.z + _Intr.x * c.x / c.z, _Intr.w + _Intr.y * c.y / c.z);
                if (img.x <= 0 || img.y <= 0 || img.x >= 1 || img.y >= 1) return 0;
                float3 toCam = _CamPos.xyz - p;
                float dist = length(toCam);
                float facing = dot(toCam / dist, _Normal.xyz);
                if (facing < 0.25) return 0;
                if (Occluded(_CamPos.xyz, p)) return 0;
                float2 e = min(img, 1 - img);                   // distance to the image border
                float edge = saturate(min(e.x, e.y) * 6);       // fades out over the outer ~17 %
                float s = facing * facing * edge * _Sharp / max(dist, 0.6);
                float w = s * s * s * s;
                float3 col = tex2D(_MainTex, img).rgb;
                return float4(col * w, w);
            }
            ENDCG
        }

        // Pass 1: load a saved texture back (colour + coverage in alpha) as a moderately weighted start
        Pass
        {
            Blend One One
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float _LoadWeight;
            float4 frag(v2f_img i) : SV_Target
            {
                float4 t = tex2D(_MainTex, i.uv);
                float w = t.a * _LoadWeight;
                return float4(t.rgb * w, w);
            }
            ENDCG
        }

        // Pass 2: normalise for saving (rgb / weight, alpha = how well covered)
        Pass
        {
            Blend Off
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float _LoadWeight;
            float4 frag(v2f_img i) : SV_Target
            {
                float4 a = tex2D(_MainTex, i.uv);
                if (a.a <= 1e-5) return 0;
                return float4(a.rgb / a.a, saturate(a.a / _LoadWeight));
            }
            ENDCG
        }
    }
}
