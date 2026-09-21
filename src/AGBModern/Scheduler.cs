namespace AGBModern;

/// <summary>
/// <c>Scheduler</c> keeps track of time on the GBA, which is counted in CPU cycles. Anything that happens by itself at
/// a set time, like a new line starting or a timer running out, is an event here.
/// </summary>
public static class Scheduler
{
    /// <summary>How many cycles the GBA runs in one second.</summary>
    public const int CyclesPerSecond = 16777216;

    private static readonly List<Event> Events = [];

    private static int _untimedDepth;

    /// <summary>How many cycles the GBA has run so far. If you want to turn it into seconds, use <see cref="CyclesPerSecond"/>.</summary>
    public static long Cycles;

    /// <summary>The cycle that the next event is due at.</summary>
    public static long NextEvent = long.MaxValue;

    /// <summary>
    /// Creates an event that you can schedule. Your callback gets the cycle that the event was due at, which can be a
    /// little before <see cref="Cycles"/>, so make sure you time whatever it does from there.
    /// </summary>
    public static Event CreateEvent(Action<long> callback)
    {
        var scheduledEvent = new Event(callback);
        Events.Add(scheduledEvent);
        return scheduledEvent;
    }

    /// <summary>Runs every event that's due, starting with the earliest one.</summary>
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
    /// Runs your code as if no time has passed at all. Usually you want this whenever a patch of yours calls one of the
    /// game's functions, so the game keeps exactly the same timing it would have without your patch.
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

    /// <summary>Jumps time forward to the next event. Returns <c>false</c> if nothing is scheduled.</summary>
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

    /// <summary>Something that happens at a set time. You can create one with <see cref="CreateEvent"/>.</summary>
    public sealed class Event(Action<long> callback)
    {
        internal readonly Action<long> Callback = callback;

        /// <summary>The cycle that the event is due at, or <see cref="long.MaxValue"/> if it isn't scheduled.</summary>
        public long When { get; internal set; } = long.MaxValue;

        /// <summary>Makes the event happen <paramref name="delay"/> cycles from now.</summary>
        public void Schedule(long delay) => ScheduleAt(Cycles + delay);

        /// <summary>Makes the event happen at the cycle <paramref name="when"/>.</summary>
        public void ScheduleAt(long when)
        {
            When = when;
            if (_untimedDepth == 0)
            {
                NextEvent = Math.Min(NextEvent, when);
            }
        }

        /// <summary>Takes the event off the schedule, so it won't happen until you schedule it again.</summary>
        public void Cancel() => When = long.MaxValue;
    }
}
