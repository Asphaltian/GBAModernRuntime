using AGBModern;

namespace LibRecomp;

/// <summary>
/// The CPU's registers and flags. A function gets its first four arguments in R0 to R3 and the rest
/// on the stack at R13, and hands its result back in R0.
/// </summary>
/// <example>
/// <code>
/// // Call a game function with two arguments and read what it returns
/// (ctx.R0, ctx.R1) = (x, y);
/// Funcs.sub_8000400(ctx);
/// uint result = ctx.R0;
/// </code>
/// </example>
public sealed class RecompContext
{
    private const uint ModeMask = 0x1F;
    private const uint ControlMask = 0xFF;
    private const uint FlagsMask = 0xF0000000;
    private const uint UserMode = 0x10;
    private const uint FIQMode = 0x11;
    private const uint IRQMode = 0x12;
    private const uint SupervisorMode = 0x13;
    private const uint SystemMode = 0x1F;
    private const uint ThumbState = 0x20;
    private const uint IRQDisable = 0x80;

    public uint R0, R1, R2, R3, R4, R5, R6, R7, R8, R9, R10, R11, R12, R13, R14;

    public bool N, Z, C, V;

    private readonly uint[] _fiqR8ToR12 = new uint[5];
    private readonly uint[] _otherR8ToR12 = new uint[5];
    private readonly uint[] _bankedR13 = new uint[6];
    private readonly uint[] _bankedR14 = new uint[6];
    private readonly uint[] _spsr = new uint[6];

    private uint _control = SystemMode;

    public RecompContext()
    {
        R13 = 0x03007F00;
        _bankedR13[BankIndex(IRQMode)] = 0x03007FA0;
        _bankedR13[BankIndex(SupervisorMode)] = 0x03007FE0;
    }

    public uint CPSR => _control | (N ? 1u << 31 : 0) | (Z ? 1u << 30 : 0) | (C ? 1u << 29 : 0) | (V ? 1u << 28 : 0);

    public uint SPSR => HasSPSR ? _spsr[BankIndex(_control)] : CPSR;

    internal bool AreIRQsEnabled => (_control & IRQDisable) == 0;

    private bool HasSPSR => BankIndex(_control) != 0;

    /// <summary>This is called by recompiled code, so you won't need it yourself.</summary>
    public void WriteCPSR(uint value, int fieldMask)
    {
        if ((fieldMask & 8) != 0)
        {
            N = (value & (1u << 31)) != 0;
            Z = (value & (1u << 30)) != 0;
            C = (value & (1u << 29)) != 0;
            V = (value & (1u << 28)) != 0;
        }

        if ((fieldMask & 1) != 0 && (_control & ModeMask) != UserMode)
        {
            SwitchMode(value);
            _control = value & ControlMask;

            if (AreIRQsEnabled && Interrupts.IsPending)
            {
                Scheduler.NextEvent = Scheduler.Cycles;
            }
        }
    }

    /// <summary>This is called by recompiled code, so you won't need it yourself.</summary>
    public void WriteSPSR(uint value, int fieldMask)
    {
        if (!HasSPSR)
        {
            return;
        }

        uint mask = ((fieldMask & 8) != 0 ? FlagsMask : 0) | ((fieldMask & 1) != 0 ? ControlMask : 0);
        ref uint spsr = ref _spsr[BankIndex(_control)];
        spsr = (spsr & ~mask) | (value & mask);
    }

    /// <summary>This is called by recompiled code, so you won't need it yourself.</summary>
    public void EnterException(uint mode)
    {
        uint cpsr = CPSR;
        SwitchMode(mode);
        _control = (_control & ~ModeMask & ~ThumbState) | mode | IRQDisable;
        _spsr[BankIndex(mode)] = cpsr;
    }

    /// <summary>This is called by recompiled code, so you won't need it yourself.</summary>
    public void ReturnFromException()
    {
        WriteCPSR(SPSR, 0b1001);
    }

    private void SwitchMode(uint newMode)
    {
        int from = BankIndex(_control);
        int to = BankIndex(newMode);
        if (from == to)
        {
            return;
        }

        _bankedR13[from] = R13;
        _bankedR14[from] = R14;
        R13 = _bankedR13[to];
        R14 = _bankedR14[to];

        bool wasFIQ = (_control & ModeMask) == FIQMode;
        bool isFIQ = (newMode & ModeMask) == FIQMode;
        if (wasFIQ != isFIQ)
        {
            var outgoing = isFIQ ? _otherR8ToR12 : _fiqR8ToR12;
            var incoming = isFIQ ? _fiqR8ToR12 : _otherR8ToR12;
            (outgoing[0], outgoing[1], outgoing[2], outgoing[3], outgoing[4]) = (R8, R9, R10, R11, R12);
            (R8, R9, R10, R11, R12) = (incoming[0], incoming[1], incoming[2], incoming[3], incoming[4]);
        }
    }

    private static int BankIndex(uint mode) => (mode & ModeMask) switch
    {
        0x11 => 1, // FIQ
        0x12 => 2, // IRQ
        0x13 => 3, // Supervisor
        0x17 => 4, // Abort
        0x1B => 5, // Undefined
        _ => 0,    // User and System
    };
}
