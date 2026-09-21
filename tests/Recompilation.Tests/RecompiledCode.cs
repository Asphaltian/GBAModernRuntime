using System.Buffers.Binary;
using System.Reflection;
using AGBModern;
using GBARecomp;
using GBARecomp.ARM;
using LibRecomp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Recompilation.Tests;

public static class RecompiledCode
{
    public const uint Address = ROM.BaseAddress;
    public const uint ReturnAddress = 0x08F00001;
    public const uint WAITCNT = 0x04000204;

    private static readonly MetadataReference[] References =
        [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Append(typeof(RecompContext).Assembly.Location)
            .Append(typeof(Memory).Assembly.Location)
            .Distinct()
            .Select(path => MetadataReference.CreateFromFile(path))];

    public static RecompFunc Thumb(params (ushort Encoding, string Disassembly)[] code)
    {
        return Recompile(ThumbBytes([.. code.Select(c => c.Encoding)]), isThumb: true, [.. code.Select(c => c.Disassembly)]);
    }

    public static RecompFunc ARM(params (uint Encoding, string Disassembly)[] code)
    {
        byte[] bytes = new byte[code.Length * 4];
        for (int i = 0; i < code.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), code[i].Encoding);
        }

        return Recompile(bytes, isThumb: false, [.. code.Select(c => c.Disassembly)]);
    }

    public static RecompFunc Recompile(byte[] bytes, bool isThumb, params string[] disassembly)
    {
        var input = new InputConfig { TextAddress = Address, TextSize = (uint)bytes.Length };
        if (!isThumb)
        {
            input.ARMFuncs.Add("Test");
        }

        var context = Context.Create(new ROM(bytes), [new Symbol(Address, (uint)bytes.Length, "Test")], input);
        var analysis = FunctionAnalysis.Analyze(context, context.Functions[0]);
        Assert.Empty(analysis.Errors);

        Assert.Equal(disassembly, analysis.Instructions.Select(i => Disassembler.Format(i)));

        var generator = new CSharpGenerator(context);
        string source = generator.GenerateFile([generator.Generate(analysis)]);
        var method = Compile(source, generator.GenerateTables()).GetType("RecompiledFuncs.Funcs")!.GetMethod("Test")!;
        return method.CreateDelegate<RecompFunc>();
    }

    internal static Type RecompileProgram(ushort[] code, Symbol[] symbols, string[]? ramFuncs = null)
    {
        byte[] bytes = ThumbBytes(code);
        bytes.CopyTo(Memory.ROM, (int)(Address & 0x1FFFFFF));
        var input = new InputConfig { TextAddress = Address, TextSize = (uint)bytes.Length, RAMFuncs = [.. ramFuncs ?? []] };
        var context = Context.Create(new ROM(bytes), symbols, input);
        context.AddStaticFunctions();
        context.FindPCRelativeAddresses();

        var analyses = context.Functions.Select(f => FunctionAnalysis.Analyze(context, f)).ToList();
        Assert.All(analyses, analysis => Assert.Empty(analysis.Errors));

        var generator = new CSharpGenerator(context);
        var funcs = Compile(generator.GenerateFile(analyses.Select(generator.Generate)), generator.GenerateTables()).GetType("RecompiledFuncs.Funcs")!;
        Recomp.RegisterFunctions((((uint, RecompFunc)[])funcs.GetField("Table")!.GetValue(null)!).AsSpan());
        Recomp.RegisterRAMFunctions((((uint, uint, RecompFunc)[])funcs.GetField("RAMFunctions")!.GetValue(null)!).AsSpan());
        return funcs;
    }

    public static RecompFunc Function(Type funcs, string name) => funcs.GetMethod(name)!.CreateDelegate<RecompFunc>();

    public static RecompContext Run(RecompFunc function, RecompContext ctx)
    {
        ctx.R14 = ReturnAddress;
        function(ctx);
        return ctx;
    }

    public static long Cycles(RecompFunc function, RecompContext ctx)
    {
        long before = Scheduler.Cycles;
        Run(function, ctx);
        return Scheduler.Cycles - before;
    }

    public static Assembly Compile(params string[] sources) => Assembly.Load(Emit("RecompiledTest", sources));

    public static byte[] Emit(string assemblyName, string[] sources, params byte[][] references)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName,
            sources.Select(source => CSharpSyntaxTree.ParseText(source)),
            [.. References, .. references.Select(image => MetadataReference.CreateFromImage(image))],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));

        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics) + Environment.NewLine + string.Join(Environment.NewLine, sources));

        return image.ToArray();
    }

    private static byte[] ThumbBytes(ushort[] code)
    {
        byte[] bytes = new byte[code.Length * 2];
        for (int i = 0; i < code.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), code[i]);
        }

        return bytes;
    }
}
