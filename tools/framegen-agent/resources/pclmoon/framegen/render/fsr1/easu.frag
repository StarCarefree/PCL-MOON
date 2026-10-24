#version 330 core
// GL 3.3 core plus the two extensions the official full-precision port needs.
// AMD headers are unmodified; only these local wrappers add the extensions and uint literals.
#extension GL_ARB_shading_language_packing : enable
#extension GL_ARB_gpu_shader5 : enable
// Official headers: GPUOpen-Effects/FidelityFX-FSR @ a21ffb8f6c13233ba336352bdff293894c706575
// textureGather is not required by GL 3.3, so the gather4 callbacks are texelFetch.
#define A_GPU 1
#define A_GLSL 1
#define A_SKIP_EXT 1
#include "ffx_a.h"
#define FSR_EASU_F 1
AF4 FsrEasuRF(AF2 p);
AF4 FsrEasuGF(AF2 p);
AF4 FsrEasuBF(AF2 p);
#include "ffx_fsr1.h"

uniform sampler2D uInput;
uniform vec4 uCon0;
uniform vec4 uCon1;
uniform vec4 uCon2;
uniform vec4 uCon3;
uniform vec2 uInputSize;

layout(location = 0) out vec4 oColor;

AF4 FsrGather(AF2 p, int channel) {
    // textureGather returns components in (x-, y-), (x+, y-), (x+, y+), (x-, y+) order.
    ivec2 base = ivec2(floor(p * uInputSize - vec2(0.5)));
    ivec2 size = ivec2(uInputSize);
    return AF4(
        texelFetch(uInput, clamp(base + ivec2(0, 1), ivec2(0), size - ivec2(1)), 0)[channel],
        texelFetch(uInput, clamp(base + ivec2(1, 1), ivec2(0), size - ivec2(1)), 0)[channel],
        texelFetch(uInput, clamp(base + ivec2(1, 0), ivec2(0), size - ivec2(1)), 0)[channel],
        texelFetch(uInput, clamp(base + ivec2(0, 0), ivec2(0), size - ivec2(1)), 0)[channel]);
}
AF4 FsrEasuRF(AF2 p) { return FsrGather(p, 0); }
AF4 FsrEasuGF(AF2 p) { return FsrGather(p, 1); }
AF4 FsrEasuBF(AF2 p) { return FsrGather(p, 2); }

void main() {
    AF3 pix;
    FsrEasuF(pix, uvec2(gl_FragCoord.xy), floatBitsToUint(uCon0), floatBitsToUint(uCon1),
        floatBitsToUint(uCon2), floatBitsToUint(uCon3));
    oColor = vec4(pix, 1.0);
}
