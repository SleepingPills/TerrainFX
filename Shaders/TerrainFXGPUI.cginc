// Makes a shader drawable by Tarkov's GPU Instancer (default / D3D11 matrix handling).
//
// Usage, inside every CGPROGRAM block that should be instanced:
//     #include "UnityCG.cginc"
//     #include "TerrainFXGPUI.cginc"
//     #pragma multi_compile_instancing
//     #pragma instancing_options procedural:setupGPUI
//     #pragma target 4.5
//
// The game draws details with Graphics.DrawMeshInstancedIndirect and binds these per renderer:
//     gpuiInstanceData          one object-to-world matrix per instance in the active cells
//     gpuiTransformationMatrix  indices into gpuiInstanceData of the instances that survived culling for this LOD
//     gpuiTransformOffset       local transform of the renderer inside the prefab (identity for a single mesh)
//
// Do not give the shader _ZWrite or _ZTest properties: the game sets _ZWrite=0 and _ZTest=Equal on every instanced
// material because its own detail shaders rely on a depth pre-pass ("DepthPrePass" / "MotionVectors" passes drawn
// from a command buffer). A shader without those passes has to write depth itself.

#ifndef TERRAINFX_GPUI_INCLUDED
#define TERRAINFX_GPUI_INCLUDED

#ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
    StructuredBuffer<uint> gpuiTransformationMatrix;
    StructuredBuffer<float4x4> gpuiInstanceData;
    float4x4 gpuiTransformOffset;
#endif

void setupGPUI()
{
#ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
    uint instanceIndex = gpuiTransformationMatrix[unity_InstanceID];

    unity_ObjectToWorld = mul(gpuiInstanceData[instanceIndex], gpuiTransformOffset);
    unity_ObjectToWorld._41_42_43_44 = float4(0.0f, 0.0f, 0.0f, 1.0f);

    // Affine inverse, the matrix carries rotation and non-uniform scale
    float3x3 w2oRotation;
    w2oRotation[0] = unity_ObjectToWorld[1].yzx * unity_ObjectToWorld[2].zxy - unity_ObjectToWorld[1].zxy * unity_ObjectToWorld[2].yzx;
    w2oRotation[1] = unity_ObjectToWorld[0].zxy * unity_ObjectToWorld[2].yzx - unity_ObjectToWorld[0].yzx * unity_ObjectToWorld[2].zxy;
    w2oRotation[2] = unity_ObjectToWorld[0].yzx * unity_ObjectToWorld[1].zxy - unity_ObjectToWorld[0].zxy * unity_ObjectToWorld[1].yzx;

    float det = dot(unity_ObjectToWorld[0].xyz, w2oRotation[0]);
    w2oRotation = transpose(w2oRotation);
    w2oRotation *= rcp(det);

    float3 w2oPosition = mul(w2oRotation, -unity_ObjectToWorld._14_24_34);

    unity_WorldToObject._11_21_31_41 = float4(w2oRotation._11_21_31, 0.0f);
    unity_WorldToObject._12_22_32_42 = float4(w2oRotation._12_22_32, 0.0f);
    unity_WorldToObject._13_23_33_43 = float4(w2oRotation._13_23_33, 0.0f);
    unity_WorldToObject._14_24_34_44 = float4(w2oPosition, 1.0f);
#endif
}

#endif
