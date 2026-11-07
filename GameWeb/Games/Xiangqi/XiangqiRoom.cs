using GameWeb.Games.Rooms;

namespace GameWeb.Games.Xiangqi;

public enum XqRedChoice { Host, Guest, Random }

public sealed class XqRoomSettings
{
    /// <summary>Strength of a computer player in an empty seat.</summary>
    public XqLevel BotLevel { get; set; } = XqLevel.Medium;
    public XqRedChoice Red { get; set; } = XqRedChoice.Host;
    /// <summary>Seconds per move; running out loses the game. 0 = no limit.</summary>
    public int MoveSeconds { get; set; } = 60;
    /// <summary>Fill the empty seat with the computer and start as soon as the creator arrives.</summary>
    public bool QuickPlay { get; set; }
}

public sealed class XqView
{
    public required string Code { get; init; }
    public required XqRoomSettings Settings { get; init; }
    public required IReadOnlyList<Seat> Seats { get; init; }
    public int MySeat { get; init; } = -1;
    public bool IsHost { get; init; }
    public bool Started { get; init; }
    public int RedSeat { get; init; }
    public XqSide? MySide { get; init; }
    public int[] Board { get; init; } = new int[90];
    public XqSide SideToMove { get; init; }
    public XqMove? LastMove { get; init; }
    /// <summary>Square of the general in check, or -1.</summary>
    public int CheckSquare { get; init; } = -1;
    /// <summary>My legal moves (from-square to target squares); empty when it is not my turn.</summary>
    public IReadOnlyDictionary<int, int[]> MyMoves { get; init; } = new Dictionary<int, int[]>();
    public IReadOnlyList<XqMoveRecord> History { get; init; } = [];
    public XqResult Result { get; init; }
    public XqEndReason Reason { get; init; }
    public int Version { get; init; }
    public int GameNumber { get; init; }
    public bool BotThinking { get; init; }
    public double? SecondsLeft { get; init; }
    public double SecondsTotal { get; init; }

    public bool IsOver => Result != XqResult.Ongoing;
    public bool MyTurn => !IsOver && Started && MySide == SideToMove;
    public int SeatOf(XqSide side) => side == XqSide.Red ? RedSeat : 1 - RedSeat;
    public XqSide SideOfSeat(int seat) => seat == RedSeat ? XqSide.Red : XqSide.Black;
}

/// <summary>A two-seat Xiangqi room: a player against a friend or the computer.</summary>
public sealed class XiangqiRoom : Room
{
    static readonly TimeSpan BotMinimumDelay = TimeSpan.FromMilliseconds(500);

    XqGame? _game;
    int _redSeat;
    int _gameNumber;
    int _clockVersion = -1;
    DateTime _turnStart;
    DateTime _lastTick = DateTime.UtcNow;
    bool _botThinking;

    public XiangqiRoom(string code, XqRoomSettings settings) : base(code, 2)
    {
        Settings = settings;
    }

    public override string GameKey => "xiangqi";
    public XqRoomSettings Settings { get; }

    public override bool IsFinished
    {
        get { lock (Sync) return _game is { IsOver: true }; }
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
        int hostSeat = Math.Max(0, Array.FindIndex(Seats, s => s.PlayerId == HostId));
        _redSeat = Settings.Red switch
        {
            XqRedChoice.Host => hostSeat,
            XqRedChoice.Guest => 1 - hostSeat,
            _ => Random.Shared.Next(2),
        };
        NewGame();
        Started = true;
    }

    void NewGame()
    {
        _game = new XqGame();
        _gameNumber++;
        _clockVersion = -1;
        _botThinking = false;
    }

    XqSide SideOfSeat(int seat) => seat == _redSeat ? XqSide.Red : XqSide.Black;
    int SeatOfSide(XqSide side) => side == XqSide.Red ? _redSeat : 1 - _redSeat;
    int SeatOfPlayer(string playerId) => Array.FindIndex(Seats, s => s.PlayerId == playerId);

    public bool Move(string playerId, XqMove move)
    {
        bool ok;
        lock (Sync)
        {
            int seat = SeatOfPlayer(playerId);
            ok = _game is { IsOver: false } g && seat >= 0 && SideOfSeat(seat) == g.SideToMove && g.TryMove(move);
        }
        if (ok) NotifyChanged();
        return ok;
    }

    public bool Resign(string playerId)
    {
        lock (Sync)
        {
            int seat = SeatOfPlayer(playerId);
            if (_game is not { IsOver: false } || seat < 0) return false;
            _game.Resign(SideOfSeat(seat));
        }
        NotifyChanged();
        return true;
    }

    /// <summary>After a game ends, either player starts a new one with the sides swapped.</summary>
    public bool Rematch(string playerId)
    {
        lock (Sync)
        {
            if (_game is not { IsOver: true } || SeatOfPlayer(playerId) < 0) return false;
            _redSeat = 1 - _redSeat;
            NewGame();
        }
        NotifyChanged();
        return true;
    }

    /// <summary>A suggested move for a beginner, from the Easy engine. Null when it is not their turn.</summary>
    public XqMove? Hint(string playerId)
    {
        XqPosition position;
        lock (Sync)
        {
            int seat = SeatOfPlayer(playerId);
            if (_game is not { IsOver: false } g || seat < 0 || SideOfSeat(seat) != g.SideToMove) return null;
            position = g.Position.Clone();
        }
        return XqBot.ChooseMove(position, XqLevel.Easy, budget: TimeSpan.FromMilliseconds(250));
    }

    protected override bool Tick(DateTime now)
    {
        var g = _game;
        // Ticks pause while nobody is connected; don't count that pause against the player to move.
        var gap = now - _lastTick;
        _lastTick = now;
        if (gap > TimeSpan.FromSeconds(2)) _turnStart += gap;

        if (g is null || g.IsOver) return false;
        SyncClock(g, now);
        int seat = SeatOfSide(g.SideToMove);
        if (Seats[seat].IsBot)
        {
            if (!_botThinking && now - _turnStart >= BotMinimumDelay) StartBotMove(g);
            return false;
        }
        if (Settings.MoveSeconds > 0 && (now - _turnStart).TotalSeconds >= Settings.MoveSeconds)
        {
            g.LoseOnTime(g.SideToMove);
            return true;
        }
        return false;
    }

    void SyncClock(XqGame g, DateTime now)
    {
        if (g.Version == _clockVersion) return;
        _clockVersion = g.Version;
        _turnStart = now;
    }

    /// <summary>Searches on a background task, so a slow move never holds the room lock.</summary>
    void StartBotMove(XqGame g)
    {
        _botThinking = true;
        var position = g.Position.Clone();
        int version = g.Version;
        var level = Settings.BotLevel;
        Task.Run(() => XqBot.ChooseMove(position, level)).ContinueWith(t =>
        {
            lock (Sync)
            {
                if (_game != g) return;
                _botThinking = false;
                if (t.IsCompletedSuccessfully && g.Version == version && !g.IsOver) g.TryMove(t.Result);
            }
            NotifyChanged();
        }, TaskScheduler.Default);
    }

    public XqView GetView(string playerId)
    {
        lock (Sync)
        {
            int me = SeatOfPlayer(playerId);
            var seats = SeatSnapshot();
            var g = _game;
            if (g is null)
                return new XqView { Code = Code, Settings = Settings, Seats = seats, MySeat = me, IsHost = playerId == HostId };

            var now = DateTime.UtcNow;
            SyncClock(g, now);
            XqSide? mySide = me >= 0 ? SideOfSeat(me) : null;
            bool myTurn = !g.IsOver && mySide == g.SideToMove;
            var myMoves = myTurn
                ? g.LegalMoves().GroupBy(m => m.From).ToDictionary(x => x.Key, x => x.Select(m => m.To).ToArray())
                : new Dictionary<int, int[]>();

            double? left = null;
            double total = 0;
            if (!g.IsOver && Settings.MoveSeconds > 0 && !Seats[SeatOfSide(g.SideToMove)].IsBot)
            {
                total = Settings.MoveSeconds;
                left = Math.Max(0, total - (now - _turnStart).TotalSeconds);
            }

            return new XqView
            {
                Code = Code,
                Settings = Settings,
                Seats = seats,
                MySeat = me,
                IsHost = playerId == HostId,
                Started = true,
                RedSeat = _redSeat,
                MySide = mySide,
                Board = g.Position.BoardCopy(),
                SideToMove = g.SideToMove,
                LastMove = g.History.Count > 0 ? g.History[^1].Move : null,
                CheckSquare = !g.IsOver && g.InCheck ? g.Position.GeneralSquare(g.SideToMove) : -1,
                MyMoves = myMoves,
                History = g.History.ToList(),
                Result = g.Result,
                Reason = g.Reason,
                Version = g.Version,
                GameNumber = _gameNumber,
                BotThinking = _botThinking,
                SecondsLeft = left,
                SecondsTotal = total,
            };
        }
    }
}
