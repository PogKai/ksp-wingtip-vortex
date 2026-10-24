# Third-party notices

## aeronautic

WingtipVortex's raymarched wing-vapor system is based on and ported from
[aeronautic](https://github.com/Romainlg29/aeronautic) by Romain Le Gall.

The aerodynamic model in `Source/Aeronautic/` and `Source/AeronauticUnity/`, together with the wing-vapor shaders in `Source/Shaders/`, is ported from commit `ca535855f77ccb578c75b6ed7f7c37d8b77ae9c1`. The shared volume pass, depth-aware compositor and scene-fog integration additionally port `packages/core/src/volume-pass.ts` and `packages/wing-vapor/src/vapor-material.ts` from commit `db04669b94ce7aa81ccf1ab87dd229e18f78456f`.

Each ported file identifies its upstream source. These portions are used under the following MIT License:

```
MIT License

Copyright (c) 2026 Romain Le Gall

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
```
