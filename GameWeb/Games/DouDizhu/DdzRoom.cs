using System.Text.Json;
using GameWeb.Games.Archive;
using GameWeb.Games.Rooms;

namespace GameWeb.Games.DouDizhu;

public sealed class DdzRoomSettings
{
    /// <summary>Hands in a match.</summary>
    public int Hands { get; set; } = 6;
    /// <summary>Seconds a player has to bid or play; 0 = no limit.</summary>
    public int TurnSeconds { get; set; } = 30;
    /// <summary>Fill empty seats with computer players and start as soon as the creator arrives.</summary>
    public bool QuickPlay { get; set; }
    /// <summary>How well the computer players play.</summary>
    public DdzLevel BotLevel { get; set; } = DdzLevel.Medium;
}

/// <summary>One seat as a viewer sees it. <paramref name="Hand"/> is only filled in for the viewer (and for
/// everyone once a hand is over).</summary>
public sealed record DdzSeatView(
    int Seat, string Name, bool IsBot, int BotIndex, bool Connected, bool Away, int Score,
    int CardCount, IReadOnlyList<int>? Hand, bool IsLandlord, int? Bid, TrickAction? Trick, bool Ready);

public sealed class DdzView
{
    public required string Code { get; init; }
    public int MySeat { get; init; } = -1;
    public bool IsHost { get; init; }
    public bool Started { get; init; }
    public required DdzRoomSettings Settings { get; init; }
    public required IReadOnlyList<Seat> LobbySeats { get; init; }
    public DdzPhase Phase { get; init; }
    public int HandNumber { get; init; }
    public int CurrentSeat { get; init; }
    public int Landlord { get; init; } = -1;
    public int Bid { get; init; }
    public int Multiplier { get; init; } = 1;
    /// <summary>The landlord's three cards: hidden (null) while bidding.</summary>
    public IReadOnlyList<int>? Bottom { get; init; }
    public IReadOnlyList<DdzSeatView> Seats { get; init; } = [];
    /// <summary>The play to beat (null when the current player starts a trick).</summary>
    public DdzCombo? LastCombo { get; init; }
    public int LastSeat { get; init; } = -1;
    public IReadOnlyList<int> AllowedBids { get; init; } = [];
    /// <summary>Every card played so far this hand: public knowledge, used by the card counter.</summary>
    public IReadOnlyList<int> Played { get; init; } = [];
    public DdzResult? Result { get; init; }
    public IReadOnlyList<DdzLogEntry> Log { get; init; } = [];
    public int PhaseVersion { get; init; }
    public double? SecondsLeft { get; init; }
    public double SecondsTotal { get; init; }

    public DdzSeatView? Me => MySeat >= 0 ? Seats[MySeat] : null;
    public bool MyTurn => MySeat >= 0 && CurrentSeat == MySeat && Phase is DdzPhase.Bidding or DdzPhase.Playing;
    public bool Leading => MyTurn && Phase == DdzPhase.Playing && LastCombo is null;
}

/// <summary>A three-seat Dou Dizhu room: friends, computer players, or both.</summary>
public sealed class DdzRoom : Room
{
    /// <summary>The computer waits this long, so players can follow what it does.</summary>
    static readonly TimeSpan BotTurnDelay = TimeSpan.FromMilliseconds(1100);
    static readonly TimeSpan HandOverAutoReady = TimeSpan.FromSeconds(45);

    DdzGame? _game;
    int _lastPhaseVersion = -1;
    DateTime _phaseStart = DateTime.UtcNow;
    DateTime _matchStartedAt;
    bool _matchArchived;

    public DdzRoom(string code, DdzRoomSettings settings) : base(code, DdzGame.Players)
    {
        Settings = settings;
    }

    public override string GameKey => "doudizhu";
    public DdzRoomSettings Settings { get; }

    public override bool IsFinished
    {
        get { lock (Sync) return _game is { Phase: DdzPhase.MatchOver }; }
    }

    protected override void OnPlayerSeated(int seat)
    {
        if (Settings.QuickPlay && !Started) StartLocked();
    }

    public override bool Start(string playerId)
    {
        lock (Sync)
        {
            if (Started || playerId != HostId) return false;
            StartLocked();
        }
        NotifyChanged();
        return true;
    }

    void StartLocked()
    {
        FillEmptySeatsWithBots();
        NewMatch();
        Started = true;
    }

    void NewMatch()
    {
        _game = new DdzGame(new DdzSettings { Hands = Settings.Hands });
        _game.StartMatch();
        _matchStartedAt = DateTime.UtcNow;
        _matchArchived = false;
        _lastPhaseVersion = -1;
    }

    /// <summary>Queues the match for saving the first time it is seen finished. Call under the lock.</summary>
    void ArchiveIfOver()
    {
        if (_game is not { Phase: DdzPhase.MatchOver } g || _matchArchived) return;
        _matchArchived = true;
        var players = Enumerable.Range(0, DdzGame.Players).Select(seat => new FinishedPlayer(
            seat, null, Seats[seat].PlayerId, Seats[seat].IsBot,
            Seats[seat].IsBot ? (byte)Settings.BotLevel : null, g.Scores[seat])).ToList();
        string details = JsonSerializer.Serialize(new { hands = Settings.Hands, level = Settings.BotLevel.ToString() });
        Archive.Save(new FinishedGame(
            GameType.DouDizhu, Code, _matchStartedAt, DateTime.UtcNow,
            Result: 1, Reason: 0, g.HandNumber, Moves: null, details, players, BookMoves: []));
    }

    public bool PlayAgain(string playerId)
    {
        lock (Sync)
        {
            if (_game is not { Phase: DdzPhase.MatchOver } || Array.FindIndex(Seats, s => s.PlayerId == playerId) < 0) return false;
            NewMatch();
        }
        NotifyChanged();
        return true;
    }

    public bool Act(string playerId, DdzAction action)
    {
        bool ok;
        lock (Sync)
        {
            int seat = Array.FindIndex(Seats, s => s.PlayerId == playerId);
            if (_game is null || seat < 0) return false;
            ok = _game.Apply(seat, action);
            if (ok) ArchiveIfOver();
        }
        if (ok) NotifyChanged();
        return ok;
    }

    protected override bool Tick(DateTime now)
    {
        var g = _game;
        if (g is null) return false;
        SyncPhaseClock(g, now);
        var elapsed = now - _phaseStart;
        bool changed = false;

        switch (g.Phase)
        {
            case DdzPhase.Bidding:
            case DdzPhase.Playing:
            {
                int s = g.CurrentSeat;
                if (IsBotControlled(s, now))
                {
                    if (elapsed >= BotTurnDelay) changed = g.Apply(s, DdzBot.Decide(g, s, Settings.BotLevel));
                }
                else if (Settings.TurnSeconds > 0 && elapsed.TotalSeconds >= Settings.TurnSeconds)
                    changed = g.Apply(s, g.AutoAction(s));
                break;
            }
            case DdzPhase.HandOver:
                for (int s = 0; s < DdzGame.Players && g.Phase == DdzPhase.HandOver; s++)
                {
                    if (g.Ready[s]) continue;
                    bool auto = IsBotControlled(s, now) || IsAway(s) || elapsed >= HandOverAutoReady;
                    if (auto && elapsed >= TimeSpan.FromSeconds(1.5)) changed |= g.Apply(s, new DdzAction(DdzActionType.Ready));
                }
                break;
        }
        if (changed) ArchiveIfOver();
        return changed;
    }

    void SyncPhaseClock(DdzGame g, DateTime now)
    {
        if (g.PhaseVersion == _lastPhaseVersion) return;
        _lastPhaseVersion = g.PhaseVersion;
        _phaseStart = now;
    }

    public DdzView GetView(string playerId)
    {
        lock (Sync)
        {
            int me = Array.FindIndex(Seats, s => s.PlayerId == playerId);
            var lobbySeats = SeatSnapshot();
            var g = _game;
            if (g is null)
                return new DdzView { Code = Code, MySeat = me, IsHost = playerId == HostId, Settings = Settings, LobbySeats = lobbySeats };

            bool reveal = g.Phase is DdzPhase.HandOver or DdzPhase.MatchOver;
            var seats = new DdzSeatView[DdzGame.Players];
            for (int s = 0; s < DdzGame.Players; s++)
            {
                IReadOnlyList<int>? hand = s == me ? g.Hands[s].ToList()
                    : reveal && g.LastResult is { } r ? r.Hands[s] : null;
                seats[s] = new DdzSeatView(s, Seats[s].Name, Seats[s].IsBot, Seats[s].BotIndex, Seats[s].Connected, IsAway(s),
                    g.Scores[s], g.Hands[s].Count, hand, g.Landlord == s, g.Bids[s], g.Trick[s], g.Ready[s]);
            }

            var now = Clock();
            SyncPhaseClock(g, now);
            double? left = null;
            double total = 0;
            var elapsed = (now - _phaseStart).TotalSeconds;
            if (me >= 0)
            {
                if (g.Phase is DdzPhase.Bidding or DdzPhase.Playing && g.CurrentSeat == me && Settings.TurnSeconds > 0)
                {
                    total = Settings.TurnSeconds;
                    left = Math.Max(0, total - elapsed);
                }
                else if (g.Phase == DdzPhase.HandOver && !g.Ready[me])
                {
                    total = HandOverAutoReady.TotalSeconds;
                    left = Math.Max(0, total - elapsed);
                }
            }

            return new DdzView
            {
                Code = Code,
                MySeat = me,
                IsHost = playerId == HostId,
                Started = true,
                Settings = Settings,
                LobbySeats = lobbySeats,
                Phase = g.Phase,
                HandNumber = g.HandNumber,
                CurrentSeat = g.CurrentSeat,
                Landlord = g.Landlord,
                Bid = g.Bid,
                Multiplier = g.Multiplier,
                Bottom = g.Phase == DdzPhase.Bidding ? null : g.Bottom.ToList(),
                Seats = seats,
                LastCombo = g.Phase == DdzPhase.Playing ? g.LastCombo : null,
                LastSeat = g.LastSeat,
                AllowedBids = me >= 0 ? g.AllowedBids(me).ToList() : [],
                Played = g.Played.ToList(),
                Result = g.LastResult,
                Log = g.Log.TakeLast(10).ToList(),
                PhaseVersion = g.PhaseVersion,
                SecondsLeft = left,
                SecondsTotal = total,
            };
        }
    }
}
