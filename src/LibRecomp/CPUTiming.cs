using System.Runtime.CompilerServices;
using AGBModern;

namespace LibRecomp;

/// <summary>These are called by recompiled code, so you won't need them yourself.</summary>
public static class CPUTiming
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ROMFetch(int sequential, int nonSequential, int prefetchedOrSequential, int prefetchedOrNonSequential, int sequentialOrNonSequential)
    {
        int s = Memory.ROMSequential;
        int n = Memory.ROMNonSequential;
        return Memory.IsPrefetchEnabled
            ? ((sequential + sequentialOrNonSequential) * s) + (nonSequential * n) + prefetchedOrSequential + prefetchedOrNonSequential
            : ((sequential + prefetchedOrSequential) * s) + ((nonSequential + prefetchedOrNonSequential + sequentialOrNonSequential) * n);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int EWRAMFetch(int halfwords) => halfwords * Memory.EWRAMHalfword;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CopiedToRAMFetch(int halfwords, int accesses) => Recomp.IsCopyInEWRAM ? EWRAMFetch(halfwords) : accesses;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int JumpCycles(uint target, bool isThumb) => Memory.JumpCycles(target, isThumb);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Multiply(uint multiplier) => UnsignedMultiply(multiplier ^ (uint)((int)multiplier >> 31));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int UnsignedMultiply(uint multiplier) => multiplier switch
    {
        < 0x100 => 1,
        < 0x10000 => 2,
        < 0x1000000 => 3,
        _ => 4,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SequentialSaving(uint address, int count)
    {
        uint last = address + (uint)((count - 1) * 4);
        int blockStarts = (int)((last >> 17) - (address >> 17));
        return (count - 1 - blockStarts) * Memory.SequentialSavingAt(address);
    }
}
