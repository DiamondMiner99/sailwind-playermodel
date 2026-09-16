// The player's own body seen in first person while seated: the game's character look, see-through above the chest and
// fading in toward the hips, so looking down shows your legs and lap without a head or shoulders in the way.
//
// Colors are built the way the game's SyntyStudios/CustomCharacter shader builds them. Its texture is a palette sheet,
// and each mask channel is white except over the swatch cells one color property paints; a mesh part that samples a
// cell takes that color. Channel to property was read off the game's own mask and base textures, whose cells carry the
// default colors: Mask_01 R Primary, G body art emblems, B Secondary; Mask_02 R/G/B metal primary, secondary, dark;
// Mask_03 R/G/B skin, scar, stubble; Mask_04 R/G/B leather primary, leather secondary, hair; Mask_05 R eyes.
//
// Built with Unity 2019.1.10f1, the game's own version, into the asset bundle SailwindPlayerModel embeds. Property names
// match the game's shader so a body's material copies straight across.
Shader "SailwindPlayerModel/SeatedBodyFade"
{
    Properties
    {
        _Color_Primary ("Color_Primary", Color) = (0.49, 0.70, 0.83, 0)
        _Color_Secondary ("Color_Secondary", Color) = (0.17, 0.17, 0.17, 0)
        _Color_Leather_Primary ("Color_Leather_Primary", Color) = (0.31, 0.21, 0.16, 0)
        _Color_Metal_Primary ("Color_Metal_Primary", Color) = (0.63, 0.62, 0.56, 0)
        _Color_Leather_Secondary ("Color_Leather_Secondary", Color) = (0.37, 0.33, 0.28, 0)
        _Color_Metal_Dark ("Color_Metal_Dark", Color) = (0.17, 0.22, 0.27, 0)
        _Color_Metal_Secondary ("Color_Metal_Secondary", Color) = (0.48, 0.52, 0.55, 0)
        _Color_Hair ("Color_Hair", Color) = (0.89, 0.78, 0.55, 0)
        _Color_Skin ("Color_Skin", Color) = (1, 0.8, 0.68, 0)
        _Color_Stubble ("Color_Stubble", Color) = (0.8, 0.7, 0.63, 0)
        _Color_Scar ("Color_Scar", Color) = (0.93, 0.69, 0.59, 0)
        _Color_BodyArt ("Color_BodyArt", Color) = (0.31, 0.72, 0.69, 0)
        _Color_Eyes ("Color_Eyes", Color) = (0, 0, 0, 1)
        _Texture ("Texture", 2D) = "white" {}
        _Mask_01 ("Mask_01", 2D) = "white" {}
        _Mask_02 ("Mask_02", 2D) = "white" {}
        _Mask_03 ("Mask_03", 2D) = "white" {}
        _Mask_04 ("Mask_04", 2D) = "white" {}
        _Mask_05 ("Mask_05", 2D) = "white" {}
        _Metallic ("Metallic", Range(0, 1)) = 0
        _Smoothness ("Smoothness", Range(0, 1)) = 0.2
        _Emission ("Emission", Range(0, 1)) = 0
        _BodyArt_Amount ("BodyArt_Amount", Range(0, 1)) = 0
        // World heights: fully see-through at and above _FadeTop, fully solid at and below _FadeBottom.
        _FadeTop ("Fade top (world Y)", Float) = 10000
        _FadeBottom ("Fade bottom (world Y)", Float) = 9999
    }

    CGINCLUDE
    float _FadeTop, _FadeBottom;
    float FadeAlpha(float worldY)
    {
        return saturate((_FadeTop - worldY) / max(_FadeTop - _FadeBottom, 0.001));
    }
    ENDCG

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        // Depth first, from the solid part only, so the see-through part does not show the body's own far side.
        Pass
        {
            ZWrite On
            ColorMask 0
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct v2f
            {
                float4 pos : SV_POSITION;
                float worldY : TEXCOORD0;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldY = mul(unity_ObjectToWorld, v.vertex).y;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                clip(FadeAlpha(i.worldY) - 0.5);
                return 0;
            }
            ENDCG
        }

        ZWrite Off
        CGPROGRAM
        #pragma surface surf Standard alpha:fade
        #pragma target 3.0

        sampler2D _Texture, _Mask_01, _Mask_02, _Mask_03, _Mask_04, _Mask_05;
        fixed4 _Color_Primary, _Color_Secondary, _Color_Leather_Primary, _Color_Metal_Primary, _Color_Leather_Secondary,
            _Color_Metal_Dark, _Color_Metal_Secondary, _Color_Hair, _Color_Skin, _Color_Stubble, _Color_Scar,
            _Color_BodyArt, _Color_Eyes;
        half _Metallic, _Smoothness, _Emission, _BodyArt_Amount;

        struct Input
        {
            float2 uv_Texture;
            float3 worldPos;
        };

        void surf(Input i, inout SurfaceOutputStandard o)
        {
            float2 uv = i.uv_Texture;
            fixed4 m1 = tex2D(_Mask_01, uv);
            fixed4 m2 = tex2D(_Mask_02, uv);
            fixed4 m3 = tex2D(_Mask_03, uv);
            fixed4 m4 = tex2D(_Mask_04, uv);
            fixed4 m5 = tex2D(_Mask_05, uv);

            fixed3 c = tex2D(_Texture, uv).rgb;
            c = lerp(_Color_Primary.rgb, c, m1.r);
            c = lerp(_Color_Secondary.rgb, c, m1.b);
            c = lerp(_Color_Metal_Primary.rgb, c, m2.r);
            c = lerp(_Color_Metal_Secondary.rgb, c, m2.g);
            c = lerp(_Color_Metal_Dark.rgb, c, m2.b);
            c = lerp(_Color_Skin.rgb, c, m3.r);
            c = lerp(_Color_Scar.rgb, c, m3.g);
            c = lerp(_Color_Stubble.rgb, c, m3.b);
            c = lerp(_Color_Leather_Primary.rgb, c, m4.r);
            c = lerp(_Color_Leather_Secondary.rgb, c, m4.g);
            c = lerp(_Color_Hair.rgb, c, m4.b);
            c = lerp(_Color_Eyes.rgb, c, m5.r);
            c = lerp(c, _Color_BodyArt.rgb, (1 - m1.g) * _BodyArt_Amount);

            o.Albedo = c;
            o.Metallic = _Metallic;
            o.Smoothness = _Smoothness;
            o.Emission = c * _Emission;
            o.Alpha = FadeAlpha(i.worldPos.y);
        }
        ENDCG
    }
    FallBack Off
}
