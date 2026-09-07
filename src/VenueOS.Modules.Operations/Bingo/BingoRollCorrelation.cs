namespace VenueOS.Modules.Operations.Bingo;

/// <summary>Which FFXIV command produced (or should produce) a roll result.</summary>
public enum BingoRollMode { Random, Dice }

/// <summary>A normalized, Dalamud-free view of one chat message that MIGHT satisfy a pending roll — produced by the
/// Plugin-side adapter (<c>VenueOS.Plugin.Bingo.BingoRollChatAdapter</c>), which has already resolved
/// <see cref="IsFromLocalHost"/> using Dalamud's structured sender identity (a <c>PlayerPayload</c>'s Name+World
/// where the chat message actually carries one, falling back to the rendered sender text only otherwise — see the
/// adapter's own doc comment for exactly which signal was used and what still needs live FFXIV verification,
/// especially for <c>/dice 75</c>). This record itself has zero Dalamud dependency so the correlation decision below
/// is fully unit-testable with synthetic values.</summary>
public sealed record BingoRollObservation(BingoRollMode Mode, bool IsFromLocalHost, int? ParsedNumber, DateTimeOffset ObservedAt);

/// <summary>One bounded "waiting for a specific local-host roll" window — created when the operator presses
/// Roll &amp; Call, consumed by at most one matching <see cref="BingoRollObservation"/>. This is the mechanism that
/// makes "VenueOS only consumes the roll it asked its own local character to make" true even when another party
/// member rolls the exact same command during the window (see <see cref="BingoRollCorrelator"/>).</summary>
public sealed class BingoPendingRoll(Guid attemptId, BingoRollMode mode, DateTimeOffset deadline)
{
    public Guid AttemptId { get; } = attemptId;
    public BingoRollMode Mode { get; } = mode;
    public DateTimeOffset Deadline { get; } = deadline;
    public bool Consumed { get; private set; }

    /// <summary>Called exactly once, the instant a matching observation is accepted — makes a second, later
    /// matching message (a duplicate chat delivery, or a coincidentally-identical roll) unable to trigger a second
    /// backend submission for the same Call Number action.</summary>
    public void MarkConsumed() => Consumed = true;
}

public enum BingoRollRejectReason { NoPendingRoll, AlreadyConsumed, Expired, WrongMode, NotLocalHost, NumberMissing, NumberOutOfRange }

public readonly struct BingoRollEvaluation
{
    private BingoRollEvaluation(bool accepted, int number, BingoRollRejectReason? rejectReason) { Accepted = accepted; Number = number; RejectReason = rejectReason; }
    public bool Accepted { get; }
    public int Number { get; }
    public BingoRollRejectReason? RejectReason { get; }
    public static BingoRollEvaluation Accept(int number) => new(true, number, null);
    public static BingoRollEvaluation Reject(BingoRollRejectReason reason) => new(false, 0, reason);
}

/// <summary>Pure decision logic: does this observation satisfy this pending roll? No Dalamud dependency, no I/O —
/// every rule the forensic-audit-derived "Dice acceptance conditions" list names is a single check here, evaluated
/// in order so the first failing condition is the reported reason. Rejecting is always silent from the caller's
/// perspective for ordinary unrelated chat — VenueBingoService only acts on <see cref="BingoRollEvaluation.Accepted"/>.
/// </summary>
public static class BingoRollCorrelator
{
    public static BingoRollEvaluation Evaluate(BingoPendingRoll? pending, BingoRollObservation observation, DateTimeOffset now)
    {
        if (pending is null) return BingoRollEvaluation.Reject(BingoRollRejectReason.NoPendingRoll);
        if (pending.Consumed) return BingoRollEvaluation.Reject(BingoRollRejectReason.AlreadyConsumed);
        if (now > pending.Deadline) return BingoRollEvaluation.Reject(BingoRollRejectReason.Expired);
        if (pending.Mode != observation.Mode) return BingoRollEvaluation.Reject(BingoRollRejectReason.WrongMode);
        // Explicit required scenario (product correction): a party member's identical-looking roll during the same
        // window must never be consumed — this is the one check that makes that true.
        if (!observation.IsFromLocalHost) return BingoRollEvaluation.Reject(BingoRollRejectReason.NotLocalHost);
        if (observation.ParsedNumber is not int number) return BingoRollEvaluation.Reject(BingoRollRejectReason.NumberMissing);
        if (number is < 1 or > 75) return BingoRollEvaluation.Reject(BingoRollRejectReason.NumberOutOfRange);
        return BingoRollEvaluation.Accept(number);
    }
}

/// <summary>Pure, Dalamud-free self/local-host sender check — extracted out of
/// <c>VenueOS.Plugin.Bingo.BingoRollChatAdapter</c> so the exact donor-parity decision rule can be unit-tested
/// directly instead of only by inspection (the adapter itself has a Dalamud dependency no test project references).
/// Donor-EXACT (FFXIVBingo4All.Plugin/Plugin.cs ~line 259-266): the donor's REJECT condition is
/// <c>!string.IsNullOrEmpty(senderName) &amp;&amp; senderName != "You" &amp;&amp; senderName != localName</c>.
/// De Morgan'd, that ACCEPTS whenever <paramref name="senderText"/> is empty/blank, OR equals "You", OR equals the
/// local character's bare name. A live-QA correction pass found VenueOS's prior version required a non-blank sender
/// that positively matched, so a message whose rendered sender text happens to be blank (plausible for a
/// self-attributed system-style roll message carrying no player-name text at all) was silently rejected with zero
/// trace — matching exactly the "0 called, no visible error" symptom reported live. Do not add a PlayerPayload or
/// other metadata requirement here for Random: the donor's own working implementation never checks one.</summary>
public static class BingoRollSenderIdentity
{
    public static bool IsLocalHost(string? senderText, string? localName)
    {
        var sender = senderText?.Trim();
        if (string.IsNullOrEmpty(sender)) return true; // donor-exact: a blank sender is never rejected
        if (string.Equals(sender, "You", StringComparison.OrdinalIgnoreCase)) return true;
        var name = localName?.Trim();
        return !string.IsNullOrEmpty(name) && string.Equals(sender, name, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Pure text parsing for the two supported roll commands — no Dalamud dependency, fully unit-testable
/// against synthetic chat text. Chat-type/sender filtering happens separately, in the adapter (Plugin-side); this
/// class only ever answers "does this text look like a result, and if so what number".
///
/// LIVE REGRESSION POSTMORTEM (this correction pass): an earlier version of this class used a paraphrased
/// approximation of the donor's regex (transcribed from the forensic audit's prose summary, not the donor source
/// itself) that omitted the parentheses the real game text actually contains — "You roll a 42 (1-75)." — so it
/// never matched a single real result, and the earlier adapter additionally pre-filtered on
/// <c>XivChatType.RandomNumber</c> (a type that existed in the Dalamud assembly per reflection, but was never
/// confirmed to be what `/random 75` results actually arrive as, and evidently is not). Both mistakes are corrected
/// here and in <c>BingoRollChatAdapter</c>: the regex below is copied byte-for-byte from the donor's own
/// live-verified <c>RollRegex</c> (FFXIVBingo4All.Plugin/Plugin.cs ~line 37-40), and the adapter no longer filters
/// on chat type for Random at all — exactly matching the donor's own working implementation, which processes every
/// chat message's text and relies entirely on the regex + sender check to disqualify anything irrelevant.</summary>
public static class BingoRollTextParser
{
    // Donor's EXACT regex, verbatim (FFXIVBingo4All.Plugin/Plugin.cs ~line 37-40): optionally prefixed with
    // "Random! ", "You roll a N" then "(1-75)" or "(out of 75)" (spacing around the dash tolerated), optional
    // trailing period, case-insensitive. This is the one part of the whole roll pipeline that has actually been
    // exercised in real production traffic — do not "clean up" or re-paraphrase it; copy the donor's literal
    // pattern if it is ever revisited.
    private static readonly System.Text.RegularExpressions.Regex RollResultRegex = new(
        @"(?:Random!\s*)?You roll a (\d+)\s*\((?:1\s*-\s*75|out of 75)\)\.?",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>The one shared parser for both roll commands. Evidence for treating them as the same underlying
    /// text template: the donor's own regex already tolerates an optional leading "Random! " (i.e. the donor
    /// anticipated/observed that exact prefix on the shared "You roll a N (X-Y)." engine text), and the live
    /// operator's own description of `/dice 75`'s output ("Random! (1-75)" plus the rolled value) matches this same
    /// shape. What is NOT yet confirmed for `/dice 75` specifically is which chat channel/type carries it — see
    /// <c>BingoRollChatAdapter</c>, which is what actually decides Random vs. Dice (by channel), not this parser.
    /// </summary>
    public static int? TryParseRollResult(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!text.Contains("You roll", StringComparison.OrdinalIgnoreCase)) return null; // donor's own fast pre-check
        var match = RollResultRegex.Match(text);
        return match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : null;
    }

    // Kept as thin aliases so call sites can still say what they mean — both currently delegate to the identical
    // parser above; see TryParseRollResult's doc comment for why.
    public static int? TryParseRandomResult(string? text) => TryParseRollResult(text);
    public static int? TryParseDiceResult(string? text) => TryParseRollResult(text);
}
