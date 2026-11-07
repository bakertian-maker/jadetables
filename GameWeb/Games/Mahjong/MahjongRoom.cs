using GameWeb.Games.Rooms;

namespace GameWeb.Games.Mahjong;

public sealed class MjRoomSettings
{
    public int MinFaan { get; set; } = 3;
    public int Rounds { get; set; } = 1;
    /// <summary>Seconds a player has to discard; 0 = no limit.</summary>
    public int TurnSeconds { get; set; } = 30;
    /// <summary>Fill empty seats with bots and start as soon as the creator arrives.</summary>
    public bool QuickPlay { get; set; }

    public int ClaimSeconds => TurnSeconds == 0 ? 20 : 10;
}

public sealed record MjSeatView(
    int Seat, string Name, bool IsBot, bool Connected, bool Away, int Wind, bool IsDealer, int Score,
    int HandCount, IReadOnlyList<int>? Hand, IReadOnlyList<Meld> Melds, IReadOnlyList<DiscardTile> Discards, bool Ready, int BotIndex);

public sealed class MjView
{
    public required string Code { get; init; }
    public int MySeat { get; init; } = -1;
    public bool IsHost { get; init; }
    public bool Started { get; init; }
    public required MjRoomSettings Settings { get; init; }
    public required IReadOnlyList<Seat> LobbySeats { get; init; }
    public MjPhase Phase { get; init; }
    public int CurrentSeat { get; init; }
    public int Dealer { get; init; }
    public int PrevailingWind { get; init; }
    public int HandNumber { get; init; }
    public int WallCount { get; init; }
    public IReadOnlyList<MjSeatView> Seats { get; init; } = [];
    public int LastDiscard { get; init; } = -1;
    public int LastDiscarder { get; init; } = -1;
    public int? DrawnTile { get; init; }
    public TurnOptions? Turn { get; init; }
    public ClaimOptions? Claim { get; init; }
    public bool Responded { get; init; }
    public HandResult? Result { get; init; }
    public IReadOnlyList<MjLogEntry> Log { get; init; } = [];
    public int PhaseVersion { get; init; }
    /// <summary>Seconds left for the player who must act now (null if no limit).</summary>
    public double? SecondsLeft { get; init; }
    public double SecondsTotal { get; init; }
}

public sealed class MahjongRoom : Room
{
    static readonly TimeSpan BotTurnDelay = TimeSpan.FromMilliseconds(900);
    static readonly TimeSpan BotClaimDelay = TimeSpan.FromMilliseconds(700);
    static readonly TimeSpan HandOverAutoReady = TimeSpan.FromSeconds(45);

    MahjongGame? _game;
    int _lastPhaseVersion = -1;
    DateTime _phaseStart = DateTime.UtcNow;

    public MahjongRoom(string code, MjRoomSettings settings) : base(code, 4)
    {
        Settings = settings;
    }

    public override string GameKey => "mahjong";
    public MjRoomSettings Settings { get; }

    protected override void OnPlayerSeated(int seat)
    {
        if (Settings.QuickPlay && !Started) StartLocked();
    }

    public bool Start(string playerId)
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
        _game = new MahjongGame(new MjSettings { MinFaan = Settings.MinFaan, Rounds = Settings.Rounds });
        _game.StartMatch();
        Started = true;
        _lastPhaseVersion = -1;
    }

    public bool PlayAgain(string playerId)
    {
        lock (Sync)
        {
            if (_game is not { Phase: MjPhase.MatchOver } || playerId != HostId) return false;
            _game = new MahjongGame(new MjSettings { MinFaan = Settings.MinFaan, Rounds = Settings.Rounds });
            _game.StartMatch();
            _lastPhaseVersion = -1;
        }
        NotifyChanged();
        return true;
    }

    public bool Act(string playerId, MjAction action)
    {
        bool ok;
        lock (Sync)
        {
            int seat = Array.FindIndex(Seats, s => s.PlayerId == playerId);
            if (_game is null || seat < 0) return false;
            ok = _game.Apply(seat, action);
        }
        if (ok) NotifyChanged();
        return ok;
    }

    public int? Hint(string playerId)
    {
        lock (Sync)
        {
            int seat = Array.FindIndex(Seats, s => s.PlayerId == playerId);
            if (_game is not { Phase: MjPhase.Turn } || _game.CurrentSeat != seat) return null;
            return MahjongBot.SuggestDiscard(_game, seat);
        }
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
            case MjPhase.Turn:
            {
                int s = g.CurrentSeat;
                if (IsBotControlled(s, now))
                {
                    if (elapsed >= BotTurnDelay) changed = g.Apply(s, MahjongBot.Decide(g, s));
                }
                else if (Settings.TurnSeconds > 0 && elapsed.TotalSeconds >= Settings.TurnSeconds)
                    changed = g.Apply(s, g.AutoAction(s));
                break;
            }
            case MjPhase.Claim or MjPhase.RobKong:
            {
                var pending = g.PendingClaimSeats().ToList();
                bool timedOut = elapsed.TotalSeconds >= Settings.ClaimSeconds;
                if (elapsed < BotClaimDelay) break;
                foreach (var s in pending)
                {
                    if (g.Phase is not (MjPhase.Claim or MjPhase.RobKong)) break;
                    if (IsBotControlled(s, now)) changed |= g.Apply(s, MahjongBot.Decide(g, s));
                    else if (timedOut) changed |= g.Apply(s, g.AutoAction(s));
                }
                break;
            }
            case MjPhase.HandOver:
            {
                for (int s = 0; s < 4 && g.Phase == MjPhase.HandOver; s++)
                {
                    if (g.Ready[s]) continue;
                    bool auto = IsBotControlled(s, now) || IsAway(s) || elapsed >= HandOverAutoReady;
                    if (auto && elapsed >= TimeSpan.FromSeconds(1.5)) changed |= g.Apply(s, new MjAction(MjActionType.Ready));
                }
                break;
            }
        }
        return changed;
    }

    /// <summary>Starts the clock for a new phase the first time anyone sees it (a tick or a view).</summary>
    void SyncPhaseClock(MahjongGame g, DateTime now)
    {
        if (g.PhaseVersion == _lastPhaseVersion) return;
        _lastPhaseVersion = g.PhaseVersion;
        _phaseStart = now;
    }

    public MjView GetView(string playerId)
    {
        lock (Sync)
        {
            int me = Array.FindIndex(Seats, s => s.PlayerId == playerId);
            var lobbySeats = Seats.Select(s => new Seat
            {
                Name = s.Name, IsBot = s.IsBot, BotIndex = s.BotIndex, PlayerId = s.PlayerId is null ? null : "x", Connections = s.Connections,
            }).ToArray();
            var g = _game;
            if (g is null)
                return new MjView { Code = Code, MySeat = me, IsHost = playerId == HostId, Started = false, Settings = Settings, LobbySeats = lobbySeats };

            bool reveal = g.Phase is MjPhase.HandOver or MjPhase.MatchOver;
            var seats = new MjSeatView[4];
            for (int s = 0; s < 4; s++)
            {
                var p = g.Players[s];
                IReadOnlyList<int>? hand = s == me ? p.Hand.ToList()
                    : reveal && g.LastResult is { } r ? r.Hands[s] : null;
                seats[s] = new MjSeatView(s, Seats[s].Name, Seats[s].IsBot, Seats[s].Connected, IsAway(s),
                    g.SeatWind(s), g.Dealer == s, p.Score, p.Hand.Count, hand, p.Melds.ToList(),
                    p.Discards.Select(d => new DiscardTile(d.Tile) { Claimed = d.Claimed }).ToList(), g.Ready[s], Seats[s].BotIndex);
            }

            SyncPhaseClock(g, DateTime.UtcNow);
            double? left = null;
            double total = 0;
            var elapsed = (DateTime.UtcNow - _phaseStart).TotalSeconds;
            if (me >= 0)
            {
                if (g.Phase == MjPhase.Turn && g.CurrentSeat == me && Settings.TurnSeconds > 0)
                {
                    total = Settings.TurnSeconds;
                    left = Math.Max(0, total - elapsed);
                }
                else if (g.GetClaimOptions(me) is not null)
                {
                    total = Settings.ClaimSeconds;
                    left = Math.Max(0, total - elapsed);
                }
                else if (g.Phase == MjPhase.HandOver && !g.Ready[me])
                {
                    total = HandOverAutoReady.TotalSeconds;
                    left = Math.Max(0, total - elapsed);
                }
            }

            return new MjView
            {
                Code = Code,
                MySeat = me,
                IsHost = playerId == HostId,
                Started = true,
                Settings = Settings,
                LobbySeats = lobbySeats,
                Phase = g.Phase,
                CurrentSeat = g.CurrentSeat,
                Dealer = g.Dealer,
                PrevailingWind = g.PrevailingWind,
                HandNumber = g.HandNumber,
                WallCount = g.WallCount,
                Seats = seats,
                LastDiscard = g.LastDiscard,
                LastDiscarder = g.LastDiscarder,
                DrawnTile = me >= 0 && g.CurrentSeat == me ? g.DrawnTile : null,
                Turn = me >= 0 ? g.GetTurnOptions(me) : null,
                Claim = me >= 0 ? g.GetClaimOptions(me) : null,
                Responded = me >= 0 && g.HasResponded(me),
                Result = g.LastResult,
                Log = g.Log.TakeLast(8).ToList(),
                PhaseVersion = g.PhaseVersion,
                SecondsLeft = left,
                SecondsTotal = total,
            };
        }
    }
}
