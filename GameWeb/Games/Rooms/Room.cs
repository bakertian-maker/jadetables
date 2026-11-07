namespace GameWeb.Games.Rooms;

public sealed class Seat
{
    public string? PlayerId { get; set; }
    public string Name { get; set; } = "";
    public bool IsBot { get; set; }
    /// <summary>Which of the built-in bot personas sits here (index into Room.BotNames).</summary>
    public int BotIndex { get; set; } = -1;
    public int Connections { get; set; }
    public DateTime? DisconnectedAt { get; set; }
    public bool IsEmpty => PlayerId is null && !IsBot;
    public bool IsHuman => PlayerId is not null;
    public bool Connected => IsBot || Connections > 0;
}

public enum JoinOutcome { Seated, Rejoined, Spectating, Full }

/// <summary>
/// Shared room logic for every game: seats, host, connections, bots in empty seats, idle tracking.
/// Game-specific rooms derive from this and run their own rules on a server-side tick.
/// </summary>
public abstract class Room : IDisposable
{
    /// <summary>How long a disconnected player keeps their seat before a bot plays for them.</summary>
    public static readonly TimeSpan ReconnectGrace = TimeSpan.FromSeconds(60);

    /// <summary>Bot surnames; the UI shows them as "Bot Lin" or "电脑·林".</summary>
    public static readonly string[] BotNames = ["Lin", "Chan", "Wong", "Ho", "Lau", "Cheung"];

    readonly Timer _timer;
    protected readonly object Sync = new();

    protected Room(string code, int seatCount)
    {
        Code = code;
        Seats = Enumerable.Range(0, seatCount).Select(_ => new Seat()).ToArray();
        LastActivity = DateTime.UtcNow;
        _timer = new Timer(_ => SafeTick(), null, 200, 200);
    }

    public string Code { get; }
    public abstract string GameKey { get; }

    /// <summary>The room's page, e.g. /mahjong/room/K7Q2M.</summary>
    public string Url => $"/{GameKey}/room/{Code}";
    public Seat[] Seats { get; }
    public string? HostId { get; protected set; }
    public bool Started { get; protected set; }
    public DateTime LastActivity { get; protected set; }
    /// <summary>True once any player has taken a seat (rooms nobody joins are closed sooner).</summary>
    public bool EverJoined { get; private set; }

    /// <summary>Raised (outside the lock) whenever anything visible changes.</summary>
    public event Action? Changed;

    protected void NotifyChanged() => Changed?.Invoke();

    /// <summary>True if this player (by their browser identity) holds a seat here.</summary>
    public bool HasSeat(string playerId)
    {
        lock (Sync) return Seats.Any(s => s.PlayerId == playerId);
    }

    /// <summary>True once the match has ended, so the room is no longer worth returning to.</summary>
    public virtual bool IsFinished => false;

    public bool HasConnectedHumans
    {
        get { lock (Sync) return Seats.Any(s => s.IsHuman && s.Connections > 0); }
    }

    /// <summary>True if a bot should act for this seat (a bot, or a player away past the grace period).</summary>
    protected bool IsBotControlled(int seat, DateTime now)
    {
        var s = Seats[seat];
        if (s.IsBot) return true;
        return s.Connections == 0 && s.DisconnectedAt is DateTime d && now - d > ReconnectGrace;
    }

    protected bool IsAway(int seat) => Seats[seat] is { IsHuman: true, Connections: 0 };

    public JoinOutcome Connect(string playerId, string name)
    {
        JoinOutcome outcome;
        lock (Sync)
        {
            name = CleanName(name);
            int existing = Array.FindIndex(Seats, s => s.PlayerId == playerId);
            if (existing >= 0)
            {
                var s = Seats[existing];
                s.Connections++;
                s.DisconnectedAt = null;
                if (!Started) s.Name = name;
                outcome = JoinOutcome.Rejoined;
            }
            else if (!Started && Array.FindIndex(Seats, s => s.IsEmpty) is var free and >= 0)
            {
                Seats[free] = new Seat { PlayerId = playerId, Name = UniqueName(name), Connections = 1 };
                HostId ??= playerId;
                EverJoined = true;
                outcome = JoinOutcome.Seated;
                OnPlayerSeated(free);
            }
            else outcome = Started ? JoinOutcome.Spectating : JoinOutcome.Full;
        }
        NotifyChanged();
        return outcome;
    }

    public void Disconnect(string playerId)
    {
        lock (Sync)
        {
            int i = Array.FindIndex(Seats, s => s.PlayerId == playerId);
            if (i < 0) return;
            var s = Seats[i];
            s.Connections = Math.Max(0, s.Connections - 1);
            if (s.Connections == 0) s.DisconnectedAt = DateTime.UtcNow;
        }
        NotifyChanged();
    }

    public bool AddBot(string playerId)
    {
        lock (Sync)
        {
            if (Started || playerId != HostId) return false;
            int free = Array.FindIndex(Seats, s => s.IsEmpty);
            if (free < 0) return false;
            FillBot(free);
        }
        NotifyChanged();
        return true;
    }

    public bool RemoveSeat(string playerId, int seat)
    {
        lock (Sync)
        {
            if (Started || playerId != HostId || seat < 0 || seat >= Seats.Length) return false;
            if (Seats[seat].PlayerId == HostId) return false;
            Seats[seat] = new Seat();
        }
        NotifyChanged();
        return true;
    }

    protected void FillBot(int seat)
    {
        var used = Seats.Where(s => s.IsBot).Select(s => s.BotIndex).ToHashSet();
        int index = Enumerable.Range(0, BotNames.Length).First(i => !used.Contains(i));
        Seats[seat] = new Seat { IsBot = true, BotIndex = index, Name = "Bot " + BotNames[index] };
    }

    protected void FillEmptySeatsWithBots()
    {
        for (int i = 0; i < Seats.Length; i++)
            if (Seats[i].IsEmpty) FillBot(i);
    }

    /// <summary>Hands the host role to another seated player if the host has been gone too long.</summary>
    /// <summary>
    /// Frees waiting-room seats of players gone past the grace period, and hands the host role
    /// to another connected player if the host has gone. Returns true if anything changed.
    /// </summary>
    protected bool MaintainSeats(DateTime now)
    {
        bool changed = false;
        if (!Started)
        {
            for (int i = 0; i < Seats.Length; i++)
                if (Seats[i].IsHuman && Seats[i].Connections == 0 && Seats[i].DisconnectedAt is DateTime d && now - d > ReconnectGrace)
                {
                    Seats[i] = new Seat();
                    changed = true;
                }
        }
        var host = Seats.FirstOrDefault(s => s.PlayerId == HostId);
        bool hostGone = host is null || (host.Connections == 0 && host.DisconnectedAt is DateTime hd && now - hd > ReconnectGrace);
        if (hostGone && Seats.FirstOrDefault(s => s.IsHuman && s.Connections > 0) is { } next && next.PlayerId != HostId)
        {
            HostId = next.PlayerId;
            changed = true;
        }
        return changed;
    }

    string UniqueName(string name)
    {
        var used = Seats.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(name)) return name;
        for (int n = 2; ; n++)
            if (!used.Contains($"{name} {n}")) return $"{name} {n}";
    }

    public static string CleanName(string? name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) name = "Guest";
        return name.Length > 20 ? name[..20] : name;
    }

    protected virtual void OnPlayerSeated(int seat) { }

    void SafeTick()
    {
        bool changed;
        try
        {
            lock (Sync)
            {
                var now = DateTime.UtcNow;
                if (!Seats.Any(s => s.IsHuman && s.Connections > 0)) return; // pause while nobody is here
                LastActivity = now;
                changed = MaintainSeats(now);
                changed |= Tick(now);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Room {Code} tick failed: {ex}");
            return;
        }
        if (changed) NotifyChanged();
    }

    /// <summary>Called every 200 ms under the room lock. Return true if anything changed.</summary>
    protected abstract bool Tick(DateTime now);

    /// <summary>Starts the game (host only). Empty seats are filled with computer players.</summary>
    public abstract bool Start(string playerId);

    /// <summary>
    /// Copies of the seats for display. Player ids are replaced with a placeholder,
    /// because a player id is that player's secret.
    /// </summary>
    protected Seat[] SeatSnapshot() => Seats.Select(s => new Seat
    {
        Name = s.Name, IsBot = s.IsBot, BotIndex = s.BotIndex,
        PlayerId = s.PlayerId is null ? null : "x", Connections = s.Connections,
    }).ToArray();

    public void Dispose()
    {
        _timer.Dispose();
        GC.SuppressFinalize(this);
    }
}
