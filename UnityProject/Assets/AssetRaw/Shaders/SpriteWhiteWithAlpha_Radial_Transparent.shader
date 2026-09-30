Shader "Custom/SpriteWhiteWithAlpha_Radial_Transparent"
{
    Properties
    {
        _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint Color", Color) = (1,1,1,1)
        _Restore ("Restore Strength", Range(0,1)) = 1
        _Radius ("Restore Radius (UV)", Range(0,1)) = 0.5
        _Feather ("Feather (smooth edge)", Range(0,0.5)) = 0.05
        _Center ("Center (UV)", Vector) = (0.5, 0.5, 0, 0)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Cull Off
        Lighting Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata_t { float4 vertex : POSITION; float2 texcoord : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 vertex : SV_POSITION; float2 texcoord : TEXCOORD0; fixed4 color : COLOR; };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            float _Restore;
            float _Radius;
            float _Feather;
            float4 _Center; // 使用 x,y

            v2f vert(appdata_t v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.texcoord = TRANSFORM_TEX(v.texcoord, _MainTex);
                // 顶点颜色与外面设置的 _Color 相乘（同 Sprite 默认行为）
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 tex = tex2D(_MainTex, i.texcoord);

                // UV 中心
                float2 center = _Center.xy;
                // 计算 UV 距离
                float dist = distance(i.texcoord, center);

                // 计算羽化边界，确保数值稳定
                float feather = max(_Feather, 1e-5);
                float edge0 = _Radius - feather * 0.5;
                float edge1 = _Radius + feather * 0.5;

                // smoothstep 产生从 0->1 的过渡，1 表示在外面
                float outside = smoothstep(edge0, edge1, dist);
                // mask = 1: 在半径内（应恢复）；0: 在半径外（保持透明）
                float mask = 1.0 - outside;

                // white 与 原图都乘以顶点色（包含 _Color），保持和 Sprite 默认一致的 tint 行为
                fixed3 whiteCol = fixed3(1.0, 1.0, 1.0) * i.color.rgb;
                fixed3 originalCol = tex.rgb * i.color.rgb;

                // mask * _Restore 控制恢复到原图的强度（0..1）
                float blendFactor = saturate(mask * _Restore);

                fixed3 finalRgb = lerp(whiteCol, originalCol, blendFactor);

                // 如果在恢复范围内，返回实际透明度，否则返回完全透明
                float alpha = mask > 0.0 ? tex.a * i.color.a : 0.0;

                return fixed4(finalRgb, alpha);
            }
            ENDCG
        }
    }
}
