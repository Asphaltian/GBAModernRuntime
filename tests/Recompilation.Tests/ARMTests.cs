using LibRecomp;
using static Recompilation.Tests.RecompiledCode;

namespace Recompilation.Tests;

public class ARMTests
{
    private const uint BxLr = 0xE12FFF1E;

    [Fact]
    public void ConditionalInstructionsOnlyRunWhenTheConditionHolds()
    {
        var function = ARM((0x12800001, "addne r0, r0, #0x1"), (BxLr, "bx lr"));

        Assert.Equal(6u, Run(function, new RecompContext { R0 = 5, Z = false }).R0);
        Assert.Equal(5u, Run(function, new RecompContext { R0 = 5, Z = true }).R0);
    }

    [Fact]
    public void ARotatedImmediateSetsTheCarryToItsTopBit()
    {
        var function = ARM((0xE3B00102, "movs r0, #0x80000000"), (BxLr, "bx lr"));
        var ctx = Run(function, new RecompContext());
        Assert.Equal((0x80000000u, true, true), (ctx.R0, ctx.N, ctx.C));

        function = ARM((0xE3B00C01, "movs r0, #0x100"), (BxLr, "bx lr"));
        ctx = Run(function, new RecompContext { C = true });
        Assert.Equal((0x100u, false), (ctx.R0, ctx.C));

        function = ARM((0xE3B000FF, "movs r0, #0xff"), (BxLr, "bx lr"));
        Assert.True(Run(function, new RecompContext { C = true }).C);
    }

    [Fact]
    public void RRXShiftsTheCarryIn()
    {
        var function = ARM((0xE1B00061, "movs r0, r1, rrx"), (BxLr, "bx lr"));

        var ctx = Run(function, new RecompContext { R1 = 0x00000003, C = true });
        Assert.Equal((0x80000001u, true), (ctx.R0, ctx.C));

        ctx = Run(function, new RecompContext { R1 = 0x00000002, C = false });
        Assert.Equal((0x00000001u, false), (ctx.R0, ctx.C));
    }

    [Fact]
    public void RscSubtractsTheOtherWayAround()
    {
        var function = ARM((0xE0F10002, "rscs r0, r1, r2"), (BxLr, "bx lr"));
        var ctx = Run(function, new RecompContext { R1 = 3, R2 = 10, C = false });

        Assert.Equal(6u, ctx.R0);
        Assert.True(ctx.C);
    }

    [Fact]
    public void LongMultiplies()
    {
        var function = ARM((0xE0832190, "umull r2, r3, r0, r1"), (BxLr, "bx lr"));
        var ctx = Run(function, new RecompContext { R0 = 0xFFFFFFFF, R1 = 0xFFFFFFFF });
        Assert.Equal((0x00000001u, 0xFFFFFFFEu), (ctx.R2, ctx.R3));

        function = ARM((0xE0C32190, "smull r2, r3, r0, r1"), (BxLr, "bx lr"));
        ctx = Run(function, new RecompContext { R0 = 0xFFFFFFFF, R1 = 2 });
        Assert.Equal((0xFFFFFFFEu, 0xFFFFFFFFu), (ctx.R2, ctx.R3));

        function = ARM((0xE0A32190, "umlal r2, r3, r0, r1"), (BxLr, "bx lr"));
        ctx = Run(function, new RecompContext { R0 = 2, R1 = 3, R2 = 0xFFFFFFFF, R3 = 1 });
        Assert.Equal((0x00000005u, 0x00000002u), (ctx.R2, ctx.R3));

        function = ARM((0xE0214392, "mla r1, r2, r3, r4"), (BxLr, "bx lr"));
        Assert.Equal(17u, Run(function, new RecompContext { R2 = 3, R3 = 4, R4 = 5 }).R1);
    }

    [Fact]
    public void TheStatusRegisterCarriesTheFlags()
    {
        var function = ARM((0xE10F0000, "mrs r0, cpsr"), (BxLr, "bx lr"));
        var ctx = Run(function, new RecompContext { N = true, C = true });
        Assert.Equal(0xA000001Fu, ctx.R0);

        function = ARM((0xE128F000, "msr cpsr_f, r0"), (BxLr, "bx lr"));
        ctx = Run(function, new RecompContext { R0 = 0x50000000 });
        Assert.Equal((false, true, false, true), (ctx.N, ctx.Z, ctx.C, ctx.V));
    }

    [Fact]
    public void IndexedAddressing()
    {
        const uint Buffer = 0x02002000;
        AGBModern.Memory.Write32(Buffer, 0x11111111);
        AGBModern.Memory.Write32(Buffer + 4, 0x22222222);
        AGBModern.Memory.Write32(Buffer + 8, 0x33333333);

        var function = ARM((0xE4910004, "ldr r0, [r1], #0x4"), (BxLr, "bx lr"));
        var ctx = Run(function, new RecompContext { R1 = Buffer });
        Assert.Equal((0x11111111u, Buffer + 4), (ctx.R0, ctx.R1));

        function = ARM((0xE5B10004, "ldr r0, [r1, #0x4]!"), (BxLr, "bx lr"));
        ctx = Run(function, new RecompContext { R1 = Buffer });
        Assert.Equal((0x22222222u, Buffer + 4), (ctx.R0, ctx.R1));

        function = ARM((0xE7910102, "ldr r0, [r1, r2, lsl #2]"), (BxLr, "bx lr"));
        ctx = Run(function, new RecompContext { R1 = Buffer, R2 = 2 });
        Assert.Equal(0x33333333u, ctx.R0);

        function = ARM((0xE5110004, "ldr r0, [r1, #-0x4]"), (BxLr, "bx lr"));
        ctx = Run(function, new RecompContext { R1 = Buffer + 8 });
        Assert.Equal(0x22222222u, ctx.R0);
    }

    [Fact]
    public void BlockTransferAddressingModes()
    {
        const uint Buffer = 0x02002100;

        var function = ARM(
            (0xE9A10005, "stmib r1!, {r0, r2}"),
            (0xE831000C, "ldmda r1!, {r2, r3}"),
            (BxLr, "bx lr"));

        var ctx = Run(function, new RecompContext { R0 = 0xAA, R1 = Buffer, R2 = 0xBB });

        Assert.Equal(0xAAu, AGBModern.Memory.Read32(Buffer + 4));
        Assert.Equal(0xBBu, AGBModern.Memory.Read32(Buffer + 8));
        Assert.Equal((0xAAu, 0xBBu, Buffer), (ctx.R2, ctx.R3, ctx.R1));
    }

    [Fact]
    public void ASkippedInstructionOnlyTakesItsFetch()
    {
        AGBModern.Memory.Write16(WAITCNT, 0x4317);
        var function = ARM((0x15910000, "ldrne r0, [r1]"), (BxLr, "bx lr"));

        long executed = Cycles(function, new RecompContext { R1 = 0x03000100, Z = false });
        long skipped = Cycles(function, new RecompContext { R1 = 0x03000100, Z = true });

        Assert.Equal(1 + 1, executed - skipped);
        AGBModern.Memory.Write16(WAITCNT, 0);
    }

    [Fact]
    public void BlockTransfersAreSequentialAfterTheFirstWord()
    {
        AGBModern.Memory.Write16(WAITCNT, 0x4317);
        var function = ARM((0xE891000C, "ldmia r1, {r2, r3}"), (BxLr, "bx lr"));

        long fromROM = Cycles(function, new RecompContext { R1 = 0x08000000 });
        long fromIWRAM = Cycles(function, new RecompContext { R1 = 0x03000100 });

        Assert.Equal((4 + 2) + (2 + 2) - (1 + 1), fromROM - fromIWRAM);
        AGBModern.Memory.Write16(WAITCNT, 0);
    }

    [Fact]
    public void ASkippedStoreFetchesSequentially()
    {
        AGBModern.Memory.Write16(WAITCNT, 0x4317);
        var function = ARM((0x15810000, "strne r0, [r1]"), (BxLr, "bx lr"));

        long executed = Cycles(function, new RecompContext { R1 = 0x03000100, Z = false });
        long skipped = Cycles(function, new RecompContext { R1 = 0x03000100, Z = true });

        Assert.Equal((6 - 4) + 1, executed - skipped);
        AGBModern.Memory.Write16(WAITCNT, 0);
    }

    [Fact]
    public void AnOperandShiftedByARegisterReadsPCTwelveAhead()
    {
        var function = ARM((0xE1A0011F, "mov r0, pc, lsl r1"), (BxLr, "bx lr"));

        Assert.Equal(Address + 12, Run(function, new RecompContext { R1 = 0 }).R0);
    }

    [Fact]
    public void ALoadFromNothingReadsTheOpcodeFetchedAhead()
    {
        var function = ARM((0xE5910000, "ldr r0, [r1]"), (0xE1A00000, "mov r0, r0"), (BxLr, "bx lr"));

        Assert.Equal(BxLr, Run(function, new RecompContext { R1 = 0x10000000 }).R0);
    }

    [Fact]
    public void Swap()
    {
        const uint Buffer = 0x02002200;
        AGBModern.Memory.Write32(Buffer, 0x12345678);

        var function = ARM((0xE1010092, "swp r0, r2, [r1]"), (BxLr, "bx lr"));
        var ctx = Run(function, new RecompContext { R1 = Buffer, R2 = 0x9ABCDEF0 });

        Assert.Equal(0x12345678u, ctx.R0);
        Assert.Equal(0x9ABCDEF0u, AGBModern.Memory.Read32(Buffer));
    }
}
