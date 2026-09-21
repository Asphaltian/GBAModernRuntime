using AGBModern;

namespace LibRecomp;

internal static partial class BIOS
{
    private const uint IRQMode = 0x12;
    private const uint InterruptCheckFlags = 0x03007FF8;
    private const uint InterruptHandler = 0x03007FFC;
    private const uint ResetFlag = 0x03007FFA;
    private const uint ResetClearStart = 0x03007E00;
    private const int ResetClearLength = 0x200;
    private const uint HandlerReturnAddress = 0x00000138;
    private const uint BIOSEnd = 0x00004000;
    private const uint Checksum = 0xBAAE187F;
    private const uint R3AfterArcTan2 = 0x170;
    private const uint RAMStart = 0x02000000;
    internal const uint ROMStart = 0x08000000;

    private const uint OpcodeAfterBoot = 0xE129F000;
    private const uint OpcodeDuringIRQ = 0xE25EF004;
    private const uint OpcodeAfterIRQ = 0xE55EC002;
    private const uint OpcodeAfterSWI = 0xE3A02004;

    private const uint DISPCNT = 0x000;
    private const uint DISPSTAT = 0x004;
    private const uint BG2PA = 0x020;
    private const uint BG2PD = 0x026;
    private const uint BG3PA = 0x030;
    private const uint BG3PD = 0x036;
    private const uint SOUND1CNT_L = 0x060;
    private const uint SOUNDBIAS = 0x088;
    private const uint DMA0SAD = 0x0B0;
    private const uint TM0CNT_L = 0x100;
    private const uint SIOCNT = 0x128;
    private const uint SIOMLT_SEND = 0x12A;
    private const uint RCNT = 0x134;
    private const uint JOYCNT = 0x140;
    private const uint JOY_RECV = 0x150;
    private const uint JOY_TRANS = 0x154;
    private const uint POSTFLG = 0x04000300;
    private const uint HALTCNT = 0x04000301;
    private const uint MemoryControl = 0x04000800;

    public static void Call(RecompContext ctx, int function)
    {
        Run(ctx, function);
        Memory.BIOSOpcode = OpcodeAfterSWI;
    }

    internal static void Boot()
    {
        Memory.BIOSOpcode = OpcodeAfterBoot;
        Memory.Write8(POSTFLG, 1);
    }

    private static void Run(RecompContext ctx, int function)
    {
        switch (function)
        {
            case 0x00:
                SoftReset();
                break;
            case 0x01:
                RegisterRamReset(ctx.R0);
                break;
            case 0x02:
                Power.Halt();
                Recomp.HandleEvents(ctx);
                break;
            case 0x03:
                Power.Stop();
                break;
            case 0x04:
                IntrWait(ctx, discardOldFlags: ctx.R0 != 0, (ushort)ctx.R1);
                break;
            case 0x05:
                (ctx.R0, ctx.R1) = (1, 1);
                IntrWait(ctx, discardOldFlags: true, (ushort)Interrupt.VBlank);
                break;
            case 0x06:
                Div(ctx, (int)ctx.R0, (int)ctx.R1);
                break;
            case 0x07:
                Div(ctx, (int)ctx.R1, (int)ctx.R0);
                break;
            case 0x08:
                ctx.R0 = Sqrt(ctx.R0);
                break;
            case 0x09:
                ctx.R0 = (uint)ArcTan((int)ctx.R0, out int a, out int b);
                (ctx.R1, ctx.R3) = ((uint)a, (uint)b);
                break;
            case 0x0A:
                ctx.R0 = (ushort)ArcTan2(ctx, (int)ctx.R0, (int)ctx.R1);
                ctx.R3 = R3AfterArcTan2;
                break;
            case 0x0B:
                CpuSet(ctx, ctx.R0, ctx.R1, ctx.R2);
                break;
            case 0x0C:
                CpuFastSet(ctx, ctx.R0, ctx.R1, ctx.R2);
                break;
            case 0x0D:
                ctx.R0 = Checksum;
                break;
            case 0x0E:
                BgAffineSet(ctx.R0, ctx.R1, ctx.R2);
                break;
            case 0x0F:
                ObjAffineSet(ctx.R0, ctx.R1, ctx.R2, ctx.R3);
                break;
            case 0x10:
                BitUnPack(ctx, ctx.R0, ctx.R1, ctx.R2);
                break;
            case 0x11:
                LZ77UnComp(ctx.R0, new Output(ctx, ctx.R1, halfwords: false));
                break;
            case 0x12:
                LZ77UnComp(ctx.R0, new Output(ctx, ctx.R1, halfwords: true));
                break;
            case 0x13:
                HuffUnComp(ctx, ctx.R0, ctx.R1);
                break;
            case 0x14:
                RLUnComp(ctx.R0, new Output(ctx, ctx.R1, halfwords: false));
                break;
            case 0x15:
                RLUnComp(ctx.R0, new Output(ctx, ctx.R1, halfwords: true));
                break;
            case 0x16:
                Diff8bitUnFilter(ctx.R0, new Output(ctx, ctx.R1, halfwords: false));
                break;
            case 0x17:
                Diff8bitUnFilter(ctx.R0, new Output(ctx, ctx.R1, halfwords: true));
                break;
            case 0x18:
                Diff16bitUnFilter(ctx, ctx.R0, ctx.R1);
                break;
            case 0x19:
                SoundBias(ctx.R0);
                break;
            case 0x1F:
                ctx.R0 = MidiKey2Freq(ctx.R0, (byte)ctx.R1, (byte)ctx.R2);
                break;
            case 0x25:
                ctx.R0 = 1;
                break;
            case 0x26:
                HardReset();
                break;
            case 0x27:
                Memory.Write8(HALTCNT, (byte)ctx.R2);
                break;
            case (>= 0x1A and <= 0x1E) or (>= 0x20 and <= 0x24) or (>= 0x28 and <= 0x2A):
                throw new NotSupportedException($"SWI 0x{function:X2} is part of the BIOS sound driver, which is not implemented.");
            default:
                throw new InvalidOperationException($"SWI 0x{function:X2} is not a BIOS function!");
        }
    }

    public static void EnterIRQ(RecompContext ctx)
    {
        ctx.EnterException(IRQMode);

        uint sp = ctx.R13 - 24;
        Memory.Write32(sp, ctx.R0);
        Memory.Write32(sp + 4, ctx.R1);
        Memory.Write32(sp + 8, ctx.R2);
        Memory.Write32(sp + 12, ctx.R3);
        Memory.Write32(sp + 16, ctx.R12);
        Memory.Write32(sp + 20, ctx.R14);
        ctx.R13 = sp;

        ctx.R0 = 0x04000000;
        ctx.R14 = HandlerReturnAddress;
        Memory.BIOSOpcode = OpcodeDuringIRQ;
        Recomp.LookupFunc(Memory.Read32(InterruptHandler))(ctx);
        Recomp.ThrowIfUnwinding();
        Memory.BIOSOpcode = OpcodeAfterIRQ;

        sp = ctx.R13;
        ctx.R0 = Memory.Read32(sp);
        ctx.R1 = Memory.Read32(sp + 4);
        ctx.R2 = Memory.Read32(sp + 8);
        ctx.R3 = Memory.Read32(sp + 12);
        ctx.R12 = Memory.Read32(sp + 16);
        ctx.R14 = Memory.Read32(sp + 20);
        ctx.R13 = sp + 24;

        ctx.ReturnFromException();
    }

    private static void SoftReset()
    {
        uint start = Memory.Read8(ResetFlag) == 0 ? ROMStart : RAMStart;
        Array.Clear(Memory.IWRAM, (int)(ResetClearStart & 0x7FFF), ResetClearLength);
        throw new GameReset(start);
    }

    private static void RegisterRamReset(uint flags)
    {
        if ((flags & 1 << 0) != 0)
        {
            Array.Clear(Memory.EWRAM);
        }

        if ((flags & 1 << 1) != 0)
        {
            Array.Clear(Memory.IWRAM, 0, Memory.IWRAM.Length - ResetClearLength);
        }

        if ((flags & 1 << 2) != 0)
        {
            Array.Clear(Memory.PaletteRAM);
        }

        if ((flags & 1 << 3) != 0)
        {
            Array.Clear(Memory.VRAM);
        }

        if ((flags & 1 << 4) != 0)
        {
            Array.Clear(Memory.OAM);
        }

        if ((flags & 1 << 5) != 0)
        {
            IO.Write16(SIOCNT, 0);
            IO.Write16(RCNT, 0x8000);
            IO.Write16(SIOMLT_SEND, 0);
            IO.Write16(JOYCNT, 0);
            ClearRegisters(JOY_RECV, JOY_RECV + 4);
            ClearRegisters(JOY_TRANS, JOY_TRANS + 4);
        }

        if ((flags & 1 << 6) != 0)
        {
            ClearRegisters(SOUND1CNT_L, SOUNDBIAS + 2);
            APU.ClearWaveRAM();
            IO.Write16(SOUNDBIAS, 0x200);
        }

        if ((flags & 1 << 7) != 0)
        {
            ClearRegisters(DISPSTAT, SOUND1CNT_L);
            IO.Write16(BG2PA, 0x100);
            IO.Write16(BG2PD, 0x100);
            IO.Write16(BG3PA, 0x100);
            IO.Write16(BG3PD, 0x100);
            ClearRegisters(DMA0SAD, DMA0SAD + (4 * 12));
            ClearRegisters(TM0CNT_L, TM0CNT_L + (4 * 4));
            ClearRegisters(IO.IE, IO.IF);
            ClearRegisters(IO.IF + 2, IO.IME + 2);
            IO.Write16(IO.IF, 0xFFFF);
        }

        IO.Write16(DISPCNT, 0x0080);
    }

    private static void HardReset()
    {
        RegisterRamReset(0xFF);
        Array.Clear(Memory.IWRAM);
        Memory.Write32(MemoryControl, IO.MemoryControlAtPowerOn);
        throw new GameReset(ROMStart);
    }

    private static void BgAffineSet(uint source, uint destination, uint count)
    {
        for (uint i = 0; i < count; i++, source += 20, destination += 16)
        {
            float originX = (int)Memory.Read32(source) / 256f;
            float originY = (int)Memory.Read32(source + 4) / 256f;
            float centerX = (short)Memory.Read16(source + 8);
            float centerY = (short)Memory.Read16(source + 10);
            var (a, b, c, d) = AffineMatrix(Memory.Read16(source + 12), Memory.Read16(source + 14), Memory.Read16(source + 16));

            Memory.Write16(destination, (ushort)(short)(a * 256));
            Memory.Write16(destination + 2, (ushort)(short)(b * 256));
            Memory.Write16(destination + 4, (ushort)(short)(c * 256));
            Memory.Write16(destination + 6, (ushort)(short)(d * 256));
            Memory.Write32(destination + 8, (uint)(int)((originX - ((a * centerX) + (b * centerY))) * 256));
            Memory.Write32(destination + 12, (uint)(int)((originY - ((c * centerX) + (d * centerY))) * 256));
        }
    }

    private static void ObjAffineSet(uint source, uint destination, uint count, uint offset)
    {
        for (uint i = 0; i < count; i++, source += 8, destination += offset * 4)
        {
            var (a, b, c, d) = AffineMatrix(Memory.Read16(source), Memory.Read16(source + 2), Memory.Read16(source + 4));

            Memory.Write16(destination, (ushort)(short)(a * 256));
            Memory.Write16(destination + offset, (ushort)(short)(b * 256));
            Memory.Write16(destination + (offset * 2), (ushort)(short)(c * 256));
            Memory.Write16(destination + (offset * 3), (ushort)(short)(d * 256));
        }
    }

    private static (float A, float B, float C, float D) AffineMatrix(ushort scaleX, ushort scaleY, ushort angle)
    {
        float sx = (short)scaleX / 256f;
        float sy = (short)scaleY / 256f;
        float theta = (float)((angle >> 8) / 128f * Math.PI);
        float cos = MathF.Cos(theta);
        float sin = MathF.Sin(theta);
        return (cos * sx, sin * -sx, sin * sy, cos * sy);
    }

    private static void ClearRegisters(uint start, uint end)
    {
        for (uint offset = start; offset < end; offset += 2)
        {
            IO.Write16(offset, 0);
        }
    }

    private static void IntrWait(RecompContext ctx, bool discardOldFlags, ushort flags)
    {
        IO.Write16(IO.IME, 1);

        if (discardOldFlags)
        {
            Memory.Write16(InterruptCheckFlags, (ushort)(Memory.Read16(InterruptCheckFlags) & ~flags));
        }

        while ((Memory.Read16(InterruptCheckFlags) & flags) == 0)
        {
            Power.Halt();
            Recomp.HandleEvents(ctx);
        }

        Memory.Write16(InterruptCheckFlags, (ushort)(Memory.Read16(InterruptCheckFlags) & ~flags));
    }

    private static void Div(RecompContext ctx, int number, int denominator)
    {
        while (denominator == 0)
        {
            Scheduler.SkipToNextEvent();
            Scheduler.RunDueEvents();
            Recomp.StopIfQuitting();
        }

        long quotient = (long)number / denominator;
        ctx.R0 = (uint)quotient;
        ctx.R1 = (uint)(number - (quotient * denominator));
        ctx.R3 = (uint)Math.Abs(quotient);
    }

    private static uint Sqrt(uint value)
    {
        uint root = (uint)Math.Sqrt(value);
        while ((ulong)root * root > value)
        {
            root--;
        }

        while ((ulong)(root + 1) * (root + 1) <= value)
        {
            root++;
        }

        return root;
    }

    private static short ArcTan(int tangent, out int a, out int b)
    {
        a = -((tangent * tangent) >> 14);
        b = ((0xA9 * a) >> 14) + 0x390;
        b = ((b * a) >> 14) + 0x91C;
        b = ((b * a) >> 14) + 0xFB6;
        b = ((b * a) >> 14) + 0x16AA;
        b = ((b * a) >> 14) + 0x2081;
        b = ((b * a) >> 14) + 0x3651;
        b = ((b * a) >> 14) + 0xA2F9;
        return (short)((tangent * b) >> 16);
    }

    private static int ArcTan2(RecompContext ctx, int x, int y)
    {
        if (y == 0)
        {
            return x >= 0 ? 0 : 0x8000;
        }

        if (x == 0)
        {
            return y >= 0 ? 0x4000 : 0xC000;
        }

        int ArcTanOf(int tangent)
        {
            short angle = ArcTan(tangent, out int a, out _);
            ctx.R1 = (uint)a;
            return angle;
        }

        if (y >= 0)
        {
            if (x >= 0 ? x >= y : -x >= y)
            {
                return ArcTanOf((y << 14) / x) + (x >= 0 ? 0 : 0x8000);
            }

            return 0x4000 - ArcTanOf((x << 14) / y);
        }

        if (x <= 0 ? -x > -y : x >= -y)
        {
            return ArcTanOf((y << 14) / x) + (x <= 0 ? 0x8000 : 0x10000);
        }

        return 0xC000 - ArcTanOf((x << 14) / y);
    }

    private static void CpuSet(RecompContext ctx, uint source, uint destination, uint control)
    {
        uint count = control & 0x1FFFFF;
        bool fill = (control & (1 << 24)) != 0;
        uint unit = (control & (1 << 26)) != 0 ? 4u : 2u;
        if (ReadsBIOS(source, count * unit))
        {
            return;
        }

        uint filler = !fill ? 0 : unit == 4 ? Memory.Read32(source) : Memory.Read16(source);
        for (uint i = 0; i < count; i++)
        {
            if (unit == 4)
            {
                Memory.Write32(destination + (i * 4), fill ? filler : Memory.Read32(source + (i * 4)));
            }
            else
            {
                Memory.Write16(destination + (i * 2), (ushort)(fill ? filler : Memory.Read16(source + (i * 2))));
            }

            CheckEvents(ctx);
        }
    }

    private static void CpuFastSet(RecompContext ctx, uint source, uint destination, uint control)
    {
        uint count = ((control & 0x1FFFFF) + 7) & ~7u;
        bool fill = (control & (1 << 24)) != 0;
        if (ReadsBIOS(source, count * 4))
        {
            return;
        }

        uint filler = fill ? Memory.Read32(source) : 0;
        for (uint i = 0; i < count; i++)
        {
            Memory.Write32(destination + (i * 4), fill ? filler : Memory.Read32(source + (i * 4)));
            CheckEvents(ctx);
        }
    }

    private static bool ReadsBIOS(uint source, uint length) => source < BIOSEnd || source + length < BIOSEnd;

    private static void SoundBias(uint level)
    {
        ushort bias = IO.Read16(SOUNDBIAS);
        IO.Write16(SOUNDBIAS, (ushort)((bias & ~0x3FE) | (level == 0 ? 0 : 0x200)));
    }

    private static uint MidiKey2Freq(uint waveData, byte key, byte fineAdjust)
    {
        uint frequency = Memory.Read32(waveData + 4);
        return (uint)(frequency * Math.Pow(2, (key - 180 + (fineAdjust / 256.0)) / 12));
    }

    private static void CheckEvents(RecompContext ctx)
    {
        if (Scheduler.Cycles >= Scheduler.NextEvent)
        {
            Recomp.HandleEvents(ctx);
        }
    }

    internal sealed class GameReset(uint start) : Exception
    {
        public uint Start { get; } = start;
    }
}
