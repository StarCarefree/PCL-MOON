#version 330 core
// GL_LINEAR scaled copy. Used as the safe GPU fallback and as the smoke reference, not as FSR.
uniform sampler2D uInput;
uniform vec2 uInvOutput;
layout(location = 0) out vec4 oColor;
void main() {
    oColor = texture(uInput, gl_FragCoord.xy * uInvOutput);
}
