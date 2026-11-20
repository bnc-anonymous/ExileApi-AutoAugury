using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AutoAugury;

/// <summary>
/// Finds what the Fates panel shows in AuguryCatalogue: a Fate by the name on its card, its tier by its full text, and
/// a reward by the text on its icon; and tells the panel's views and a waiting Cauldron's label by their text. Texts
/// are compared with their whitespace collapsed, since the panel's text and the table's carry the same words and markup
/// but not always the same line breaks.
///
/// Every text is matched against all of the client's languages at once (AuguryTexts), so the client's language is never
/// needed: the generator stops on a text that would read as two things across languages. Measured in English only;
/// the other languages are matched by the game's translated tables, unmeasured in game.
/// </summary>
internal static class AuguryCatalogueLookup
{
    private static readonly Dictionary<string, AuguryFate> FatesById =
        AuguryCatalogue.Fates.ToDictionary(static f => f.Id);

    private static readonly Dictionary<string, AuguryReward> RewardsById =
        AuguryCatalogue.Rewards.ToDictionary(static r => r.Id);

    private static readonly Dictionary<string, AuguryFate> FatesByName = BuildFatesByName();

    /// <summary>Each Fate's tier texts that tell its tiers apart, collapsed, by Fate Id.</summary>
    private static readonly Dictionary<string, List<(string Text, int Tier)>> TierTextsByFate = AuguryTexts.Languages
        .SelectMany(static l => l.TierTexts)
        .GroupBy(static t => t.FateId)
        .ToDictionary(static g => g.Key, static g => g.Select(static t => (Collapsed(t.Body), t.Tier)).ToList());

    /// <summary>
    /// Each reward by its text without the copies note and its tier, the first row of each (a family's level bands
    /// share a text), in every language. No key is shared by two families in English (exports of 2026-11-14 to
    /// 2026-11-20); the few shared in a translation are listed in AuguryTexts. A text alone is not enough: "will drop
    /// additional Currency Items" is CurrencyLow at tier 1 and CurrencyHigh at tier 2.
    /// </summary>
    private static readonly Dictionary<(string Text, int Tier), AuguryReward> RewardsByTextAndTier = new();

    /// <summary>Each reward by its text alone: the lowest tier's first row.</summary>
    private static readonly Dictionary<string, AuguryReward> RewardsByText = new();

    private static readonly HashSet<string> ChoosingTitles = TextsOf(static l => l.ChoosingTitle);
    private static readonly HashSet<string> InvokingTitles = TextsOf(static l => l.InvokingTitle);
    private static readonly HashSet<string> CauldronLabels = TextsOf(static l => l.CauldronLabel);
    private static readonly string[] TwoCopiesNotes = [..TextsOf(static l => l.TwoCopiesNote)];
    private static readonly string[] ThreeCopiesNotes = [..TextsOf(static l => l.ThreeCopiesNote)];

    static AuguryCatalogueLookup()
    {
        foreach (var language in AuguryTexts.Languages)
        {
            foreach (var shown in language.RewardTexts)
            {
                var reward = RewardsById.GetValueOrDefault(shown.RewardId);

                if (reward == null)
                    continue;

                var text = RewardText(shown.Description);

                RewardsByTextAndTier.TryAdd((text, reward.Tier), reward);

                if (!RewardsByText.TryGetValue(text, out var lowest) || reward.Tier < lowest.Tier)
                    RewardsByText[text] = reward;
            }
        }
    }

    private static Dictionary<string, AuguryFate> BuildFatesByName()
    {
        var byName = new Dictionary<string, AuguryFate>(StringComparer.OrdinalIgnoreCase);

        foreach (var fate in AuguryCatalogue.Fates)
            byName.TryAdd(fate.Id, fate);

        foreach (var name in AuguryTexts.Languages.SelectMany(static l => l.FateNames))
        {
            if (FatesById.TryGetValue(name.FateId, out var fate))
                byName.TryAdd(name.Name.Trim(), fate);
        }

        return byName;
    }

    /// <summary>One text from every language, collapsed, without duplicates (two languages may share one).</summary>
    private static HashSet<string> TextsOf(Func<AuguryLanguageTexts, string> textOf) =>
        AuguryTexts.Languages.Select(textOf).Select(Collapsed).Where(static t => t.Length > 0).ToHashSet();

    /// <summary>Whether a title is the Fates panel's while choosing a Fate ("Fates" in English).</summary>
    public static bool IsChoosingTitle(string title) => title != null && ChoosingTitles.Contains(Collapsed(title));

    /// <summary>Whether a title is the Fates panel's at the boss ("Invoke Fates" in English).</summary>
    public static bool IsInvokingTitle(string title) => title != null && InvokingTitles.Contains(Collapsed(title));

    /// <summary>Whether a text is a waiting Cauldron's ground label ("Select a Fate" in English).</summary>
    public static bool IsCauldronLabel(string text) => text != null && CauldronLabels.Contains(Collapsed(text));

    /// <summary>The Fate whose name (in any language, or its table Id) the card shows, or null.</summary>
    public static AuguryFate FateNamed(string name) =>
        name == null ? null : FatesByName.GetValueOrDefault(name.Trim());

    /// <summary>
    /// The tier whose full text equals the card's, or null when none does or more than one does: in English 7 of the
    /// 25 Fates (Dryad, Wildfire, Gravewalker, Tempest, Savage, Pestilent, Sadist) read the same at every tier, and
    /// AuguryTexts leaves out every text two tiers of a Fate share.
    /// </summary>
    public static int? TierOf(AuguryFate fate, string fullText)
    {
        if (fate == null || string.IsNullOrWhiteSpace(fullText) ||
            !TierTextsByFate.TryGetValue(fate.Id, out var tierTexts))
            return null;

        var text = Collapsed(fullText);
        var matches = tierTexts.Where(t => t.Text == text).Select(static t => t.Tier).Distinct().ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// How many copies a reward needs, from the note the panel adds to it ("To claim this reward, invoke two copies of
    /// it" in English): 2 or 3 by which language's note the text holds, 1 with none. Seen in English: the two-copy
    /// note on rewards of tier 2 in the table, the three-copy note on CurrencyOpulent1, of tier 3 (2026-11-14).
    /// </summary>
    public static int CopiesNeededOf(string shownText)
    {
        if (shownText == null)
            return 1;

        var text = Collapsed(shownText);

        if (ThreeCopiesNotes.Any(note => text.Contains(note, StringComparison.OrdinalIgnoreCase)))
            return 3;

        return TwoCopiesNotes.Any(note => text.Contains(note, StringComparison.OrdinalIgnoreCase)) ? 2 : 1;
    }

    /// <summary>
    /// The catalogue reward a shown text is: by its text and, taking the copies needed as the tier, by tier; failing
    /// that by text alone. Null when no reward has the text.
    /// </summary>
    public static AuguryReward RewardShown(string shownText, int copiesNeeded)
    {
        if (string.IsNullOrWhiteSpace(shownText))
            return null;

        var text = RewardText(shownText);

        return RewardsByTextAndTier.GetValueOrDefault((text, copiesNeeded)) ?? RewardsByText.GetValueOrDefault(text);
    }

    /// <summary>
    /// A reward's text as rewards are compared: without the panel's copies note, its whitespace collapsed.
    /// </summary>
    internal static string RewardText(string text) => Collapsed(WithoutCopiesNote(text));

    /// <summary>A reward's text without the panel's copies note ("&lt;rgb(122,122,122)&gt;{To claim ...}").</summary>
    internal static string WithoutCopiesNote(string text)
    {
        var note = (text ?? "").IndexOf("<rgb(", StringComparison.Ordinal);

        return note >= 0 ? text[..note] : text ?? "";
    }

    private static string Collapsed(string text) => Regex.Replace(text ?? "", @"\s+", " ").Trim();
}
