using GameWeb.Games.Archive;
using GameWeb.Games.Rooms;

namespace GameWeb.Games.Xiangqi;

public enum XqRedChoice { Host, Guest, Random }

public sealed class XqRoomSettings
{
    /// <summary>Strength of a computer player in an empty seat.</summary>
    public XqLevel BotLevel { get; set; } = XqLevel.Medium;
    public XqRedChoice Red { get; set; } = XqRedChoice.Host;
    /// <summary>Seconds per move; running out loses the game. 0 = no limit. Ignored when there is a game clock.</summary>
    public int MoveSeconds { get; set; } = 60;
    /// <summary>Game clock: minutes each for the whole game; running out loses. 0 = no game clock.</summary>
    public int ClockMinutes { get; set; }
    /// <summary>Seconds added to a player's clock after each of their moves.</summary>
    public int IncrementSeconds { get; set; }
    public bool HasClock => ClockMinutes > 0;
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
    /// <summary>Game clocks in seconds (when <see cref="XqRoomSettings.HasClock"/>), as of <see cref="ClockAt"/>.</summary>
    public double RedClock { get; init; }
    public double BlackClock { get; init; }
    public DateTime ClockAt { get; init; }
    /// <summary>The side that has offered a draw and is waiting for an answer.</summary>
    public XqSide? DrawOfferBy { get; init; }
    /// <summary>My last draw offer was turned down (shown until my next move).</summary>
    public bool MyDrawDeclined { get; init; }
    public bool CanOfferDraw { get; init; }

    public double ClockOf(XqSide side) => side == XqSide.Red ? RedClock : BlackClock;

    public bool IsOver => Result != XqResult.Ongoing;
    public bool MyTurn => !IsOver && Started && MySide == SideToMove;
    public int SeatOf(XqSide side) => side == XqSide.Red ? RedSeat : 1 - RedSeat;
    public XqSide SideOfSeat(int seat) => seat == RedSeat ? XqSide.Red : XqSide.Black;
}

/// <summary>A two-seat Xiangqi room: a player against a friend or the computer.</summary>
public sealed class XiangqiRoom : Room
{
    /// <summary>
    /// The computer waits at least this long before moving, so the player sees their own move land (the
    /// board slides each piece in over about half a second) before the reply arrives.
    /// </summary>
    static readonly TimeSpan BotMinimumDelay = TimeSpan.FromMilliseconds(1200);
    /// <summary>Half-moves a side must wait after offering a draw before offering again.</summary>
    public const int DrawOfferGap = 10;

    XqGame? _game;
    int _redSeat;
    int _gameNumber;
    int _clockVersion = -1;
    DateTime _turnStart;
    DateTime _lastTick = DateTime.UtcNow;
    bool _botThinking;
    DateTime _gameStartedAt;
    int _archivedGame;
    readonly TimeSpan[] _clock = new TimeSpan[2];
    readonly int[] _lastOfferPly = new int[2];
    XqSide? _drawOffer;
    XqSide? _drawDeclined;

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
        _gameStartedAt = DateTime.UtcNow;
        _clockVersion = -1;
        _botThinking = false;
        _clock[0] = _clock[1] = TimeSpan.FromMinutes(Settings.ClockMinutes);
        _lastOfferPly[0] = _lastOfferPly[1] = -DrawOfferGap;
        _drawOffer = _drawDeclined = null;
    }

    static int Index(XqSide side) => side == XqSide.Red ? 0 : 1;

    /// <summary>Time the side to move has left (game clock or move timer); null when untimed.</summary>
    TimeSpan? TimeLeft(XqGame g, DateTime now)
    {
        var used = now - _turnStart;
        if (Settings.HasClock) return _clock[Index(g.SideToMove)] - used;
        if (Settings.MoveSeconds > 0) return TimeSpan.FromSeconds(Settings.MoveSeconds) - used;
        return null;
    }

    /// <summary>Makes a move for the side to move, with the clock and draw-offer bookkeeping. Call under the lock.</summary>
    bool PlayMove(XqGame g, XqMove move, DateTime now)
    {
        SyncClock(g, now);
        var mover = g.SideToMove;
        if (!g.LegalMoves().Contains(move)) return false;
        bool human = !Seats[SeatOfSide(mover)].IsBot;
        if (human && TimeLeft(g, now) <= TimeSpan.Zero)
        {
            // The flag fell between ticks: the move came too late.
            g.LoseOnTime(mover);
            ArchiveIfOver();
            return true;
        }
        g.TryMove(move);
        if (Settings.HasClock)
        {
            int i = Index(mover);
            var left = _clock[i] - (now - _turnStart);
            _clock[i] = (left < TimeSpan.Zero ? TimeSpan.Zero : left) + TimeSpan.FromSeconds(Settings.IncrementSeconds);
        }
        _turnStart = now;
        _clockVersion = g.Version;
        // Moving instead of answering turns a draw offer down; a side's "declined" notice goes once it moves on.
        if (_drawOffer is XqSide offerer && offerer != mover)
        {
            _drawDeclined = offerer;
            _drawOffer = null;
        }
        if (_drawDeclined == mover) _drawDeclined = null;
        ArchiveIfOver();
        return true;
    }

    bool CanOffer(XqGame g, XqSide side) =>
        !g.IsOver && _drawOffer is null && g.History.Count - _lastOfferPly[Index(side)] >= DrawOfferGap;

    /// <summary>Offers a draw. A computer opponent answers after a short think; a person answers with <see cref="AnswerDraw"/>.</summary>
    public bool OfferDraw(string playerId)
    {
        lock (Sync)
        {
            int seat = SeatOfPlayer(playerId);
            if (_game is not { } g || seat < 0) return false;
            var side = SideOfSeat(seat);
            if (!CanOffer(g, side)) return false;
            _drawOffer = side;
            _drawDeclined = null;
            _lastOfferPly[Index(side)] = g.History.Count;
            if (Seats[1 - seat].IsBot) StartBotDrawReply(g, side);
        }
        NotifyChanged();
        return true;
    }

    /// <summary>The opponent of the side that offered accepts (the game is drawn) or declines.</summary>
    public bool AnswerDraw(string playerId, bool accept)
    {
        lock (Sync)
        {
            int seat = SeatOfPlayer(playerId);
            if (_game is not { IsOver: false } g || seat < 0 || _drawOffer is not XqSide offerer || SideOfSeat(seat) == offerer)
                return false;
            Answer(g, offerer, accept);
        }
        NotifyChanged();
        return true;
    }

    void Answer(XqGame g, XqSide offerer, bool accept)
    {
        _drawOffer = null;
        if (accept)
        {
            g.AgreeDraw();
            ArchiveIfOver();
        }
        else _drawDeclined = offerer;
    }

    /// <summary>The computer looks at the position briefly and takes the draw only if it isn't doing well.</summary>
    void StartBotDrawReply(XqGame g, XqSide offerer)
    {
        var position = g.Position.Clone();
        var context = XqBot.XqGameContext.Of(g);
        int plies = g.History.Count;
        var bot = Pieces.Other(offerer);
        Task.Run(async () =>
        {
            await Task.Delay(BotDrawReplyDelay);   // a moment to "think", so the answer doesn't look automatic
            if (position.LegalMoves().Count == 0) return false;
            var a = XqBot.Analyse(position, XqLevel.Medium, budget: TimeSpan.FromMilliseconds(300), context: context);
            return XqBot.AcceptsDraw(position.SideToMove == bot ? a.Score : -a.Score, plies);
        }).ContinueWith(t =>
        {
            lock (Sync)
            {
                if (_game != g || g.IsOver || _drawOffer != offerer) return;
                Answer(g, offerer, t.IsCompletedSuccessfully && t.Result);
            }
            NotifyChanged();
        }, TaskScheduler.Default);
    }

    /// <summary>How long the computer waits before answering a draw offer (tests shorten it).</summary>
    internal TimeSpan BotDrawReplyDelay { get; set; } = TimeSpan.FromMilliseconds(800);

    /// <summary>Queues the game for saving the first time it is seen finished. Call under the lock.</summary>
    void ArchiveIfOver()
    {
        if (_game is not { IsOver: true } g || _archivedGame == _gameNumber) return;
        _archivedGame = _gameNumber;
        var players = Enumerable.Range(0, Seats.Length).Select(seat => new FinishedPlayer(
            seat,
            (byte)SideOfSeat(seat),
            Seats[seat].PlayerId,
            Seats[seat].IsBot,
            Seats[seat].IsBot ? (byte)Settings.BotLevel : null,
            null)).ToList();
        // Book moves come from people and the Difficult computer only; weaker bots would teach bad habits.
        bool Trusted(XqSide side)
        {
            var s = Seats[SeatOfSide(side)];
            return !s.IsBot || Settings.BotLevel == XqLevel.Difficult;
        }
        Archive.Save(new FinishedGame(
            GameType.Xiangqi, Code, _gameStartedAt, DateTime.UtcNow,
            (byte)g.Result, (byte)g.Reason, g.History.Count,
            string.Join(' ', g.History.Select(h => h.Move.ToString())),
            null, players, XqBook.Entries(g, Trusted)));
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
            ok = _game is { IsOver: false } g && seat >= 0 && SideOfSeat(seat) == g.SideToMove && PlayMove(g, move, Clock());
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
            ArchiveIfOver();
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

    /// <summary>A suggested move, from the Medium engine with a short think. Null when it is not their turn.</summary>
    public XqMove? Hint(string playerId)
    {
        XqPosition position;
        XqBot.XqGameContext context;
        lock (Sync)
        {
            int seat = SeatOfPlayer(playerId);
            if (_game is not { IsOver: false } g || seat < 0 || SideOfSeat(seat) != g.SideToMove) return null;
            position = g.Position.Clone();
            context = XqBot.XqGameContext.Of(g);
        }
        return XqBot.ChooseMove(position, XqLevel.Medium, budget: TimeSpan.FromMilliseconds(400), context: context);
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
        if (TimeLeft(g, now) <= TimeSpan.Zero)
        {
            g.LoseOnTime(g.SideToMove);
            ArchiveIfOver();
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
        var context = XqBot.XqGameContext.Of(g);
        int version = g.Version;
        var level = Settings.BotLevel;
        bool inOpening = g.History.Count < XqBook.MaxPlies;
        var book = Book;
        Task.Run(async () =>
        {
            // In the opening, a well-tested book move beats a 2-second search.
            if (inOpening && level != XqLevel.Beginner)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
                    var entries = await book.LookupAsync(XqZobrist.Hash(position), timeout.Token);
                    if (XqBook.Pick(position, entries, level, Random.Shared) is XqMove bookMove) return bookMove;
                }
                catch (Exception)
                {
                    // Database slow or down: just search.
                }
            }
            return XqBot.ChooseMove(position, level, context: context);
        }).ContinueWith(t =>
        {
            lock (Sync)
            {
                if (_game != g) return;
                _botThinking = false;
                if (t.IsCompletedSuccessfully && g.Version == version && !g.IsOver)
                    PlayMove(g, t.Result, Clock());
            }
            NotifyChanged();
        }, TaskScheduler.Default);
    }

    double ClockSeconds(XqGame g, XqSide side, DateTime now)
    {
        if (!Settings.HasClock) return 0;
        var left = _clock[Index(side)] - (!g.IsOver && g.SideToMove == side ? now - _turnStart : TimeSpan.Zero);
        return Math.Max(0, left.TotalSeconds);
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

            var now = Clock();
            SyncClock(g, now);
            XqSide? mySide = me >= 0 ? SideOfSeat(me) : null;
            bool myTurn = !g.IsOver && mySide == g.SideToMove;
            var myMoves = myTurn
                ? g.LegalMoves().GroupBy(m => m.From).ToDictionary(x => x.Key, x => x.Select(m => m.To).ToArray())
                : new Dictionary<int, int[]>();

            double? left = null;
            double total = 0;
            if (!g.IsOver && !Settings.HasClock && Settings.MoveSeconds > 0 && !Seats[SeatOfSide(g.SideToMove)].IsBot)
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
                RedClock = ClockSeconds(g, XqSide.Red, now),
                BlackClock = ClockSeconds(g, XqSide.Black, now),
                ClockAt = DateTime.UtcNow,
                DrawOfferBy = g.IsOver ? null : _drawOffer,
                MyDrawDeclined = !g.IsOver && mySide is not null && _drawDeclined == mySide,
                CanOfferDraw = mySide is XqSide side && CanOffer(g, side),
            };
        }
    }
}
