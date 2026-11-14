#version 460
// DYNAMIC: "PIXELFOGTYPE"				"0..1"

layout(location = 0) in vec2 vs_BaseTexCoord;
layout(location = 1) in vec4 vs_WorldPos_ProjPosZ;

layout(std140, binding = 6) uniform source_ps_constants {
    vec4 ps_const[256];
};

layout(std140, binding = 3) uniform source_pixel_sharedUBO {
    bool isAlphaTesting;
    int alphaTestFunc;
    float alphaTestRef;
};

out vec4 fragColor;

#include "common_gl460.fs"

layout(binding = 0) uniform sampler2D TexSampler;

#define g_EyePos_SpecExponent	ps_const[11]
#define g_FogParams				ps_const[12]

void main()
{
    vec4 result = texture(TexSampler, vs_BaseTexCoord);

    if(isAlphaTesting){
        float alpha = result.a;
        switch(alphaTestFunc){
            case 0: discard; break;
            case 1: if(alpha >= alphaTestRef){ discard; } break;
            case 2: if(alpha != alphaTestRef){ discard; } break;
            case 3: if(alpha > alphaTestRef){ discard; } break;
            case 4: if(alpha <= alphaTestRef){ discard; } break;
            case 5: if(alpha == alphaTestRef){ discard; } break;
            case 6: if(alpha < alphaTestRef){ discard; } break;
        }
    }

    float fogFactor = CalcPixelFogFactor(PIXELFOGTYPE, g_FogParams, g_EyePos_SpecExponent.z, vs_WorldPos_ProjPosZ.z, vs_WorldPos_ProjPosZ.w);

    fogFactor = pow(clamp(fogFactor, 0.0, 1.0), 0.4);

    fragColor = FinalOutput(result, fogFactor, PIXELFOGTYPE, TONEMAP_SCALE_NONE);
}
