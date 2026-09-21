using LibRecomp;
using static Recompilation.Tests.RecompiledCode;

namespace Recompilation.Tests;

public class ThumbTests
{
    private static void AssertFlags(RecompContext ctx, bool n, bool z, bool c, bool v)
    {
        Assert.Equal((n, z, c, v), (ctx.N, ctx.Z, ctx.C, ctx.V));
    }

    [Theory]
    [InlineData(1, 2, 3, false, false, false, false)]
    [InlineData(0x7FFFFFFF, 1, 0x80000000, true, false, false, true)]
    [InlineData(0xFFFFFFFF, 1, 0, false, true, true, false)]
    [InlineData(0x80000000, 0x80000000, 0, false, true, true, true)]
    public void Adds(uint a, uint b, uint result, bool n, bool z, bool c, bool v)
    {
        var function = Thumb((0x1840, "adds r0, r0, r1"), (0x4770, "bx lr"));
        var ctx = Run(function, new RecompContext { R0 = a, R1 = b });

        Assert.Equal(result, ctx.R0);
        AssertFlags(ctx, n, z, c, v);
    }

    [Theory]
    [InlineData(5, 5, 0, false, true, true, false)]
    [InlineData(0, 1, 0xFFFFFFFF, true, false, false, false)]
    [InlineData(0x80000000, 1, 0x7FFFFFFF, false, false, true, true)]
    [InlineData(0x7FFFFFFF, 0xFFFFFFFF, 0x80000000, true, false, false, true)]
    public void Subs(uint a, uint b, uint result, bool n, bool z, bool c, bool v)
    {
        var function = Thumb((0x1A40, "subs r0, r0, r1"), (0x4770, "bx lr"));
        var ctx = Run(function, new RecompContext { R0 = a, R1 = b });

        Assert.Equal(result, ctx.R0);
        AssertFlags(ctx, n, z, c, v);
    }

    [Fact]
    public void CmpSetsFlagsAndLeavesTheRegisterAlone()
    {
        var function = Thumb((0x4288, "cmp r0, r1"), (0x4770, "bx lr"));
        var ctx = Run(function, new RecompContext { R0 = 0, R1 = 1 });

        Assert.Equal(0u, ctx.R0);
        AssertFlags(ctx, n: true, z: false, c: false, v: false);
    }

    [Theory]
    [InlineData(0xFFFFFFFF, 0, true, 0, false, true, true, false)]
    [InlineData(1, 1, false, 2, false, false, false, false)]
    [InlineData(0x7FFFFFFF, 0, true, 0x80000000, true, false, false, true)]
    public void Adcs(uint a, uint b, bool carryIn, uint result, bool n, bool z, bool c, bool v)
    {
        var function = Thumb((0x4148, "adcs r0, r0, r1"), (0x4770, "bx lr"));
        var ctx = Run(function, new RecompContext { R0 = a, R1 = b, C = carryIn });

        Assert.Equal(result, ctx.R0);
        AssertFlags(ctx, n, z, c, v);
    }

    [Theory]
    [InlineData(5, 5, true, 0, false, true, true, false)]
    [InlineData(5, 5, false, 0xFFFFFFFF, true, false, false, false)]
    [InlineData(0, 0xFFFFFFFF, false, 0, false, true, false, false)]
    public void Sbcs(uint a, uint b, bool carryIn, uint result, bool n, bool z, bool c, bool v)
    {
        var function = Thumb((0x4188, "sbcs r0, r0, r1"), (0x4770, "bx lr"));
        var ctx = Run(function, new RecompContext { R0 = a, R1 = b, C = carryIn });

        Assert.Equal(result, ctx.R0);
        AssertFlags(ctx, n, z, c, v);
    }

    [Theory]
    [InlineData(1, 0xFFFFFFFF, true, false, false, false)]
    [InlineData(0, 0, false, true, true, false)]
    [InlineData(0x80000000, 0x80000000, true, false, false, true)]
    public void Negs(uint value, uint result, bool n, bool z, bool c, bool v)
    {
        var function = Thumb((0x4248, "rsbs r0, r1, #0x0"), (0x4770, "bx lr"));
        var ctx = Run(function, new RecompContext { R1 = value });

        Assert.Equal(result, ctx.R0);
        AssertFlags(ctx, n, z, c, v);
    }

    [Fact]
    public void MulsSetsNAndZAndLeavesCAndV()
    {
        var function = Thumb((0x4348, "muls r0, r1, r0"), (0x4770, "bx lr"));

        var ctx = Run(function, new RecompContext { R0 = 3, R1 = 4, C = true, V = true });
        Assert.Equal(12u, ctx.R0);
        AssertFlags(ctx, n: false, z: false, c: true, v: true);

        ctx = Run(function, new RecompContext { R0 = 0x10000, R1 = 0x10000 });
        Assert.Equal(0u, ctx.R0);
        AssertFlags(ctx, n: false, z: true, c: false, v: false);
    }

    [Fact]
    public void LogicalOperationsLeaveCAndV()
    {
        var function = Thumb((0x4008, "ands r0, r0, r1"), (0x4770, "bx lr"));
        var ctx = Run(function, new RecompContext { R0 = 0xF0F0F0F0, R1 = 0x80000000, C = true, V = true });
        Assert.Equal(0x80000000u, ctx.R0);
        AssertFlags(ctx, n: true, z: false, c: true, v: true);

        function = Thumb((0x4388, "bics r0, r0, r1"), (0x4770, "bx lr"));
        ctx = Run(function, new RecompContext { R0 = 0xFF, R1 = 0xFF });
        Assert.Equal(0u, ctx.R0);
        AssertFlags(ctx, n: false, z: true, c: false, v: false);

        function = Thumb((0x43C8, "mvns r0, r1"), (0x4770, "bx lr"));
        ctx = Run(function, new RecompContext { R1 = 0 });
        Assert.Equal(0xFFFFFFFFu, ctx.R0);

        function = Thumb((0x4048, "eors r0, r0, r1"), (0x4770, "bx lr"));
        Assert.Equal(0x0Fu, Run(function, new RecompContext { R0 = 0xFF, R1 = 0xF0 }).R0);

        function = Thumb((0x4308, "orrs r0, r0, r1"), (0x4770, "bx lr"));
        Assert.Equal(0xFFu, Run(function, new RecompContext { R0 = 0x0F, R1 = 0xF0 }).R0);
    }

    [Theory]
    [InlineData(0x0048, "movs r0, r1, lsl #1", 0x80000001, false, 0x00000002, true)]
    [InlineData(0x0008, "movs r0, r1", 0x80000001, true, 0x80000001, true)]
    [InlineData(0x0848, "movs r0, r1, lsr #1", 0x00000003, false, 0x00000001, true)]
    [InlineData(0x0808, "movs r0, r1, lsr #32", 0x80000000, false, 0x00000000, true)]
    [InlineData(0x1048, "movs r0, r1, asr #1", 0x80000002, true, 0xC0000001, false)]
    [InlineData(0x1008, "movs r0, r1, asr #32", 0x80000000, false, 0xFFFFFFFF, true)]
    public void ShiftsByAConstant(ushort encoding, string disassembly, uint value, bool carryIn, uint result, bool carryOut)
    {
        var function = Thumb((encoding, disassembly), (0x4770, "bx lr"));
        var ctx = Run(function, new RecompContext { R1 = value, C = carryIn });

        Assert.Equal(result, ctx.R0);
        Assert.Equal(carryOut, ctx.C);
        Assert.Equal((int)result < 0, ctx.N);
        Assert.Equal(result == 0, ctx.Z);
    }

    [Theory]
    [InlineData(0x4088, "movs r0, r0, lsl r1", 1, 0, true, 1, true)]
    [InlineData(0x4088, "movs r0, r0, lsl r1", 1, 32, false, 0, true)]
    [InlineData(0x4088, "movs r0, r0, lsl r1", 1, 33, true, 0, false)]
    [InlineData(0x40C8, "movs r0, r0, lsr r1", 0x80000000, 32, false, 0, true)]
    [InlineData(0x40C8, "movs r0, r0, lsr r1", 0x80000000, 0x100, true, 0x80000000, true)]
    [InlineData(0x4108, "movs r0, r0, asr r1", 0x80000000, 40, false, 0xFFFFFFFF, true)]
    [InlineData(0x41C8, "movs r0, r0, ror r1", 0x00000001, 1, false, 0x80000000, true)]
    [InlineData(0x41C8, "movs r0, r0, ror r1", 0x80000000, 32, false, 0x80000000, true)]
    public void ShiftsByARegister(
        ushort encoding, string disassembly, uint value, uint amount, bool carryIn, uint result, bool carryOut)
    {
        var function = Thumb((encoding, disassembly), (0x4770, "bx lr"));
        var ctx = Run(function, new RecompContext { R0 = value, R1 = amount, C = carryIn });

        Assert.Equal(result, ctx.R0);
        Assert.Equal(carryOut, ctx.C);
    }

    [Theory]
    [InlineData(1, 1, "eq cs pl vc ls ge le")]
    [InlineData(1, 2, "ne cc mi vc ls lt le")]
    [InlineData(2, 1, "ne cs pl vc hi ge gt")]
    [InlineData(0x80000000, 1, "ne cs pl vs hi lt le")]
    [InlineData(1, 0x80000000, "ne cc mi vs ls ge gt")]
    public void ConditionalBranches(uint a, uint b, string conditionsThatHold)
    {
        string[] conditions = ["eq", "ne", "cs", "cc", "mi", "pl", "vs", "vc", "hi", "ls", "ge", "lt", "gt", "le"];
        for (int condition = 0; condition < conditions.Length; condition++)
        {
            var function = Thumb(
                (0x4288, "cmp r0, r1"),
                ((ushort)(0xD001 | (condition << 8)), $"b{conditions[condition]} 0x08000008"),
                (0x2200, "movs r2, #0x0"),
                (0x4770, "bx lr"),
                (0x2201, "movs r2, #0x1"),
                (0x4770, "bx lr"));

            var ctx = Run(function, new RecompContext { R0 = a, R1 = b });
            bool holds = conditionsThatHold.Split(' ').Contains(conditions[condition]);
            Assert.True(holds == (ctx.R2 == 1), $"{conditions[condition]} after cmp 0x{a:X}, 0x{b:X}");
        }
    }

    [Fact]
    public void LoadsAndStores()
    {
        const uint Buffer = 0x02001000;

        var function = Thumb(
            (0x6048, "str r0, [r1, #0x4]"),
            (0x7088, "strb r0, [r1, #0x2]"),
            (0x8008, "strh r0, [r1]"),
            (0x684A, "ldr r2, [r1, #0x4]"),
            (0x788B, "ldrb r3, [r1, #0x2]"),
            (0x880C, "ldrh r4, [r1]"),
            (0x4770, "bx lr"));

        var ctx = Run(function, new RecompContext { R0 = 0x8899AABB, R1 = Buffer });

        Assert.Equal(0x8899AABBu, ctx.R2);
        Assert.Equal(0xBBu, ctx.R3);
        Assert.Equal(0xAABBu, ctx.R4);
        Assert.Equal(0x00BBAABBu, AGBModern.Memory.Read32(Buffer));
    }

    [Fact]
    public void SignedLoadsExtendTheSign()
    {
        const uint Buffer = 0x02001100;
        AGBModern.Memory.Write32(Buffer, 0x0000FF80);

        var function = Thumb(
            (0x5688, "ldrsb r0, [r1, r2]"),
            (0x5E8B, "ldrsh r3, [r1, r2]"),
            (0x4770, "bx lr"));

        var ctx = Run(function, new RecompContext { R1 = Buffer - 8, R2 = 8 });

        Assert.Equal(0xFFFFFF80u, ctx.R0);
        Assert.Equal(0xFFFFFF80u, ctx.R3);
    }

    [Fact]
    public void PushAndPopReturnThroughTheStack()
    {
        var function = Thumb(
            (0xB510, "stmdb sp!, {r4, lr}"),
            (0x2407, "movs r4, #0x7"),
            (0xBD10, "ldmia sp!, {r4, pc}"));

        var ctx = Run(function, new RecompContext { R4 = 0x1234, R13 = 0x03007000 });

        Assert.Equal(0x1234u, ctx.R4);
        Assert.Equal(0x03007000u, ctx.R13);
        Assert.Equal(ReturnAddress, AGBModern.Memory.Read32(0x03006FFC));
    }

    [Fact]
    public void BlockTransfersWriteTheBaseBack()
    {
        const uint Buffer = 0x02001200;

        var function = Thumb((0xC105, "stmia r1!, {r0, r2}"), (0x4770, "bx lr"));
        var ctx = Run(function, new RecompContext { R0 = 0x11, R1 = Buffer, R2 = 0x22 });
        Assert.Equal(Buffer + 8, ctx.R1);
        Assert.Equal(0x11u, AGBModern.Memory.Read32(Buffer));
        Assert.Equal(0x22u, AGBModern.Memory.Read32(Buffer + 4));

        function = Thumb((0xC905, "ldmia r1!, {r0, r2}"), (0x4770, "bx lr"));
        ctx = Run(function, new RecompContext { R1 = Buffer });
        Assert.Equal((0x11u, 0x22u, Buffer + 8), (ctx.R0, ctx.R2, ctx.R1));

        function = Thumb((0xC903, "ldmia r1!, {r0, r1}"), (0x4770, "bx lr"));
        ctx = Run(function, new RecompContext { R1 = Buffer });
        Assert.Equal((0x11u, 0x22u), (ctx.R0, ctx.R1));
    }

    [Fact]
    public void CyclesFollowTheBusTimings()
    {
        AGBModern.Memory.Write16(WAITCNT, 0x4317);

        var function = Thumb((0x1840, "adds r0, r0, r1"), (0x4770, "bx lr"));
        Assert.Equal(2 + 2 + 6, Cycles(function, new RecompContext()));

        function = Thumb((0x6808, "ldr r0, [r1]"), (0x4770, "bx lr"));
        Assert.Equal((2 + 1 + 1) + 1 + 6, Cycles(function, new RecompContext { R1 = 0x03000100 }));
        Assert.Equal((2 + 1 + 6) + 1 + 6, Cycles(function, new RecompContext { R1 = 0x02000100 }));

        AGBModern.Memory.Write16(WAITCNT, 0);
        Assert.Equal((5 + 1 + 1) + 3 + 8, Cycles(function, new RecompContext { R1 = 0x03000100 }));
    }

    [Theory]
    [InlineData(0x00000010u, 1)]
    [InlineData(0xFFFFFF00u, 1)]
    [InlineData(0x00123456u, 3)]
    [InlineData(0x12345678u, 4)]
    public void MultipliesTakeLongerForWiderMultipliers(uint multiplier, int internalCycles)
    {
        AGBModern.Memory.Write16(WAITCNT, 0x4317);
        var function = Thumb((0x4348, "muls r0, r1, r0"), (0x4770, "bx lr"));

        Assert.Equal(2 + internalCycles + 1 + 6, Cycles(function, new RecompContext { R0 = multiplier, R1 = 3 }));
        AGBModern.Memory.Write16(WAITCNT, 0);
    }

    [Fact]
    public void ALoadFromNothingReadsTheOpcodeFetchedAhead()
    {
        var function = Thumb((0x6808, "ldr r0, [r1]"), (0x46C0, "mov r8, r8"), (0x4770, "bx lr"));

        Assert.Equal(0x47704770u, Run(function, new RecompContext { R1 = 0x10000000 }).R0);
    }

    [Fact]
    public void BxPCSwitchesToARMAtTheNextWord()
    {
        byte[] code = [0x78, 0x47, 0xC0, 0x46, 0x05, 0x00, 0xA0, 0xE3, 0x1E, 0xFF, 0x2F, 0xE1];
        var function = Recompile(code, isThumb: true, "bx pc", "mov r0, #0x5", "bx lr");

        Assert.Equal(5u, Run(function, new RecompContext()).R0);
    }

    [Fact]
    public void AJumpBackIntoTheFunctionThatCantBeFollowedFailsLoudly()
    {
        byte[] code = [0x87, 0x46, 0x70, 0x47];
        var function = Recompile(code, isThumb: true, "mov pc, r0");

        Assert.Throws<InvalidOperationException>(() => Run(function, new RecompContext { R0 = Address + 3 }));
        Assert.Equal(ReturnAddress, Run(function, new RecompContext { R0 = ReturnAddress }).R0);
    }

    [Fact]
    public void PCRelativeValuesAreWordAligned()
    {
        var function = Thumb(
            (0x46C0, "mov r8, r8"),
            (0xA001, "add r0, pc, #0x4"),
            (0x4679, "mov r1, pc"),
            (0x4770, "bx lr"));

        var ctx = Run(function, new RecompContext());

        Assert.Equal(Address + 4 + 4, ctx.R0);
        Assert.Equal(Address + 4 + 4, ctx.R1);
    }
}
