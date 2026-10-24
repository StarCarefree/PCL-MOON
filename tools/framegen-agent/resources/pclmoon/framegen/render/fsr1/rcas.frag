#version 330 core
#extension GL_ARB_shading_language_packing : enable
#extension GL_ARB_gpu_shader5 : enable
// Official headers: GPUOpen-Effects/FidelityFX-FSR @ a21ffb8f6c13233ba336352bdff293894c706575
#define A_GPU 1
#define A_GLSL 1
#define A_SKIP_EXT 1
#include "ffx_a.h"
#define FSR_RCAS_F 1
AF4 FsrRcasLoadF(ASU2 p);
void FsrRcasInputF(inout AF1 r, inout AF1 g, inout AF1 b);
#include "ffx_fsr1.h"

uniform sampler2D uInput;
uniform vec4 uCon;
uniform vec2 uSize;

layout(location = 0) out vec4 oColor;

AF4 FsrRcasLoadF(ASU2 p) {
    ivec2 size = ivec2(uSize);
    return texelFetch(uInput, clamp(p, ivec2(0), size - ivec2(1)), 0);
}
void FsrRcasInputF(inout AF1 r, inout AF1 g, inout AF1 b) { }

void main() {
    AF1 r, g, b;
    FsrRcasF(r, g, b, uvec2(gl_FragCoord.xy), floatBitsToUint(uCon));
    oColor = vec4(r, g, b, 1.0);
}
