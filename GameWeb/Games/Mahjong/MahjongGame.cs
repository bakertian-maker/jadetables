using System.Security.Cryptography;

namespace GameWeb.Games.Mahjong;

public enum MjPhase { NotStarted, Turn, Claim, RobKong, HandOver, MatchOver }

public enum MjActionType { Discard, SelfDrawWin, ConcealedKong, AddKong, Win, Pung, Kong, Chow, Pass, Ready }

/// <summary>A player action. For Chow, Tile is the lowest tile of the run; for kongs and discards, the tile.</summary>
public readonly record struct MjAction(MjActionType Type, int Tile = -1);

public sealed class MjSettings
{
    /// <summary>Minimum faan needed to declare a win (Hong Kong standard is 3).</summary>
    public int MinFaan { get; set; } = 3;
    /// <summary>Faan value of a limit hand; totals are capped here.</summary>
    public int LimitFaan { get; set; } = 10;
    /// <summary>Number of prevailing-wind rounds: 1 = East round only, 4 = full game.</summary>
    public int Rounds { get; set; } = 1;
}

public enum MjLogKind { HandStart, ConcealedKong, AddKong, Pung, Kong, Chow, SelfDrawWin, DiscardWin, WallEmpty, MatchOver }

/// <summary>A game event, kept as data so each viewer can read it in their own language.</summary>
public readonly record struct MjLogEntry(MjLogKind Kind, int Seat = -1, int Other = -1, int Tile = -1, int Value = 0);

public sealed class DiscardTile(int tile)
{
    public int Tile { get; } = tile;
    public bool Claimed { get; set; }
}

public sealed class MjPlayer
{
    public List<int> Hand { get; } = [];
    public List<Meld> Melds { get; } = [];
    public List<DiscardTile> Discards { get; } = [];
    public int Score { get; set; }
}

public sealed class TurnOptions
{
    public bool CanSelfDraw { get; init; }
    public ScoreResult? SelfDrawScore { get; init; }
    public IReadOnlyList<int> ConcealedKongs { get; init; } = [];
    public IReadOnlyList<int> AddKongs { get; init; } = [];
}

public sealed class ClaimOptions
{
    public int Tile { get; init; }
    public int FromSeat { get; init; }
    public bool Win { get; init; }
    public ScoreResult? WinScore { get; init; }
    public bool Pung { get; init; }
    public bool Kong { get; init; }
    public IReadOnlyList<int> Chows { get; init; } = [];
    public bool Any => Win || Pung || Kong || Chows.Count > 0;
}

public sealed class HandResult
{
    public int Winner { get; init; } = -1;
    public int WinTile { get; init; } = -1;
    public bool SelfDrawn { get; init; }
    public int Discarder { get; init; } = -1;
    public ScoreResult? Score { get; init; }
    public int[] Delta { get; init; } = new int[4];
    public List<int>[] Hands { get; init; } = [];
    public List<Meld>[] Melds { get; init; } = [];
    public bool IsDraw => Winner < 0;
}

/// <summary>
/// Hong Kong mahjong rules engine for four seats. Purely logical: no timers, no I/O.
/// Seats are fixed 0..3; play moves 0 → 1 → 2 → 3 (counter-clockwise at the table).
/// </summary>
public sealed class MahjongGame
{
    readonly Random? _rng;
    readonly List<int> _wall = [];
    readonly ClaimOptions?[] _claimOptions = new ClaimOptions?[4];
    readonly MjAction?[] _claimResponses = new MjAction?[4];
    readonly List<MjLogEntry> _log = [];
    bool _justDrew;
    bool _drewReplacement;
    int _discardCount;
    bool _noClaimsYet;
    int _kongSeat = -1;
    int _kongTile = -1;

    public MahjongGame(MjSettings settings, Random? rng = null)
    {
        Settings = settings;
        _rng = rng;
        for (int i = 0; i < 4; i++) Players[i] = new MjPlayer();
    }

    public MjSettings Settings { get; }
    public MjPlayer[] Players { get; } = new MjPlayer[4];
    public MjPhase Phase { get; private set; } = MjPhase.NotStarted;
    public int Dealer { get; private set; }
    public int PrevailingWind { get; private set; }
    public int HandNumber { get; private set; }
    public int CurrentSeat { get; private set; }
    public int? DrawnTile { get; private set; }
    public int LastDiscard { get; private set; } = -1;
    public int LastDiscarder { get; private set; } = -1;
    public int WallCount => _wall.Count;
    public HandResult? LastResult { get; private set; }
    public bool[] Ready { get; } = new bool[4];
    public IReadOnlyList<MjLogEntry> Log => _log;
    /// <summary>Changes only when the phase or the seat to act changes (used for timers).</summary>
    public int PhaseVersion { get; private set; }

    public int SeatWind(int seat) => (seat - Dealer + 4) % 4;

    public ClaimOptions? GetClaimOptions(int seat) =>
        Phase is MjPhase.Claim or MjPhase.RobKong && _claimResponses[seat] is null ? _claimOptions[seat] : null;

    public bool HasResponded(int seat) => _claimResponses[seat] is not null;

    /// <summary>Seats that still owe a response in the current claim window.</summary>
    public IEnumerable<int> PendingClaimSeats()
    {
        if (Phase is not (MjPhase.Claim or MjPhase.RobKong)) yield break;
        for (int s = 0; s < 4; s++)
            if (_claimOptions[s] is not null && _claimResponses[s] is null) yield return s;
    }

    public void StartMatch()
    {
        foreach (var p in Players) p.Score = 0;
        Dealer = 0;
        PrevailingWind = 0;
        HandNumber = 0;
        _log.Clear();
        StartHand();
    }

    void StartHand()
    {
        HandNumber++;
        _wall.Clear();
        for (int k = 0; k < Tiles.KindCount; k++)
            for (int n = 0; n < 4; n++) _wall.Add(k);
        if (_rng is null) RandomNumberGenerator.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_wall));
        else
            for (int i = _wall.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (_wall[i], _wall[j]) = (_wall[j], _wall[i]);
            }

        foreach (var p in Players)
        {
            p.Hand.Clear(); p.Melds.Clear(); p.Discards.Clear();
        }
        for (int round = 0; round < 13; round++)
            for (int i = 0; i < 4; i++)
            {
                Players[(Dealer + i) % 4].Hand.Add(_wall[0]);
                _wall.RemoveAt(0);
            }
        foreach (var p in Players) p.Hand.Sort();
        BeginHand();
    }

    /// <summary>Test hook: starts a hand with fixed 13-tile hands; the dealer draws wall[0] first.</summary>
    internal void StartWithTiles(List<int>[] hands, List<int> wall, int dealer = 0)
    {
        Dealer = dealer;
        HandNumber++;
        _wall.Clear();
        _wall.AddRange(wall);
        for (int s = 0; s < 4; s++)
        {
            Players[s].Hand.Clear(); Players[s].Melds.Clear(); Players[s].Discards.Clear();
            Players[s].Hand.AddRange(hands[s]);
            Players[s].Hand.Sort();
        }
        BeginHand();
    }

    void BeginHand()
    {
        LastResult = null;
        LastDiscard = -1;
        LastDiscarder = -1;
        Array.Clear(Ready);
        ClearClaims();
        _discardCount = 0;
        _noClaimsYet = true;
        AddLog(new(MjLogKind.HandStart, Dealer, Other: PrevailingWind, Value: HandNumber));
        Draw(Dealer, false);
    }

    void Draw(int seat, bool replacement)
    {
        if (_wall.Count == 0)
        {
            EndInDraw();
            return;
        }
        int t;
        if (replacement) { t = _wall[^1]; _wall.RemoveAt(_wall.Count - 1); }
        else { t = _wall[0]; _wall.RemoveAt(0); }
        var hand = Players[seat].Hand;
        hand.Add(t);
        hand.Sort();
        DrawnTile = t;
        _justDrew = true;
        _drewReplacement = replacement;
        CurrentSeat = seat;
        Phase = MjPhase.Turn;
        NewPhase();
    }

    public TurnOptions? GetTurnOptions(int seat)
    {
        if (Phase != MjPhase.Turn || seat != CurrentSeat) return null;
        var p = Players[seat];
        ScoreResult? score = null;
        if (_justDrew && DrawnTile is int dt)
        {
            var s = EvaluateWin(seat, Tiles.ToCounts(p.Hand), dt, selfDrawn: true, robbing: false);
            if (s is not null && s.Total >= Settings.MinFaan) score = s;
        }
        var concealed = new List<int>();
        var add = new List<int>();
        if (_wall.Count > 0)
        {
            var counts = Tiles.ToCounts(p.Hand);
            for (int k = 0; k < Tiles.KindCount; k++) if (counts[k] == 4) concealed.Add(k);
            foreach (var m in p.Melds)
                if (m.Type == MeldType.Pung && counts[m.Tile] > 0) add.Add(m.Tile);
        }
        return new TurnOptions { CanSelfDraw = score is not null, SelfDrawScore = score, ConcealedKongs = concealed, AddKongs = add };
    }

    ScoreResult? EvaluateWin(int seat, int[] counts, int winTile, bool selfDrawn, bool robbing)
    {
        var p = Players[seat];
        return Scoring.Evaluate(new WinContext
        {
            Concealed = counts,
            Melds = p.Melds,
            WinTile = winTile,
            SelfDrawn = selfDrawn,
            SeatWind = SeatWind(seat),
            PrevailingWind = PrevailingWind,
            LastTile = _wall.Count == 0 && !robbing,
            KongReplacement = selfDrawn && _drewReplacement,
            RobbingKong = robbing,
            Heavenly = selfDrawn && seat == Dealer && _discardCount == 0 && _noClaimsYet,
            Earthly = !selfDrawn && !robbing && LastDiscarder == Dealer && _discardCount == 1 && _noClaimsYet,
            LimitFaan = Settings.LimitFaan,
        });
    }

    /// <summary>Applies an action; returns false (and changes nothing) if it is not legal now.</summary>
    public bool Apply(int seat, MjAction a)
    {
        if (seat is < 0 or > 3) return false;
        switch (Phase)
        {
            case MjPhase.Turn when seat == CurrentSeat:
                return ApplyTurn(seat, a);
            case MjPhase.Claim or MjPhase.RobKong:
                return ApplyClaim(seat, a);
            case MjPhase.HandOver when a.Type == MjActionType.Ready:
                if (Ready[seat]) return false;
                Ready[seat] = true;
                if (Ready.All(r => r)) NextHand();
                return true;
            default:
                return false;
        }
    }

    bool ApplyTurn(int seat, MjAction a)
    {
        var p = Players[seat];
        switch (a.Type)
        {
            case MjActionType.Discard:
            {
                if (!p.Hand.Remove(a.Tile)) return false;
                p.Discards.Add(new DiscardTile(a.Tile));
                LastDiscard = a.Tile;
                LastDiscarder = seat;
                _discardCount++;
                _justDrew = false;
                _drewReplacement = false;
                DrawnTile = null;
                OpenClaimWindow(seat, a.Tile);
                return true;
            }
            case MjActionType.SelfDrawWin:
            {
                var opts = GetTurnOptions(seat);
                if (opts is not { CanSelfDraw: true } || DrawnTile is not int dt) return false;
                FinishWin(seat, dt, opts.SelfDrawScore!, selfDrawn: true, discarder: -1);
                return true;
            }
            case MjActionType.ConcealedKong:
            {
                var opts = GetTurnOptions(seat);
                if (opts is null || !opts.ConcealedKongs.Contains(a.Tile)) return false;
                p.Hand.RemoveAll(t => t == a.Tile);
                p.Melds.Add(new Meld(MeldType.Kong, a.Tile, Concealed: true));
                _noClaimsYet = false;
                AddLog(new(MjLogKind.ConcealedKong, seat));
                Draw(seat, true);
                return true;
            }
            case MjActionType.AddKong:
            {
                var opts = GetTurnOptions(seat);
                if (opts is null || !opts.AddKongs.Contains(a.Tile)) return false;
                p.Hand.Remove(a.Tile);
                _justDrew = false;
                DrawnTile = null;
                _kongSeat = seat;
                _kongTile = a.Tile;
                ClearClaims();
                bool anyRob = false;
                for (int s = 0; s < 4; s++)
                {
                    if (s == seat) continue;
                    var counts = Tiles.ToCounts(Players[s].Hand);
                    counts[a.Tile]++;
                    var score = EvaluateWin(s, counts, a.Tile, selfDrawn: false, robbing: true);
                    if (score is not null && score.Total >= Settings.MinFaan)
                    {
                        _claimOptions[s] = new ClaimOptions { Tile = a.Tile, FromSeat = seat, Win = true, WinScore = score };
                        anyRob = true;
                    }
                }
                if (anyRob)
                {
                    Phase = MjPhase.RobKong;
                    NewPhase();
                }
                else CompleteAddKong();
                return true;
            }
            default:
                return false;
        }
    }

    void CompleteAddKong()
    {
        var p = Players[_kongSeat];
        int idx = p.Melds.FindIndex(m => m.Type == MeldType.Pung && m.Tile == _kongTile);
        var old = p.Melds[idx];
        p.Melds[idx] = old with { Type = MeldType.Kong };
        _noClaimsYet = false;
        AddLog(new(MjLogKind.AddKong, _kongSeat, Tile: _kongTile));
        int seat = _kongSeat;
        _kongSeat = -1;
        _kongTile = -1;
        ClearClaims();
        Draw(seat, true);
    }

    void OpenClaimWindow(int discarder, int tile)
    {
        ClearClaims();
        bool any = false;
        for (int s = 0; s < 4; s++)
        {
            if (s == discarder) continue;
            var o = ComputeClaim(s, tile, discarder);
            if (o.Any)
            {
                _claimOptions[s] = o;
                any = true;
            }
        }
        if (any)
        {
            Phase = MjPhase.Claim;
            CurrentSeat = discarder;
            NewPhase();
        }
        else Draw((discarder + 1) % 4, false);
    }

    ClaimOptions ComputeClaim(int seat, int tile, int discarder)
    {
        var hand = Players[seat].Hand;
        var counts = Tiles.ToCounts(hand);
        counts[tile]++;
        var score = EvaluateWin(seat, counts, tile, selfDrawn: false, robbing: false);
        counts[tile]--;
        bool win = score is not null && score.Total >= Settings.MinFaan;
        bool wallLeft = _wall.Count > 0;
        var chows = new List<int>();
        if (wallLeft && seat == (discarder + 1) % 4 && Tiles.IsSuited(tile))
        {
            int r = tile % 9;
            for (int start = tile - 2; start <= tile; start++)
            {
                int sr = r - (tile - start);
                if (sr < 0 || sr > 6) continue;
                bool ok = true;
                for (int k = start; k < start + 3; k++)
                    if (k != tile && counts[k] == 0) ok = false;
                if (ok) chows.Add(start);
            }
        }
        return new ClaimOptions
        {
            Tile = tile,
            FromSeat = discarder,
            Win = win,
            WinScore = win ? score : null,
            Pung = wallLeft && counts[tile] >= 2,
            Kong = wallLeft && counts[tile] >= 3,
            Chows = chows,
        };
    }

    bool ApplyClaim(int seat, MjAction a)
    {
        var o = _claimOptions[seat];
        if (o is null || _claimResponses[seat] is not null) return false;
        bool ok = a.Type switch
        {
            MjActionType.Pass => true,
            MjActionType.Win => o.Win,
            MjActionType.Pung => o.Pung && Phase == MjPhase.Claim,
            MjActionType.Kong => o.Kong && Phase == MjPhase.Claim,
            MjActionType.Chow => o.Chows.Contains(a.Tile) && Phase == MjPhase.Claim,
            _ => false,
        };
        if (!ok) return false;
        _claimResponses[seat] = a;
        if (!PendingClaimSeats().Any()) ResolveClaims();
        return true;
    }

    void ResolveClaims()
    {
        if (Phase == MjPhase.RobKong)
        {
            for (int i = 1; i <= 3; i++)
            {
                int s = (_kongSeat + i) % 4;
                if (_claimResponses[s] is { Type: MjActionType.Win })
                {
                    int tile = _kongTile;
                    var score = _claimOptions[s]!.WinScore!;
                    Players[s].Hand.Add(tile);
                    Players[s].Hand.Sort();
                    FinishWin(s, tile, score, selfDrawn: false, discarder: _kongSeat);
                    return;
                }
            }
            CompleteAddKong();
            return;
        }

        int discarder = LastDiscarder;
        int t = LastDiscard;
        var discards = Players[discarder].Discards;

        // Win has priority; the first winner after the discarder takes it (head bump).
        for (int i = 1; i <= 3; i++)
        {
            int s = (discarder + i) % 4;
            if (_claimResponses[s] is { Type: MjActionType.Win })
            {
                discards[^1].Claimed = true;
                var score = _claimOptions[s]!.WinScore!;
                Players[s].Hand.Add(t);
                Players[s].Hand.Sort();
                FinishWin(s, t, score, selfDrawn: false, discarder: discarder);
                return;
            }
        }
        for (int i = 1; i <= 3; i++)
        {
            int s = (discarder + i) % 4;
            if (_claimResponses[s] is { Type: MjActionType.Kong or MjActionType.Pung } r)
            {
                discards[^1].Claimed = true;
                var hand = Players[s].Hand;
                int take = r.Type == MjActionType.Kong ? 3 : 2;
                for (int n = 0; n < take; n++) hand.Remove(t);
                var type = r.Type == MjActionType.Kong ? MeldType.Kong : MeldType.Pung;
                Players[s].Melds.Add(new Meld(type, t, false, t, discarder));
                _noClaimsYet = false;
                AddLog(new(type == MeldType.Kong ? MjLogKind.Kong : MjLogKind.Pung, s, discarder, t));
                ClearClaims();
                if (type == MeldType.Kong) Draw(s, true);
                else StartClaimedTurn(s);
                return;
            }
        }
        int next = (discarder + 1) % 4;
        if (_claimResponses[next] is { Type: MjActionType.Chow } chow)
        {
            discards[^1].Claimed = true;
            var hand = Players[next].Hand;
            for (int k = chow.Tile; k < chow.Tile + 3; k++)
                if (k != t) hand.Remove(k);
            Players[next].Melds.Add(new Meld(MeldType.Chow, chow.Tile, false, t, discarder));
            _noClaimsYet = false;
            AddLog(new(MjLogKind.Chow, next, discarder, t));
            ClearClaims();
            StartClaimedTurn(next);
            return;
        }
        ClearClaims();
        Draw(next, false);
    }

    void StartClaimedTurn(int seat)
    {
        CurrentSeat = seat;
        Phase = MjPhase.Turn;
        DrawnTile = null;
        _justDrew = false;
        _drewReplacement = false;
        NewPhase();
    }

    void FinishWin(int seat, int tile, ScoreResult score, bool selfDrawn, int discarder)
    {
        int points = Scoring.BasePoints(score.Total);
        var delta = new int[4];
        if (selfDrawn)
        {
            for (int s = 0; s < 4; s++)
                if (s != seat) { delta[s] -= points; delta[seat] += points; }
        }
        else
        {
            delta[discarder] -= points * 2;
            delta[seat] += points * 2;
        }
        for (int s = 0; s < 4; s++) Players[s].Score += delta[s];

        LastResult = new HandResult
        {
            Winner = seat,
            WinTile = tile,
            SelfDrawn = selfDrawn,
            Discarder = discarder,
            Score = score,
            Delta = delta,
            Hands = Players.Select(p => p.Hand.ToList()).ToArray(),
            Melds = Players.Select(p => p.Melds.ToList()).ToArray(),
        };
        AddLog(new(selfDrawn ? MjLogKind.SelfDrawWin : MjLogKind.DiscardWin, seat, discarder, tile, score.Total));
        EnterHandOver();
    }

    void EndInDraw()
    {
        LastResult = new HandResult
        {
            Hands = Players.Select(p => p.Hand.ToList()).ToArray(),
            Melds = Players.Select(p => p.Melds.ToList()).ToArray(),
        };
        AddLog(new(MjLogKind.WallEmpty));
        EnterHandOver();
    }

    void EnterHandOver()
    {
        ClearClaims();
        DrawnTile = null;
        _justDrew = false;
        Array.Clear(Ready);
        Phase = MjPhase.HandOver;
        NewPhase();
    }

    void NextHand()
    {
        var r = LastResult!;
        bool dealerKeeps = r.IsDraw || r.Winner == Dealer;
        if (!dealerKeeps)
        {
            Dealer = (Dealer + 1) % 4;
            if (Dealer == 0) PrevailingWind++;
        }
        // Safety cap so endless dealer repeats still finish the match.
        if (PrevailingWind >= Settings.Rounds || HandNumber >= Settings.Rounds * 12)
        {
            Phase = MjPhase.MatchOver;
            AddLog(new(MjLogKind.MatchOver));
            NewPhase();
            return;
        }
        StartHand();
    }

    /// <summary>The action taken when a player runs out of time (or is away).</summary>
    public MjAction AutoAction(int seat)
    {
        switch (Phase)
        {
            case MjPhase.Turn when seat == CurrentSeat:
                var opts = GetTurnOptions(seat)!;
                if (opts.CanSelfDraw) return new MjAction(MjActionType.SelfDrawWin);
                return new MjAction(MjActionType.Discard, DrawnTile ?? Players[seat].Hand[^1]);
            case MjPhase.Claim or MjPhase.RobKong:
                var c = GetClaimOptions(seat);
                return c is { Win: true } ? new MjAction(MjActionType.Win) : new MjAction(MjActionType.Pass);
            default:
                return new MjAction(MjActionType.Ready);
        }
    }

    void ClearClaims()
    {
        Array.Clear(_claimOptions);
        Array.Clear(_claimResponses);
    }

    void AddLog(MjLogEntry entry)
    {
        _log.Add(entry);
        if (_log.Count > 40) _log.RemoveAt(0);
    }

    /// <summary>Marks a new phase or a new seat to act, which restarts the room's timers.</summary>
    void NewPhase() => PhaseVersion++;
}
