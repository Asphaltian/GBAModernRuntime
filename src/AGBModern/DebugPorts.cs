using System.Text;

namespace AGBModern;

internal static class DebugPorts
{
    public const uint Start = 0xFFFA00;
    public const uint End = 0xFFFA28;

    private const uint CharOut = 0xFFFA1C;
    private const uint ClockCycles = 0xFFFA20;

    private static readonly byte[] EmulationID = Encoding.ASCII.GetBytes("GBAModernRuntime");

    public static ushort Read16(uint offset)
    {
        if (offset < CharOut)
        {
            int index = (int)(offset - Start);
            return index < EmulationID.Length ? (ushort)(EmulationID[index] | (EmulationID[index + 1] << 8)) : (ushort)0;
        }

        return offset >= ClockCycles ? (ushort)(Scheduler.Cycles >> (int)((offset - ClockCycles) * 8)) : (ushort)0;
    }

    public static void Write(uint offset, byte value)
    {
        if (offset == CharOut)
        {
            Console.Write((char)value);
        }
    }
}
