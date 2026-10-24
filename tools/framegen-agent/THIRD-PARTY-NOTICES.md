# Third-party notices

This agent includes ASM 9.9, relocated into `pclmoon.framegen.internal.asm` to avoid interfering with mod loaders. Source: https://asm.ow2.io/ ; license: https://asm.ow2.io/license.html .

ASM: a very small and fast Java bytecode manipulation framework
Copyright (c) 2000-2011 INRIA, France Telecom
All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.
3. Neither the name of the copyright holders nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

LWJGL is a compile-time-only dependency, not included in the agent. Runtime uses the LWJGL and native libraries supplied by Minecraft. https://www.lwjgl.org/license

Screamix1/FrameGen was inspected as a conceptual reference. It uses depth reprojection (LGPL-3.0-or-later), unlike this independently implemented image-space block-matching pipeline. No FrameGen source or shaders are included.

## Frame pacing references (MIT)

The portable FPS pacing, hardware HUD and profile concepts were researched from
[endjynn/frame-pacer](https://github.com/endjynn/frame-pacer), Copyright (c) 2026 Endjynn.
Linux injection, cgroups and per-thread CPU quotas are not included.

The coarse-park/final-spin algorithm was also researched from **FramePacer 1.2**,
Copyright (c) 2026 supcherski: https://modrinth.com/mod/framepacer
and https://www.curseforge.com/minecraft/mc-mods/framepacer .
The user-supplied multiloader JAR has SHA256
`61e2ecd0e05761f5bf6ee293ce58e827ee5e92ad5290a99ef4947374b154aebf`.
It was inspected as bytecode, not executed or redistributed. The agent's Java
pacing implementation is independent and adds configurable spin margins,
interrupt handling and a unified presentation limiter.

The MIT permission and disclaimer below apply to both references, with their
respective copyright notices above:

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
