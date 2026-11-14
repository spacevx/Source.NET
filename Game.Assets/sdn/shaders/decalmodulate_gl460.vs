#version 460
//  STATIC: "DECAL"						"0..1"

//	DYNAMIC: "SKINNING"					"0..1"
//  DYNAMIC: "MORPHING"					"0..1"

layout(location = 0) in vec3 v_Position;
layout(location = 1) in vec3 v_Normal;
layout(location = 7) in ivec4 v_BoneIndex;
layout(location = 8) in vec2 v_BoneWeights;
layout(location = 10) in vec4 v_TexCoord0;
layout(location = 12) in vec4 v_TexCoord2;
layout(location = 14) in vec4 v_FlexPosition;
layout(location = 15) in vec3 v_FlexNormal;

layout(std140, binding = 0) uniform source_matrices {
    mat4 viewMatrix;
    mat4 projectionMatrix;
    mat4 modelMatrix;
};

layout(std140, binding = 5) uniform source_vs_constants {
    vec4 vs_const[256];
};

const int SHADER_SPECIFIC_CONST_0 = 48;

#include "common_gl460.vs"
#include "common_morph_gl460.vs"
#include "common_skinning_gl460.vs"

const bool g_bSkinning		= SKINNING != 0;
#if MORPHING && DECAL
const bool g_bDecalOffset	= true;
#else
const bool g_bDecalOffset	= false;
#endif

#define cBaseTexCoordTransform0		vs_const[SHADER_SPECIFIC_CONST_0 + 0]
#define cBaseTexCoordTransform1		vs_const[SHADER_SPECIFIC_CONST_0 + 1]

layout(location = 0) out vec2 vs_BaseTexCoord;
layout(location = 1) out vec4 vs_WorldPos_ProjPosZ;

#include "common_clipplanes_gl460.vs"

void main()
{
    vec4 vPosition = vec4(v_Position, 1.0);
    vec3 vNormal = vec3(0.0);

    if (g_bDecalOffset)
        vNormal = v_Normal;

#if !MORPHING
    ApplyMorph(v_FlexPosition.xyz, v_FlexNormal, vPosition.xyz, vNormal);
#else
    // todo: ApplyMorph from the morph target texture (vertex ID, v_TexCoord2), it needs hardware morphing ported first
#endif

    vec3 worldNormal, worldPos;
    SkinPositionAndNormal(
        g_bSkinning,
        vPosition, vNormal,
        v_BoneIndex, v_BoneWeights,
        worldPos, worldNormal);

#if MORPHING && DECAL
    worldNormal = normalize(worldNormal);
    worldPos += worldNormal * 0.05 * v_TexCoord2.z;
#endif

    vec4 vProjPos = projectionMatrix * viewMatrix * vec4(worldPos, 1.0);
    gl_Position = vProjPos;
    WriteUserClipDistances(gl_Position);

    vs_WorldPos_ProjPosZ = vec4(worldPos.xyz, vProjPos.z);

    vs_BaseTexCoord.x = dot(v_TexCoord0, cBaseTexCoordTransform0);
    vs_BaseTexCoord.y = dot(v_TexCoord0, cBaseTexCoordTransform1);
}
