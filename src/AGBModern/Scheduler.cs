namespace AGBModern;

/// <summary>
/// Time on the GBA, counted in CPU cycles at 16.78 MHz. Anything that happens on its own, like a
/// new line starting or a timer running out, is an event here.
/// </summary>
public static class Scheduler
{
    /// <summary>How many cycles the GBA runs in one second.</summary>
    public const int CyclesPerSecond = 16777216;

    private static readonly List<Event> Events = [];

    private static int _untimedDepth;

    /// <summary>Where emulated time is right now. Recompiled code and memory accesses move it forward.</summary>
    public static long Cycles;

    /// <summary>When the next event is due.</summary>
    public static long NextEvent = long.MaxValue;

    /// <summary>
    /// Makes an event you can schedule. Your callback gets the cycle the event was due at, which can be
    /// a little before <see cref="Cycles"/>, so make sure to time whatever it does from there.
    /// </summary>
    public static Event CreateEvent(Action<long> callback)
    {
        var scheduledEvent = new Event(callback);
        Events.Add(scheduledEvent);
        return scheduledEvent;
    }

    /// <summary>Runs every event that's due, earliest first.</summary>
    public static void RunDueEvents()
    {
        if (_untimedDepth > 0)
        {
            return;
        }

        while (true)
        {
            Event? earliest = null;
            foreach (var candidate in Events)
            {
                if (candidate.When <= Cycles && (earliest is null || candidate.When < earliest.When))
                {
                    earliest = candidate;
                }
            }

            if (earliest is null)
            {
                break;
            }

            long dueAt = earliest.When;
            earliest.When = long.MaxValue;
            earliest.Callback(dueAt);
        }

        UpdateNextEvent();
    }

    /// <summary>
    /// Runs game code as if no time passed. Nothing scheduled runs in the meantime, and
    /// <see cref="Cycles"/> goes back to where it was afterwards. Use it whenever your own code calls
    /// the game's functions, so the game's timing stays the same.
    /// </summary>
    /// <example>
    /// <code>
    /// // Ask the game for something from a patch without throwing its timing off
    /// Scheduler.RunUntimed(() => Funcs.sub_8000400(ctx));
    /// </code>
    /// </example>
    public static void RunUntimed(Action work)
    {
        long cycles = Cycles;
        NextEvent = long.MaxValue;
        _untimedDepth++;
        try
        {
            work();
        }
        finally
        {
            Cycles = cycles;
            _untimedDepth--;
            UpdateNextEvent();
        }
    }

    /// <summary>Jumps time forward to the next event. Returns false if nothing is scheduled.</summary>
    public static bool SkipToNextEvent()
    {
        UpdateNextEvent();
        if (NextEvent == long.MaxValue)
        {
            return false;
        }

        Cycles = Math.Max(Cycles, NextEvent);
        return true;
    }

    private static void UpdateNextEvent()
    {
        if (_untimedDepth > 0)
        {
            return;
        }

        long next = long.MaxValue;
        foreach (var scheduledEvent in Events)
        {
            next = Math.Min(next, scheduledEvent.When);
        }

        NextEvent = next;
    }

    /// <summary>Something that happens at a set time. Make one with <see cref="CreateEvent"/>.</summary>
    public sealed class Event(Action<long> callback)
    {
        internal readonly Action<long> Callback = callback;

        /// <summary>The cycle the event is due at, or <see cref="long.MaxValue"/> when it isn't scheduled.</summary>
        public long When { get; internal set; } = long.MaxValue;

        /// <summary>Makes the event due <paramref name="delay"/> cycles from now.</summary>
        public void Schedule(long delay) => ScheduleAt(Cycles + delay);

        /// <summary>Makes the event due at the cycle <paramref name="when"/>.</summary>
        public void ScheduleAt(long when)
        {
            When = when;
            if (_untimedDepth == 0)
            {
                NextEvent = Math.Min(NextEvent, when);
            }
        }

        /// <summary>Unschedules the event.</summary>
        public void Cancel() => When = long.MaxValue;
    }
}
