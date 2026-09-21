using AGBModern;
using GBARecomp;
using LibRecomp;
using static Recompilation.Tests.RecompiledCode;

namespace Recompilation.Tests;

public class CallAndReturnTests
{
    [Fact]
    public void ReturningPastTheCallerSkipsTheRestOfTheCaller()
    {
        var funcs = RecompileProgram(
            [
                0xF000, 0xF802, // 00: bl 0x08
                0x2001,         // 04: movs r0, #1
                0x4728,         // 06: bx r5
                0x4676,         // 08: mov r6, lr
                0xF000, 0xF802, // 0a: bl 0x12
                0x2102,         // 0e: movs r1, #2
                0x4730,         // 10: bx r6
                0x4720,         // 12: bx r4
            ],
            [new Symbol(Address, 8, "Outer"), new Symbol(Address + 8, 0x0A, "Middle"), new Symbol(Address + 0x12, 2, "Inner")]);

        var ctx = Run(Function(funcs, "Outer"), new RecompContext { R4 = Address + 5, R5 = ReturnAddress });

        Assert.Equal((1u, 0u), (ctx.R0, ctx.R1));
    }

    [Fact]
    public void JumpingToAnAddressTheCallerWorkedOutFromPCContinuesThere()
    {
        var funcs = RecompileProgram(
            [
                0xA402,         // 00: add r4, pc, #0x8
                0xF000, 0xF805, // 02: bl 0x10
                0x2102,         // 06: movs r1, #2
                0xE000,         // 08: b 0x0c
                0x46C0,         // 0a: mov r8, r8
                0x2001,         // 0c: movs r0, #1
                0x4728,         // 0e: bx r5
                0x46A7,         // 10: mov pc, r4
            ],
            [new Symbol(Address, 0x10, "Caller"), new Symbol(Address + 0x10, 2, "Jumper")]);

        var ctx = Run(Function(funcs, "Caller"), new RecompContext { R5 = ReturnAddress });

        Assert.Equal((1u, 0u), (ctx.R0, ctx.R1));
    }

    [Fact]
    public void JumpingToAnAddressWorkedOutFromPCBeforeATailCallContinuesThere()
    {
        var funcs = RecompileProgram(
            [
                0xA401, // 00: add r4, pc, #0x4
                0x2800, // 02: cmp r0, #0
                0xD100, // 04: bne 0x08
                0xE001, // 06: b 0x0c
                0x2001, // 08: movs r0, #1
                0x4728, // 0a: bx r5
                0x46A7, // 0c: mov pc, r4
            ],
            [new Symbol(Address, 0x0C, "Caller"), new Symbol(Address + 0x0C, 2, "Jumper")]);

        var ctx = Run(Function(funcs, "Caller"), new RecompContext { R5 = ReturnAddress });

        Assert.Equal(1u, ctx.R0);
    }

    [Fact]
    public void CodeCopiedToRAMWorksOutAddressesFromWhereItRuns()
    {
        const uint Copy = 0x03000400;
        var funcs = RecompileProgram(
            [
                0xA000, // 00: add r0, pc, #0x0
                0x4770, // 02: bx lr
            ],
            [new Symbol(Address, 4, "Copied")],
            ramFuncs: ["Copied"]);
        Memory.ROM.AsSpan((int)(Address & 0x1FFFFFF), 4).CopyTo(Memory.IWRAM.AsSpan((int)(Copy & 0x7FFF)));

        Assert.Equal(Copy + 4, Run(Recomp.LookupFunc(Copy), new RecompContext()).R0);
    }
}
