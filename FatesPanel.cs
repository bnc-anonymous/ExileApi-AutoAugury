using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using ExileCore;
using ExileCore.PoEMemory;
using RectangleF = SharpDX.RectangleF;

namespace AutoAugury;

/// <summary>
/// A reward icon on a card: the text the panel holds for it, the catalogue reward it is, its copies, where it is.
/// </summary>
/// <param name="Text">The reward's text as the panel holds it, markup included.</param>
/// <param name="Reward">The catalogue reward the text is, or null when no catalogue reward has that text.</param>
/// <param name="CopiesNeeded">
/// How many copies of the Fate must be invoked for the reward: 1; 2 when the panel adds "To claim this reward, invoke
/// two copies of it"; 3 for "three copies".
/// </param>
/// <param name="Rect">
/// The icon's rectangle in client pixels where it sits while its card is not hovered (51x48 since 3.30.0c made the
/// panel larger, 40x37 before): the hovered card's content, icons included, is lifted (FatesPanelState.HoverLift), and
/// the lift read with the icon is taken out, so the rectangle stays put between reads. Empty when not read.
/// </param>
internal sealed record FateCardReward(string Text, AuguryReward Reward, int CopiesNeeded, RectangleF Rect = default);

/// <summary>A card in the Fates panel.</summary>
/// <param name="Name">The name the card shows.</param>
/// <param name="Fate">The catalogue Fate of that name, or null when none has it.</param>
/// <param name="Tier">
/// The tier, from the card's full text, which differs by tier for 18 of the 25 Fates; for the others, and for a text
/// a patch changed, the area's tier read off another card (FatesPanel.WithAreaTier); null when neither is known. The
/// panel's own tier number is a hidden element that read "1" on tier 2 and 3 cards.
/// </param>
/// <param name="Rect">
/// The card's rectangle in client pixels; the hovered card's content moves up 13 px, this does not.
/// </param>
/// <param name="Rewards">The card's visible reward icons, in order.</param>
/// <param name="Placed">
/// Whether the card's element reads not active (Element.IsActive false), which at the boss marks a collected copy that
/// is in an invocation slot: in every boss dump of 2026-11-14 the collection cards reading not active were exactly the
/// copies placed (one Crowkeeper of three cards at 04:26, one Incandescent of two at 04:36, two then all three
/// Incandescent at 05:35, the Houndmaster with Item Rarity of two at 06:14), and with nothing placed every card read
/// active. Every card read active while choosing, and every slot card at the boss.
/// </param>
internal sealed record FateCard(string Name, AuguryFate Fate, int? Tier, RectangleF Rect,
    IReadOnlyList<FateCardReward> Rewards, bool Placed = false);

/// <summary>What FatesPanel read: whether the panel is open, in which view, and the cards of its two rows.</summary>
/// <param name="Visible">Whether the panel is open.</param>
/// <param name="Title">
/// The title, in the client's language: "Fates" while choosing, "Invoke Fates" at the boss in English; empty when
/// closed.
/// </param>
/// <param name="Offered">
/// The top row's visible cards: the Fates on offer while choosing; at the boss, the Fates placed in the invocation
/// slots (an empty slot has no name and is left out).
/// </param>
/// <param name="Collection">The bottom row's visible cards: the area's collection, in both views.</param>
/// <param name="TopRowSlots">
/// The rectangles of the top row's visible card elements, named or not: at the boss, the invocation slots, which an
/// empty slot shows as a card frame with its name hidden (three at the boss of 2026-11-14 03:38).
/// </param>
/// <param name="ConfirmButton">
/// The confirm button's rectangle ([2,10], below the top row in both views; 221x52 while choosing since 3.30.0c, 193x45
/// before); empty when it is not visible.
/// </param>
/// <param name="CloseButton">
/// The close button's rectangle ([3], 37x37 at the panel's top right in both views); empty when it is not visible.
/// </param>
/// <param name="HoverLift">
/// How far, in pixels, the game lifts the content of the card under the mouse (its name, text and reward icons; the
/// card's own rectangle stays): the most read so far; 0 until a hovered card has been read (the overlay then takes
/// FatesPanel.UnmeasuredHoverLift).
/// </param>
/// <param name="Counter">The Fate counter the panel shows ([2,5,0]); null when it shows none, or not "N/M".</param>
internal sealed record FatesPanelState(bool Visible, string Title, IReadOnlyList<FateCard> Offered,
    IReadOnlyList<FateCard> Collection, IReadOnlyList<RectangleF> TopRowSlots, RectangleF ConfirmButton,
    RectangleF CloseButton, float HoverLift = 0f, FateCounter Counter = null)
{
    public static readonly FatesPanelState Closed = new(false, "", [], [], [], default, default);

    /// <summary>Whether the panel offers a Cauldron's Fates to choose from.</summary>
    public bool ChoosingFate => Visible && AuguryCatalogueLookup.IsChoosingTitle(Title);

    /// <summary>Whether the panel is the boss's invocation view.</summary>
    public bool InvokingFates => Visible && AuguryCatalogueLookup.IsInvokingTitle(Title);
}

/// <summary>
/// The Fate counter the game shows since patch 3.30.0.4 (2026-11-19), "collected/available", in the Fates panel and on
/// the Show collected Fates button: the Fates collected in the area, and the Fates it holds, one per Cauldron. Read
/// from the client's code: the first number is the collection's length, the second a byte the server sends in a
/// message that carries nothing else (ClientAugury's sub-message 4). Seen in game: it counts the map's Cauldrons used
/// and in all (the user, 2026-11-19: "1/4" after the first choice).
/// </summary>
internal sealed record FateCounter(int Collected, int InArea)
{
    private static readonly Regex CounterText = new(@"^\s*(\d+)\s*/\s*(\d+)\s*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The counter a text shows, or null when the text is not "N/M" with M at least 1. The button's copy read empty
    /// before the server had sent the count (dump of 2026-11-19 22:25:49, no Cauldron opened yet in that visit).
    /// </summary>
    public static FateCounter Parse(string text)
    {
        var match = CounterText.Match(text ?? "");

        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var collected) ||
            !int.TryParse(match.Groups[2].Value, out var inArea) || inArea < 1)
            return null;

        return new FateCounter(collected, inArea);
    }
}

/// <summary>
/// Reads the Fates panel, the Augury window in which a Cauldron's Fates are chosen and, at the area's boss, the
/// collection's Fates are invoked, as the game shows them. Only visible elements count: the panel reuses its elements,
/// and hidden ones keep earlier cards' names and rewards (dumps of 2026-11-14).
///
/// The layout, read off dumps of 2026-11-14 (the panel while choosing, and at the boss): the panel is a
/// child of IngameUi whose index moves as other panels open, recognised by its title, child 2's child 3: "Fates" while
/// choosing, "Invoke Fates" at the boss, in English (AuguryCatalogueLookup knows each language's). Below child 2:
/// [5,0] the Fate counter's text, "collected/available" ("0/4"), [6,0] the top row (the offer, or the invocation
/// slots), [7,0] the collection, [8] a reward summary (hidden while choosing, visible at the boss), [10] the confirm
/// button. Patch 3.30.0.4 (2026-11-19) added the counter at [5] and
/// moved every later child up one (child 2 had 11 children before, 12 since; dumps of 22:30 to 22:36 choosing, with
/// the counter at "0/4" to "2/4", and 22:37:09 at the boss, "4/4"). A card k's content is [k,0,0]: [4] its name,
/// [8,0] its reward icons, each icon's child [3] the reward's text; the card element's Tooltip holds its full text.
/// </summary>
internal sealed class FatesPanel
{
    /// <summary>
    /// How often the panel is looked for among IngameUi's children while it has not been found, in ms. Chosen: the
    /// panel may not exist until a Cauldron is first opened, and looking means reading every child's title.
    /// </summary>
    private const int LookIntervalMs = 1000;

    /// <summary>The most text read; Element.Text stops at about 250 characters, short of a Fate's full text.</summary>
    private const int MostTextLength = 4096;

    /// <summary>
    /// The hover lift taken until one is read: 13 px, measured before 3.30.0c (2026-11-14); the larger panel may lift
    /// by another amount, which the first read of a hovered card replaces.
    /// </summary>
    public const float UnmeasuredHoverLift = 13f;

    /// <summary>
    /// The largest lift taken as one: a card's content read further above its rectangle than this is taken to be in
    /// some other state, not hovered. Chosen, about three times the lift measured.
    /// </summary>
    private const float MostHoverLift = 40f;

    private Element _panel;

    /// <summary>The largest hover lift read since the panel was found; 0 until one is read.</summary>
    private float _hoverLift;

    /// <summary>Not started, so the first read looks at once; restarted by every look after.</summary>
    private readonly Stopwatch _sinceLook = new();

    /// <summary>
    /// Forgets the panel, so the next read looks for it again. For an area change. The hover lift is kept: it belongs
    /// to the interface's size, not to the area.
    /// </summary>
    public void Forget() => _panel = null;

    public FatesPanelState Read(GameController gc)
    {
        var panel = PanelOf(gc);

        if (panel == null || !Safe.Read(panel, static p => p.IsVisible, false))
            return FatesPanelState.Closed;

        var body = ChildAt(panel, 2);

        var title = TitleOf(panel);
        var top = CardsOf(ChildAt(body, 6, 0));
        var bottom = CardsOf(ChildAt(body, 7, 0));
        var choosing = AuguryCatalogueLookup.IsChoosingTitle(title);
        var (offered, collection) = WithAreaTier(top.Cards, bottom.Cards, choosing);

        _hoverLift = Math.Max(_hoverLift, Math.Max(top.Lift, bottom.Lift));

        var counterElement = ChildAt(body, 5, 0);
        var counter = Safe.Read(counterElement, static c => c?.IsVisible ?? false, false)
            ? FateCounter.Parse(TextOf(counterElement))
            : null;

        return new FatesPanelState(true, title, offered, collection, top.Slots, VisibleRectOf(ChildAt(body, 10)),
            VisibleRectOf(ChildAt(panel, 3)), _hoverLift, counter);
    }

    /// <summary>
    /// The cards, those whose text gave no tier given the area's: every card in an area shows one tier, the one the
    /// server sends on entering it (the client looks up a card's tier text by that tier and the card, read from its
    /// code on 2026-11-18). The area's tier is the one tier read off the collection and, while choosing, the offer; not
    /// off the boss's slots, whose text shows a pair's raised tier (tier 3 for two copies of a tier 2 Fate, 2026-11-14
    /// 05:35). Unchanged when no tier was read, or two different ones were.
    /// </summary>
    internal static (List<FateCard> Top, List<FateCard> Bottom) WithAreaTier(List<FateCard> top, List<FateCard> bottom,
        bool choosing)
    {
        var tiers = (choosing ? top.Concat(bottom) : bottom).Select(static c => c.Tier).OfType<int>().Distinct()
            .ToList();

        if (tiers.Count != 1)
            return (top, bottom);

        List<FateCard> Filled(List<FateCard> cards) =>
            cards.Select(c => c.Tier == null ? c with { Tier = tiers[0] } : c).ToList();

        return (Filled(top), Filled(bottom));
    }

    /// <summary>An element's rectangle while it is visible; empty otherwise.</summary>
    private static RectangleF VisibleRectOf(Element element)
    {
        return Safe.Read(element, static e => e?.IsVisible ?? false, false)
            ? Safe.Read(element, static e => e.GetClientRectCache, default(RectangleF))
            : default;
    }

    /// <summary>The panel: the one found before while it still has the title, or a new look among IngameUi's.</summary>
    private Element PanelOf(GameController gc)
    {
        if (_panel != null && IsFatesPanel(_panel))
            return _panel;

        _panel = null;

        if (_sinceLook.IsRunning && _sinceLook.ElapsedMilliseconds < LookIntervalMs)
            return null;

        _sinceLook.Restart();

        var ingameUi = Safe.Read(gc, static g => (Element)g.IngameState.IngameUi, null);

        foreach (var child in Safe.ChildrenOf(ingameUi) ?? [])
        {
            if (!IsFatesPanel(child))
                continue;

            _panel = child;
            break;
        }

        return _panel;
    }

    /// <summary>
    /// Whether an element is the Fates panel: its title reads as one of the panel's two in any of the client's
    /// languages ("Fates" or "Invoke Fates" in English).
    /// </summary>
    public static bool IsFatesPanel(Element panel)
    {
        var title = TitleOf(panel);

        return AuguryCatalogueLookup.IsChoosingTitle(title) || AuguryCatalogueLookup.IsInvokingTitle(title);
    }

    /// <summary>The text of child 2's child 3, where the Fates panel has its title; null when there is none.</summary>
    private static string TitleOf(Element panel) => Safe.Read(ChildAt(panel, 2, 3), static t => t?.Text, null);

    /// <summary>
    /// A row's cards: every visible card element's rectangle (slots), and a FateCard for each whose name element is
    /// visible and named, with the largest hover lift read among them (0 when no card was hovered). An empty
    /// invocation slot is a visible card element with its name element hidden.
    /// </summary>
    private static (List<FateCard> Cards, List<RectangleF> Slots, float Lift) CardsOf(Element row)
    {
        var cards = new List<FateCard>();
        var slots = new List<RectangleF>();
        var mostLift = 0f;

        if (!Safe.Read(row, static r => r?.IsVisible ?? false, false))
            return (cards, slots, mostLift);

        foreach (var card in Safe.ChildrenOf(row) ?? [])
        {
            if (!Safe.Read(card, static c => c.IsVisible, false))
                continue;

            var rect = Safe.Read(card, static c => c.GetClientRectCache, default(RectangleF));

            slots.Add(rect);

            var content = ChildAt(card, 0, 0);
            var lift = LiftOf(rect, Safe.Read(content, static c => c?.GetClientRectCache ?? default,
                default(RectangleF)));
            var nameElement = ChildAt(content, 4);

            mostLift = Math.Max(mostLift, lift);
            var name = Safe.Read(nameElement, static n => n?.IsVisible ?? false, false) ? TextOf(nameElement) : null;

            if (string.IsNullOrWhiteSpace(name))
                continue;

            var fate = AuguryCatalogueLookup.FateNamed(name);
            var fullText = TextOf(Safe.Read(card, static c => c.Tooltip, null));

            cards.Add(new FateCard(name.Trim(), fate, AuguryCatalogueLookup.TierOf(fate, fullText), rect,
                RewardsOf(ChildAt(content, 8, 0), lift), !Safe.Read(card, static c => c.IsActive, true)));
        }

        return (cards, slots, mostLift);
    }

    /// <summary>
    /// How far a card's content sits above the card's own rectangle: the hover lift while the card is hovered, 0
    /// otherwise; 0 too when the content was not read or sits further up than MostHoverLift.
    /// </summary>
    private static float LiftOf(RectangleF card, RectangleF content)
    {
        var lift = card.Top - content.Top;

        return content.Height > 0f && lift > 0f && lift <= MostHoverLift ? lift : 0f;
    }

    /// <summary>A card's visible reward icons, each moved down by the card's lift, to where it rests.</summary>
    private static List<FateCardReward> RewardsOf(Element icons, float lift)
    {
        var rewards = new List<FateCardReward>();

        foreach (var icon in Safe.ChildrenOf(icons) ?? [])
        {
            if (!Safe.Read(icon, static i => i.IsVisible, false))
                continue;

            var text = TextOf(ChildAt(icon, 3));

            if (string.IsNullOrWhiteSpace(text))
                continue;

            var copies = AuguryCatalogueLookup.CopiesNeededOf(text);

            var rect = Safe.Read(icon, static i => i.GetClientRectCache, default(RectangleF));

            if (rect.Width > 0f)
                rect.Y += lift;

            rewards.Add(new FateCardReward(text, AuguryCatalogueLookup.RewardShown(text, copies), copies, rect));
        }

        return rewards;
    }

    private static string TextOf(Element element) =>
        Safe.Read(element, static e => e?.GetText(MostTextLength), null);

    /// <summary>The element at a path of child indices below element, or null when a step is missing.</summary>
    private static Element ChildAt(Element element, params int[] path)
    {
        var at = element;

        foreach (var index in path)
        {
            var children = Safe.ChildrenOf(at);

            if (children == null || index >= children.Count)
                return null;

            at = children[index];
        }

        return at;
    }
}
