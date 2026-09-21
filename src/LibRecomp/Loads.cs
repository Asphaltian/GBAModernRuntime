using System.Numerics;
using System.Runtime.CompilerServices;
using AGBModern;

namespace LibRecomp;

/// <summary>These are called by recompiled code, so you won't need them yourself.</summary>
public static class Loads
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Word(uint address, uint openBus = 0) => BitOperations.RotateRight(Memory.Read32(address, openBus), (int)(address & 3) * 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Halfword(uint address, uint openBus = 0) => BitOperations.RotateRight(Memory.Read16(address, openBus), (int)(address & 1) * 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint SignedHalfword(uint address, uint openBus = 0) => (address & 1) == 0 ? (uint)(short)Memory.Read16(address, openBus) : (uint)(sbyte)Memory.Read8(address, openBus);
}
