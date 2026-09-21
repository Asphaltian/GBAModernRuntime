# GBA Modern Runtime

GBA Modern Runtime is what Game Boy Advance games recompiled with [GBARecomp](https://github.com/Asphaltian/GBARecomp) run on. It takes the place of the GBA's hardware and BIOS for the recompiled code, and handles the things every port needs, like saves, checking the player's ROM and loading mods.

## Building

You will need the .NET 10 SDK and [Slang](https://github.com/shader-slang/slang/releases), with `slangc` available on your PATH.

```
git clone --recurse-submodules https://github.com/Asphaltian/GBAModernRuntime
cd GBAModernRuntime
dotnet build
```
