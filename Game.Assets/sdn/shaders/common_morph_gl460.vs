#ifndef COMMON_MORPH_GL460_VS
#define COMMON_MORPH_GL460_VS

const int VERTEX_SHADER_FLEXSCALE = 3;
#define cFlexScale			vs_const[VERTEX_SHADER_FLEXSCALE]

bool ApplyMorph(vec3 vPosFlex, inout vec3 vPosition)
{
    vec3 vPosDelta = vPosFlex.xyz * cFlexScale.x;
    vPosition.xyz += vPosDelta;
    return true;
}

bool ApplyMorph(vec3 vPosFlex, vec3 vNormalFlex, inout vec3 vPosition, inout vec3 vNormal)
{
    vec3 vPosDelta = vPosFlex.xyz * cFlexScale.x;
    vec3 vNormalDelta = vNormalFlex.xyz * cFlexScale.x;
    vPosition.xyz += vPosDelta;
    vNormal       += vNormalDelta;
    return true;
}

bool ApplyMorph(vec3 vPosFlex, vec3 vNormalFlex, inout vec3 vPosition, inout vec3 vNormal, inout vec3 vTangent)
{
    vec3 vPosDelta = vPosFlex.xyz * cFlexScale.x;
    vec3 vNormalDelta = vNormalFlex.xyz * cFlexScale.x;
    vPosition.xyz += vPosDelta;
    vNormal       += vNormalDelta;
    vTangent.xyz  += vNormalDelta;
    return true;
}

bool ApplyMorph(vec4 vPosFlex, vec3 vNormalFlex, inout vec3 vPosition, inout vec3 vNormal, inout vec3 vTangent, out float flWrinkle)
{
    vec3 vPosDelta = vPosFlex.xyz * cFlexScale.x;
    vec3 vNormalDelta = vNormalFlex.xyz * cFlexScale.x;
    flWrinkle = vPosFlex.w * cFlexScale.y;
    vPosition.xyz += vPosDelta;
    vNormal       += vNormalDelta;
    vTangent.xyz  += vNormalDelta;
    return true;
}

#if defined(MORPHING) && defined(DECAL)

vec4 SampleMorphDelta(sampler2D vt, vec3 vMorphTargetTextureDim, vec4 vMorphSubrect, float flVertexID, float flField)
{
    float flColumn = floor(flVertexID / vMorphSubrect.w);

    vec4 t;
    t.x = vMorphSubrect.x + vMorphTargetTextureDim.z * flColumn + flField + 0.5;
    t.y = vMorphSubrect.y + flVertexID - flColumn * vMorphSubrect.w + 0.5;
    t.xy /= vMorphTargetTextureDim.xy;
    t.z = t.w = 0.0;

    return textureLod(vt, t.xy, t.w);
}

void SampleMorphDelta2(sampler2D vt, vec3 vMorphTargetTextureDim, vec4 vMorphSubrect, float flVertexID, out vec4 delta1, out vec4 delta2)
{
    float flColumn = floor(flVertexID / vMorphSubrect.w);

    vec4 t;
    t.x = vMorphSubrect.x + vMorphTargetTextureDim.z * flColumn + 0.5;
    t.y = vMorphSubrect.y + flVertexID - flColumn * vMorphSubrect.w + 0.5;
    t.xy /= vMorphTargetTextureDim.xy;
    t.z = t.w = 0.0;

    delta1 = textureLod(vt, t.xy, t.w);
    t.x += 1.0 / vMorphTargetTextureDim.x;
    delta2 = textureLod(vt, t.xy, t.w);
}

bool ApplyMorph(sampler2D morphSampler, vec3 vMorphTargetTextureDim, vec4 vMorphSubrect, float flVertexID, vec3 vMorphTexCoord, inout vec3 vPosition)
{
#if MORPHING

#if !DECAL
    vec4 vPosDelta = SampleMorphDelta(morphSampler, vMorphTargetTextureDim, vMorphSubrect, flVertexID, 0.0);
    vPosition += vPosDelta.xyz;
#else
    vec4 t = vec4(vMorphTexCoord.x, vMorphTexCoord.y, 0.0, 0.0);
    vec3 vPosDelta = textureLod(morphSampler, t.xy, t.w).xyz;
    vPosition += vPosDelta.xyz * vMorphTexCoord.z;
#endif

    return true;

#else
    return false;
#endif
}

bool ApplyMorph(sampler2D morphSampler, vec3 vMorphTargetTextureDim, vec4 vMorphSubrect, float flVertexID, vec3 vMorphTexCoord, inout vec3 vPosition, inout vec3 vNormal)
{
#if MORPHING

#if !DECAL
    vec4 vPosDelta, vNormalDelta;
    SampleMorphDelta2(morphSampler, vMorphTargetTextureDim, vMorphSubrect, flVertexID, vPosDelta, vNormalDelta);
    vPosition += vPosDelta.xyz;
    vNormal += vNormalDelta.xyz;
#else
    vec4 t = vec4(vMorphTexCoord.x, vMorphTexCoord.y, 0.0, 0.0);
    vec3 vPosDelta = textureLod(morphSampler, t.xy, t.w).xyz;
    t.x += 1.0 / vMorphTargetTextureDim.x;
    vec3 vNormalDelta = textureLod(morphSampler, t.xy, t.w).xyz;
    vPosition += vPosDelta.xyz * vMorphTexCoord.z;
    vNormal += vNormalDelta.xyz * vMorphTexCoord.z;
#endif

    return true;

#else
    return false;
#endif
}

#endif

#endif
