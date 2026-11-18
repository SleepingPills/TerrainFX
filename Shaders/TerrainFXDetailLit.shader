// Opaque PBR shader for custom mesh details. Build it into the "terrainfx" asset bundle; the plugin registers it
// with the game's GPU Instancer by name (TerrainFX/DetailLit).
Shader "TerrainFX/DetailLit"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
        _MainTex ("Albedo (RGB)", 2D) = "white" {}
        [NoScaleOffset] [Normal] _BumpMap ("Normal Map", 2D) = "bump" {}
        _Glossiness ("Smoothness", Range(0, 1)) = 0.2
        _Metallic ("Metallic", Range(0, 1)) = 0.0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        LOD 200

        CGPROGRAM
        #include "UnityCG.cginc"
        #include "TerrainFXGPUI.cginc"
        #pragma multi_compile_instancing
        #pragma instancing_options procedural:setupGPUI
        // addshadow generates a shadow caster that goes through setupGPUI as well
        #pragma surface surf Standard addshadow fullforwardshadows
        #pragma target 4.5

        sampler2D _MainTex;
        sampler2D _BumpMap;
        fixed4 _Color;
        half _Glossiness;
        half _Metallic;

        struct Input
        {
            float2 uv_MainTex;
        };

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 albedo = tex2D(_MainTex, IN.uv_MainTex) * _Color;

            o.Albedo = albedo.rgb;
            o.Normal = UnpackNormal(tex2D(_BumpMap, IN.uv_MainTex));
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Alpha = albedo.a;
        }
        ENDCG
    }

    FallBack Off
}
