namespace GameWeb.Games.DouDizhu;

public enum DdzPhase { Bidding, Playing, HandOver, MatchOver }

public enum DdzActionType { Bid, Pass, Play, Ready }

/// <param name="Bid">For <see cref="DdzActionType.Bid"/>: 1, 2 or 3 points.</param>
/// <param name="Cards">For <see cref="DdzActionType.Play"/>: the cards played.</param>
public sealed record DdzAction(DdzActionType Type, int Bid = 0, IReadOnlyList<int>? Cards = null);

public sealed class DdzSettings
{
    /// <summary>Hands in a match.</summary>
    public int Hands { get; init; } = 6;
}

/// <summary>What one seat did in the current trick: played cards, or passed.</summary>
public sealed record TrickAction(bool Passed, DdzCombo? Combo);

/// <summary>How a hand ended.</summary>
/// <param name="Points">Bid × multiplier: what each farmer wins or loses; the landlord wins or loses twice that.</param>
public sealed record DdzResult(
    int Landlord, bool LandlordWon, int Bid, int Bombs, bool Spring, bool AntiSpring,
    int Multiplier, int Points, int[] Changes, IReadOnlyList<IReadOnlyList<int>> Hands);

public enum DdzLogKind { Bid, Pass, Landlord, Redeal, Play, Win }

public sealed record DdzLogEntry(DdzLogKind Kind, int Seat, int Value = 0, DdzCombo? Combo = null);

/// <summary>
/// One Dou Dizhu match for three players: classic rules with 1–3 point bidding.
/// <list type="bullet">
/// <item>Each hand: 17 cards each, 3 left face down. Players bid once each in turn, 1, 2 or 3 points, each
/// bid higher than the last, or pass; 3 ends the bidding. The highest bidder is the landlord (地主), takes the
/// 3 cards and leads. If everyone passes, the cards are dealt again.</item>
/// <item>Play goes round: beat the last play with the same kind and length, play a bomb or the rocket, or
/// pass. After two passes in a row, whoever played last starts a new trick with anything.</item>
/// <item>The first to empty their hand wins for their side: the landlord alone, or the two farmers (农民).</item>
/// <item>Points = bid × 2 for each bomb or rocket played × 2 for a spring (the landlord won and neither farmer
/// played a card) or an anti-spring (the farmers won and the landlord played only once). Each farmer wins or
/// loses that much; the landlord twice as much.</item>
/// </list>
/// No timers or I/O: the room drives it.
/// </summary>
public sealed class DdzGame
{
    public const int Players = 3;
    public const int HandSize = 17;

    readonly Random _rng;
    readonly List<DdzLogEntry> _log = [];
    int _firstBidder;
    int _landlordPlays;
    int _farmerPlays;
    int _passesInRow;

    public DdzGame(DdzSettings settings, Random? rng = null)
    {
        Settings = settings;
        _rng = rng ?? Random.Shared;
        for (int s = 0; s < Players; s++) Hands[s] = [];
    }

    public DdzSettings Settings { get; }
    public DdzPhase Phase { get; private set; }
    /// <summary>1-based number of the hand being played.</summary>
    public int HandNumber { get; private set; }
    public List<int>[] Hands { get; } = new List<int>[Players];
    /// <summary>The three cards for the landlord; shown to everyone once the bidding is over.</summary>
    public List<int> Bottom { get; private set; } = [];
    public int Landlord { get; private set; } = -1;
    /// <summary>The highest bid so far (0 = none yet).</summary>
    public int Bid { get; private set; }
    /// <summary>Each seat's bid this hand: null = not yet asked, 0 = passed, 1–3 = points.</summary>
    public int?[] Bids { get; } = new int?[Players];
    public int CurrentSeat { get; private set; }
    /// <summary>The play to beat, and who made it; null when a new trick starts.</summary>
    public DdzCombo? LastCombo { get; private set; }
    public int LastSeat { get; private set; } = -1;
    /// <summary>What each seat did in the current trick, for display.</summary>
    public TrickAction?[] Trick { get; } = new TrickAction?[Players];
    /// <summary>Bombs and rockets played this hand.</summary>
    public int Bombs { get; private set; }
    public int[] Scores { get; } = new int[Players];
    public bool[] Ready { get; } = new bool[Players];
    public DdzResult? LastResult { get; private set; }
    public IReadOnlyList<DdzLogEntry> Log => _log;
    /// <summary>Changes whenever whose turn it is, or the phase, changes (the room times turns by it).</summary>
    public int PhaseVersion { get; private set; }
    /// <summary>The current multiplier: 2 for each bomb or rocket so far.</summary>
    public int Multiplier => 1 << Bombs;

    public bool IsLeading(int seat) => Phase == DdzPhase.Playing && CurrentSeat == seat && LastCombo is null;

    public void StartMatch()
    {
        HandNumber = 1;
        _firstBidder = _rng.Next(Players);
        Deal();
    }

    void Deal()
    {
        var deck = Cards.ShuffledDeck(_rng);
        for (int s = 0; s < Players; s++) Hands[s] = Cards.SortForDisplay(deck.Skip(s * HandSize).Take(HandSize));
        Bottom = Cards.SortForDisplay(deck.Skip(Players * HandSize));
        Landlord = -1;
        Bid = 0;
        Array.Clear(Bids);
        Array.Clear(Trick);
        Array.Clear(Ready);
        LastCombo = null;
        LastSeat = -1;
        Bombs = 0;
        _landlordPlays = _farmerPlays = _passesInRow = 0;
        CurrentSeat = _firstBidder;
        Phase = DdzPhase.Bidding;
        PhaseVersion++;
    }

    public bool Apply(int seat, DdzAction action)
    {
        if (seat is < 0 or >= Players) return false;
        return (Phase, action.Type) switch
        {
            (DdzPhase.Bidding, DdzActionType.Bid) => DoBid(seat, action.Bid),
            (DdzPhase.Bidding, DdzActionType.Pass) => DoBid(seat, 0),
            (DdzPhase.Playing, DdzActionType.Play) => DoPlay(seat, action.Cards ?? []),
            (DdzPhase.Playing, DdzActionType.Pass) => DoPass(seat),
            (DdzPhase.HandOver, DdzActionType.Ready) => DoReady(seat),
            _ => false,
        };
    }

    // ---------------------------------------------------------------- bidding

    /// <summary>Bids this seat may make now (higher than the current bid).</summary>
    public IEnumerable<int> AllowedBids(int seat) =>
        Phase == DdzPhase.Bidding && CurrentSeat == seat ? Enumerable.Range(Bid + 1, 3 - Bid) : [];

    bool DoBid(int seat, int points)
    {
        if (CurrentSeat != seat || Bids[seat] is not null) return false;
        if (points != 0 && (points <= Bid || points > 3)) return false;
        Bids[seat] = points;
        if (points > 0)
        {
            Bid = points;
            _log.Add(new DdzLogEntry(DdzLogKind.Bid, seat, points));
        }
        else _log.Add(new DdzLogEntry(DdzLogKind.Pass, seat));

        if (points == 3 || Bids.All(b => b is not null))
        {
            if (Bid == 0)
            {
                // Nobody wanted it: deal again, starting with the next player.
                _log.Add(new DdzLogEntry(DdzLogKind.Redeal, -1));
                _firstBidder = (_firstBidder + 1) % Players;
                Deal();
                return true;
            }
            int landlord = Array.FindIndex(Bids, b => b == Bid);
            Landlord = landlord;
            Hands[landlord] = Cards.SortForDisplay(Hands[landlord].Concat(Bottom));
            _log.Add(new DdzLogEntry(DdzLogKind.Landlord, landlord, Bid));
            Phase = DdzPhase.Playing;
            CurrentSeat = landlord;
        }
        else CurrentSeat = (CurrentSeat + 1) % Players;
        PhaseVersion++;
        return true;
    }

    // ---------------------------------------------------------------- play

    /// <summary>How <paramref name="cards"/> would be played by this seat now, or null if it isn't allowed.</summary>
    public DdzCombo? CheckPlay(int seat, IReadOnlyList<int> cards)
    {
        if (Phase != DdzPhase.Playing || CurrentSeat != seat || cards.Count == 0) return null;
        if (cards.Distinct().Count() != cards.Count || !cards.All(Hands[seat].Contains)) return null;
        return Combos.Read(cards, LastCombo);
    }

    bool DoPlay(int seat, IReadOnlyList<int> cards)
    {
        if (CheckPlay(seat, cards) is not { } combo) return false;
        foreach (var c in cards) Hands[seat].Remove(c);
        if (combo.IsBomb) Bombs++;
        if (seat == Landlord) _landlordPlays++; else _farmerPlays++;
        if (LastCombo is null) Array.Clear(Trick);   // a new trick: clear the previous one from view
        Trick[seat] = new TrickAction(false, combo);
        LastCombo = combo;
        LastSeat = seat;
        _passesInRow = 0;
        _log.Add(new DdzLogEntry(DdzLogKind.Play, seat, Combo: combo));

        if (Hands[seat].Count == 0) FinishHand(seat);
        else CurrentSeat = (seat + 1) % Players;
        PhaseVersion++;
        return true;
    }

    bool DoPass(int seat)
    {
        if (Phase != DdzPhase.Playing || CurrentSeat != seat || LastCombo is null) return false;   // the leader must play
        Trick[seat] = new TrickAction(true, null);
        _log.Add(new DdzLogEntry(DdzLogKind.Pass, seat));
        if (++_passesInRow == 2)
        {
            // Both others passed: the last player starts a new trick.
            CurrentSeat = LastSeat;
            LastCombo = null;
            _passesInRow = 0;
        }
        else CurrentSeat = (seat + 1) % Players;
        PhaseVersion++;
        return true;
    }

    /// <summary>What a player who ran out of time does: pass if they can, otherwise lead their lowest card.</summary>
    public DdzAction AutoAction(int seat)
    {
        if (Phase == DdzPhase.Bidding) return new DdzAction(DdzActionType.Pass);
        if (Phase == DdzPhase.HandOver) return new DdzAction(DdzActionType.Ready);
        if (LastCombo is not null) return new DdzAction(DdzActionType.Pass);
        var lowest = Hands[seat].OrderBy(Cards.Rank).First();
        return new DdzAction(DdzActionType.Play, Cards: [lowest]);
    }

    void FinishHand(int winner)
    {
        bool landlordWon = winner == Landlord;
        bool spring = landlordWon && _farmerPlays == 0;
        bool antiSpring = !landlordWon && _landlordPlays == 1;
        int multiplier = (1 << Bombs) * (spring || antiSpring ? 2 : 1);
        int points = Bid * multiplier;
        var changes = new int[Players];
        for (int s = 0; s < Players; s++)
        {
            int sign = (s == Landlord) == landlordWon ? 1 : -1;
            changes[s] = sign * (s == Landlord ? 2 * points : points);
            Scores[s] += changes[s];
        }
        LastResult = new DdzResult(Landlord, landlordWon, Bid, Bombs, spring, antiSpring, multiplier, points, changes,
            Hands.Select(h => (IReadOnlyList<int>)h.ToList()).ToList());
        _log.Add(new DdzLogEntry(DdzLogKind.Win, winner, landlordWon ? 1 : 0));
        Array.Clear(Ready);
        Phase = DdzPhase.HandOver;
    }

    bool DoReady(int seat)
    {
        if (Ready[seat]) return false;
        Ready[seat] = true;
        if (Ready.All(r => r))
        {
            if (HandNumber >= Settings.Hands)
            {
                Phase = DdzPhase.MatchOver;
            }
            else
            {
                HandNumber++;
                _firstBidder = (_firstBidder + 1) % Players;
                Deal();
            }
        }
        PhaseVersion++;
        return true;
    }

    /// <summary>True if the two seats are on the same side this hand (both farmers).</summary>
    public bool SameSide(int a, int b) => a == b || (Landlord >= 0 && a != Landlord && b != Landlord);
}
