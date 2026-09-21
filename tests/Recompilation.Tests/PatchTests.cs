using System.Reflection;
using GBARecomp;
using LibRecomp;

namespace Recompilation.Tests;

public class PatchTests
{
    private static readonly ushort[] Code = [0xB500, 0xF000, 0xF801, 0xBD00, 0x2001, 0x4770];

    private static Type Recompile()
    {
        return RecompiledCode.RecompileProgram(Code, [new(RecompiledCode.Address, 8, "Caller"), new(RecompiledCode.Address + 8, 4, "Callee")]);
    }

    private static uint CallCaller(Type funcs)
    {
        return RecompiledCode.Run(RecompiledCode.Function(funcs, "Caller"), new RecompContext { R13 = 0x03007F00 }).R0;
    }

    [Fact]
    public void AFunctionRunsAsRecompiledUntilItIsReplaced()
    {
        Assert.Equal(1u, CallCaller(Recompile()));
    }

    [Fact]
    public void AReplacementIsCalledInPlaceOfTheFunctionAndCanCallTheOriginal()
    {
        var funcs = Recompile();
        var original = funcs.GetNestedType("Original")!.GetMethod("Callee")!.CreateDelegate<RecompFunc>();
        var slot = funcs.GetNestedType("Patches")!.GetField("Callee", BindingFlags.Public | BindingFlags.Static)!;

        slot.SetValue(null, (RecompFunc)(ctx =>
        {
            original(ctx);
            ctx.R0 += 10;
        }));

        Assert.Equal(11u, CallCaller(funcs));
    }
}
