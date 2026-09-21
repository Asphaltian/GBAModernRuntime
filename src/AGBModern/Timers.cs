namespace AGBModern;

internal static class Timers
{
    private const ushort CountUp = 1 << 2;
    private const ushort IRQEnable = 1 << 6;
    private const ushort Start = 1 << 7;

    private static readonly Timer[] All = [new(0), new(1), new(2), new(3)];

    public static event Action<int, long>? Overflowed;

    public static ushort ReadCounter(int timer) => All[timer].Counter;

    public static ushort ReadControl(int timer) => All[timer].Control;

    public static void WriteReload(int timer, ushort value) => All[timer].Reload = value;

    public static void WriteControl(int timer, ushort value) => All[timer].WriteControl(value);

    private sealed class Timer
    {
        private readonly int _index;
        private readonly Scheduler.Event _overflow;

        private ushort _counter;
        private long _zeroAt;

        public Timer(int index)
        {
            _index = index;
            _overflow = Scheduler.CreateEvent(Overflow);
        }

        public ushort Reload { get; set; }

        public ushort Control { get; private set; }

        public ushort Counter => IsClocked ? (ushort)((Scheduler.Cycles - _zeroAt) >> PrescalerShift) : _counter;

        private bool IsRunning => (Control & Start) != 0;

        private bool IsClocked => IsRunning && (Control & CountUp) == 0;

        private int PrescalerShift => (Control & 3) switch
        {
            0 => 0,
            1 => 6,
            2 => 8,
            _ => 10,
        };

        public void WriteControl(ushort value)
        {
            ushort newControl = (ushort)(value & (_index == 0 ? 0x00C3 : 0x00C7));
            bool starts = !IsRunning && (newControl & Start) != 0;

            _counter = starts ? Reload : Counter;
            Control = newControl;

            if (IsClocked)
            {
                RunFrom(_counter, Scheduler.Cycles);
            }
            else
            {
                _overflow.Cancel();
            }
        }

        private void RunFrom(ushort counter, long startedAt)
        {
            _zeroAt = startedAt - ((long)counter << PrescalerShift);
            _overflow.ScheduleAt(_zeroAt + (0x10000L << PrescalerShift));
        }

        private void Overflow(long overflowedAt)
        {
            if (IsClocked)
            {
                RunFrom(Reload, overflowedAt);
            }
            else
            {
                _counter = Reload;
            }

            if ((Control & IRQEnable) != 0)
            {
                Interrupts.Raise((Interrupt)((ushort)Interrupt.Timer0 << _index));
            }

            Overflowed?.Invoke(_index, overflowedAt);

            if (_index < 3)
            {
                All[_index + 1].CountOverflowBelow(overflowedAt);
            }
        }

        private void CountOverflowBelow(long overflowedAt)
        {
            if (IsRunning && (Control & CountUp) != 0 && ++_counter == 0)
            {
                Overflow(overflowedAt);
            }
        }
    }
}
