using System.Runtime.CompilerServices;

namespace LibRecomp;

/// <summary>These are called by recompiled code, so you won't need them yourself.</summary>
public static class Shifter
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint LSL(uint value, uint amount) => amount < 32 ? value << (int)amount : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint LSR(uint value, uint amount) => amount < 32 ? value >> (int)amount : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ASR(uint value, uint amount) => (uint)((int)value >> (int)Math.Min(amount, 31));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ROR(uint value, uint amount) => uint.RotateRight(value, (int)(amount & 31));

    public static bool LSLCarry(uint value, uint amount, bool carryIn) => amount switch
    {
        0 => carryIn,
        <= 32 => ((value >> (int)(32 - amount)) & 1) != 0,
        _ => false,
    };

    public static bool LSRCarry(uint value, uint amount, bool carryIn) => amount switch
    {
        0 => carryIn,
        <= 32 => ((value >> (int)(amount - 1)) & 1) != 0,
        _ => false,
    };

    public static bool ASRCarry(uint value, uint amount, bool carryIn) => amount switch
    {
        0 => carryIn,
        < 32 => ((value >> (int)(amount - 1)) & 1) != 0,
        _ => (int)value < 0,
    };

    public static bool RORCarry(uint value, uint amount, bool carryIn)
    {
        return amount == 0 ? carryIn : ((value >> (int)((amount - 1) & 31)) & 1) != 0;
    }
}
