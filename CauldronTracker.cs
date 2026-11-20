using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.Shared.Enums;

namespace AutoAugury;

/// <summary>A Cauldron's stage as the tracker knows it.</summary>
internal enum CauldronStage
{
    /// <summary>Seen, its choice never seen waiting: not started, mid-encounter, or finished unseen.</summary>
    NotOwed,

    /// <summary>Targetable, or its "Select a Fate" label is up: its encounter is done and its choice waits.</summary>
    Owed,

    /// <summary>Read valid with card_chosen set or its minimap icon hidden: picked from.</summary>
    Done,
}

/// <summary>A Cauldron of the area as last seen.</summary>
/// <param name="Distance">Its distance from the player; float.MaxValue while it is not in the entity list.</param>
/// <param name="GridPos">Where it was last seen in the entity list, kept after it leaves; null if never there.</param>
/// <param name="Targetable">Its Targetable as last read; null if never read.</param>
/// <param name="IconListed">Whether its icon is in the server's minimap icon list (ServerData.MinimapIcons).</param>
/// <param name="History">Its last changes as read, oldest first ("07:50:12 targetable True"), for the dump.</param>
internal sealed record TrackedCauldron(uint Id, string FactionId, CauldronStage Stage, float Distance,
    Vector2? GridPos, bool? Targetable, bool IconListed, IReadOnlyList<string> History);

/// <summary>
/// Follows the area's Cauldrons (Metadata/Terrain/Leagues/Augury/Objects/AuguryCardObject), so FateScoring knows how
/// many offers may still come. Read every half second from the Tick. What is known of an area instance is kept by its
/// area hash and picked up again on returning to it (EnterArea): leaving a map for the hideout and coming back had
/// emptied it, and the Cauldrons, far off on the return, read neither state nor faction (Belfry, 06:27:29 to 06:33:10,
/// one hash and the same Cauldron ids both times).
///
/// What is read, all through ExileCore: a Cauldron entity while it is in the entity list (Cauldrons stay in it far off,
/// 1280 away in the 05:35 dumps, but not always), its grid position, Targetable, its faction from its Animated base
/// object (AuguryWickerWell01 Savage, AuguryFilthWell01 Filth, AuguryWitchWell01 Classic, dumps of 2026-11-14), its
/// StateMachine's card_chosen, its MinimapIcon's hide; and the ground labels reading "Select a Fate", one per Cauldron
/// whose choice waits, which stay up off screen and out of range too (02:41:36).
///
/// Measured over every Cauldron in the dumps of 2026-11-14, near enough to read valid: not targetable before and during
/// its encounter, targetable while its choice waits, not targetable once picked from, with card_chosen 1 and its icon
/// hidden. Beyond about 250 a Cauldron reads not valid, and its components hold what they held when it went out of
/// range: Cauldron 1061, picked from after 05:57:46, read targetable True 470 away at 06:01:45. So the state is taken
/// from valid readings only, and a Cauldron is done once it reads card_chosen non-zero or its icon hidden: every picked
/// Cauldron read valid showed both, and no other showed either. Two rules were dropped for marking a Cauldron done
/// with its choice waiting (1209 at 07:52:44, valid, targetable True, card_chosen 0, icon shown): its label seen and
/// then gone, and Targetable seen true then false; which of them fired was not recorded. Each Cauldron keeps its last
/// changes (History) for the dump, to tell next time. Owed is targetable on its last valid read, or its label up. One
/// never seen in the entity list, by its label or by its icon is not known at all: the count errs low. A Cauldron's
/// grid position is kept after it leaves the list, for CauldronMapIcons.
///
/// The server's minimap icon list (ServerData.MinimapIcons: an entity id, a grid position and the icon) is read too,
/// and each AuguryEncounter icon adds its Cauldron: the game drew a Cauldron's icon on the large map well before the
/// entity reached the entity list, about 250 away (the user, 2026-11-14), so the list reaches further. Its position
/// stands until the entity's own is read. Whether the list holds every Cauldron of the map from the start, and whether
/// a Cauldron's icon leaves it at the pick, is not known yet (the dump lists it).
///
/// Towns and hideouts track nothing, and an icon left over from the area just left is not taken. In the Lush Hideout
/// (dump of 2026-11-14 20:57:01) the tracker knew Cauldrons 1071 and 2889 at grid positions far outside the hideout,
/// and drew them on its large map. Their histories held no entity-list or label reading, so the server list had added
/// them, yet it held no Cauldron icon at the dump. Suspected, not measured: the list still held the map's icons for a
/// moment after the area change. An icon is taken for a left-over when the last area left, passing over towns and
/// hideouts (so a short stop in the hideout between two maps is covered), listed one with the same entity id at the
/// same grid position, and this area has not tracked that id otherwise; an icon the area left listed only in its last
/// half second, unread, is not recognised. Each icon not taken is kept for the dump (IconsNotTracked) with when it
/// was listed after entering, to settle the cause.
/// </summary>
internal sealed class CauldronTracker
{
    private const string CauldronPath = "Metadata/Terrain/Leagues/Augury/Objects/AuguryCardObject";

    /// <summary>The Cauldron's minimap icon, as its MinimapIcon component and the icon table name it.</summary>
    public const string CauldronIconId = "AuguryEncounter";

    /// <summary>How often the entities and labels are read, in ms. Chosen: Cauldrons change at human speed.</summary>
    private const int ReadIntervalMs = 500;

    /// <summary>How many changes each Cauldron keeps for the dump. Chosen.</summary>
    private const int MostHistory = 16;

    private sealed class Tracked
    {
        public string FactionId;
        public bool Owed;
        public bool LabelUp;
        public bool? Valid;
        public bool? Targetable;
        public long? Chosen;
        public bool? IconHidden;
        public bool Done;
        public float Distance = float.MaxValue;
        public Vector2? GridPos;
        public bool GridPosFromEntity;
        public bool IconListed;

        /// <summary>Its icon's grid position as the server list last gave it; null if never listed.</summary>
        public Vector2? IconGrid;
        public readonly List<string> History = new();

        /// <summary>Adds a change to History, timed, dropping the oldest past MostHistory.</summary>
        public void Note(string change)
        {
            History.Add($"{DateTime.Now:HH:mm:ss} {change}");

            if (History.Count > MostHistory)
                History.RemoveAt(0);
        }
    }

    /// <summary>
    /// How many area instances are remembered; entering one more forgets the one entered longest ago. Chosen: a map,
    /// its boss arena, the hideout and a few more.
    /// </summary>
    private const int MostAreasRemembered = 8;

    /// <summary>Each remembered area instance's Cauldrons, by area hash.</summary>
    private readonly Dictionary<uint, Dictionary<uint, Tracked>> _byArea = new();

    /// <summary>The remembered area hashes, entered longest ago first.</summary>
    private readonly List<uint> _areasByEntry = new();

    private Dictionary<uint, Tracked> _cauldrons = new();
    private readonly Stopwatch _sinceRead = new();
    private List<TrackedCauldron> _published = [];

    /// <summary>The area instance's hash; 0 when unread, in a town or hideout, or before any area change.</summary>
    private uint _areaHash;

    /// <summary>Whether the area is a town or hideout, where nothing is tracked.</summary>
    private bool _inTownOrHideout;

    /// <summary>
    /// The Cauldron icons of the last area left other than a town or hideout, by entity id, at the grid positions its
    /// server list gave: an icon listed here with the same id at the same position is taken for a left-over.
    /// </summary>
    private Dictionary<uint, Vector2> _leftAreaIcons = new();

    /// <summary>Time since the area was entered; not running before the first area change.</summary>
    private readonly Stopwatch _sinceEntered = new();

    /// <summary>A Cauldron icon of the server list not tracked, why, and when it was listed after entering.</summary>
    private sealed class IconNotTracked
    {
        public Vector2 Grid;
        public string Reason;
        public double? FirstSeconds;
        public double? LastSeconds;
    }

    private const string LeftOverReason = "left over from the area left";
    private const string TownOrHideoutReason = "in a town or hideout";

    /// <summary>The Cauldron icons of the server list not tracked since the area was entered, by entity id.</summary>
    private readonly Dictionary<uint, IconNotTracked> _iconsNotTracked = new();

    private List<string> _publishedIconsNotTracked = [];

    /// <summary>
    /// On entering an area: picks up what was known of that instance (by its area hash), or starts it empty; an
    /// unread hash (0), a town or a hideout starts empty and is not kept. Distances, labels and the icon list are read
    /// again; Done, Targetable, faction, positions and History are kept. The icons of the area left are kept to tell
    /// left-overs by (_leftAreaIcons): a town or hideout, which tracks none, passes on the ones it was given, and the
    /// same instance entered again has none.
    /// </summary>
    public void EnterArea(uint areaHash, bool townOrHideout)
    {
        _sinceRead.Reset();
        _sinceEntered.Restart();
        _iconsNotTracked.Clear();

        if (areaHash != 0u && areaHash == _areaHash)
        {
            _leftAreaIcons = new Dictionary<uint, Vector2>();
        }
        else if (!_inTownOrHideout)
        {
            _leftAreaIcons = _cauldrons.Where(static c => c.Value.IconGrid != null)
                .ToDictionary(static c => c.Key, static c => c.Value.IconGrid.Value);
        }

        _inTownOrHideout = townOrHideout;
        _areaHash = townOrHideout ? 0u : areaHash;

        if (_areaHash == 0u)
        {
            _cauldrons = new Dictionary<uint, Tracked>();
        }
        else
        {
            if (!_byArea.TryGetValue(areaHash, out var known))
                _byArea[areaHash] = known = new Dictionary<uint, Tracked>();

            _areasByEntry.Remove(areaHash);
            _areasByEntry.Add(areaHash);

            while (_areasByEntry.Count > MostAreasRemembered)
            {
                _byArea.Remove(_areasByEntry[0]);
                _areasByEntry.RemoveAt(0);
            }

            _cauldrons = known;
        }

        foreach (var tracked in _cauldrons.Values)
        {
            tracked.Distance = float.MaxValue;
            tracked.LabelUp = false;
            tracked.IconListed = false;
            tracked.Note("area entered again");
        }

        Publish();
    }

    /// <summary>The Cauldrons as of the last read, in a list replaced on each read, so safe to draw from.</summary>
    public IReadOnlyList<TrackedCauldron> Cauldrons => _published;

    /// <summary>
    /// The Cauldron icons of the server list not tracked since the area was entered, for the dump: "id 1071 grid
    /// &lt;400, 732&gt; left over from the area left, listed 0.0 s to 0.5 s after entering".
    /// </summary>
    public IReadOnlyList<string> IconsNotTracked => _publishedIconsNotTracked;

    public void Update(GameController gc)
    {
        if (_sinceRead.IsRunning && _sinceRead.ElapsedMilliseconds < ReadIntervalMs)
            return;

        _sinceRead.Restart();

        var listed = ListedCauldrons(gc);

        if (_inTownOrHideout)
        {
            foreach (var (id, grid) in listed)
                NoteIconNotTracked(id, grid, IsLeftOver(id, grid) ? LeftOverReason : TownOrHideoutReason);

            Publish();
            return;
        }

        var owedRead = OwedIds(gc);
        var owed = owedRead ?? [];
        var inRange = new HashSet<uint>();
        var entities = Safe.Read(gc, static g => g.EntityListWrapper.Entities.ToList(), null) ?? [];

        foreach (var entity in entities)
        {
            if (Safe.Read(entity, static e => e.Path, null) != CauldronPath)
                continue;

            var id = Safe.Read(entity, static e => e.Id, 0u);

            if (id == 0u)
                continue;

            var tracked = TrackedOf(id, "in the entity list");

            inRange.Add(id);
            tracked.Distance = Safe.Read(entity, static e => e.DistancePlayer, float.MaxValue);
            if (Safe.Read(entity, static e => (Vector2?)e.GridPosNum, null) is { } entityGrid)
            {
                tracked.GridPos = entityGrid;
                tracked.GridPosFromEntity = true;
            }
            tracked.FactionId ??= FactionOf(entity);

            // Not valid, far off: its components hold what they held when it went out of range, so its state is not
            // taken. Its position and faction do not change.
            var valid = Safe.Read(entity, static e => e.IsValid, false);

            if (tracked.Valid != valid)
                tracked.Note(valid ? $"valid, {tracked.Distance:0} away" : $"not valid, {tracked.Distance:0} away");

            tracked.Valid = valid;

            if (!valid)
                continue;

            // An unreadable value leaves the last reading alone.
            if (Safe.Read(entity, static e => (bool?)e.IsTargetable, null) is { } targetable)
            {
                if (tracked.Targetable != targetable)
                    tracked.Note($"targetable {targetable}");

                tracked.Targetable = targetable;
            }

            if (Safe.Read(entity, static e => e.GetComponent<StateMachine>()?.States
                    ?.FirstOrDefault(s => s.Name == "card_chosen")?.Value, null) is { } chosen)
            {
                if (tracked.Chosen != chosen)
                    tracked.Note($"card_chosen {chosen}");

                tracked.Chosen = chosen;
            }

            if (Safe.Read(entity, static e => e.GetComponent<MinimapIcon>()?.IsHide, null) is { } iconHidden)
            {
                if (tracked.IconHidden != iconHidden)
                    tracked.Note($"icon hidden {iconHidden}");

                tracked.IconHidden = iconHidden;
            }

            if (!tracked.Done && (tracked.Chosen is { } c && c != 0L || tracked.IconHidden == true))
            {
                tracked.Done = true;
                tracked.Note(tracked.Chosen is { } d && d != 0L ? "done: card_chosen set" : "done: icon hidden");
            }
        }

        foreach (var id in owed)
            TrackedOf(id, "by its label");

        foreach (var (id, grid) in listed)
        {
            if (!_cauldrons.ContainsKey(id) && IsLeftOver(id, grid))
            {
                NoteIconNotTracked(id, grid, LeftOverReason);
                continue;
            }

            var tracked = TrackedOf(id, $"in the server icon list at <{grid.X:0}, {grid.Y:0}>");

            tracked.IconGrid = grid;

            if (!tracked.GridPosFromEntity)
                tracked.GridPos = grid;
        }

        foreach (var (id, tracked) in _cauldrons)
        {
            if (!inRange.Contains(id))
                tracked.Distance = float.MaxValue;

            tracked.IconListed = listed.ContainsKey(id);

            // Left as it was when the labels could not be read.
            if (owedRead != null && tracked.LabelUp != owed.Contains(id))
            {
                tracked.LabelUp = owed.Contains(id);
                tracked.Note(tracked.LabelUp ? "label up" : "label gone");
            }

            tracked.Owed = !tracked.Done && (tracked.LabelUp || tracked.Targetable == true);
        }

        Publish();
    }

    private void Publish()
    {
        _published = _cauldrons
            .Select(c => new TrackedCauldron(c.Key, c.Value.FactionId,
                c.Value.Done ? CauldronStage.Done : c.Value.Owed ? CauldronStage.Owed : CauldronStage.NotOwed,
                c.Value.Distance, c.Value.GridPos, c.Value.Targetable, c.Value.IconListed, c.Value.History.ToList()))
            .ToList();
        _publishedIconsNotTracked = _iconsNotTracked
            .Select(static i => $"id {i.Key} grid <{i.Value.Grid.X:0}, {i.Value.Grid.Y:0}> {i.Value.Reason}, " +
                                $"listed {SecondsText(i.Value.FirstSeconds)} to {SecondsText(i.Value.LastSeconds)} s " +
                                "after entering")
            .ToList();
    }

    private static string SecondsText(double? seconds) =>
        seconds?.ToString("0.0", CultureInfo.InvariantCulture) ?? "?";

    /// <summary>Seconds since the area was entered; null before the first area change.</summary>
    private double? SecondsSinceEntered() =>
        _sinceEntered.IsRunning ? _sinceEntered.Elapsed.TotalSeconds : null;

    /// <summary>Whether the area just left listed a Cauldron icon with this entity id at this grid position.</summary>
    private bool IsLeftOver(uint id, Vector2 grid) => _leftAreaIcons.TryGetValue(id, out var left) && left == grid;

    /// <summary>Keeps a Cauldron icon not tracked for the dump, with when it was first and last listed.</summary>
    private void NoteIconNotTracked(uint id, Vector2 grid, string reason)
    {
        var seconds = SecondsSinceEntered();

        if (!_iconsNotTracked.TryGetValue(id, out var icon))
            _iconsNotTracked[id] = icon = new IconNotTracked { FirstSeconds = seconds };

        icon.Grid = grid;
        icon.Reason = reason;
        icon.LastSeconds = seconds;
    }

    /// <summary>
    /// How many offers may still come from Cauldrons of a faction: the tracked ones not done, of that faction or of
    /// one not known, less the one the player is choosing at now (the nearest one not done, while choosing).
    /// </summary>
    public int OffersToCome(string factionId, bool choosingNow)
    {
        var open = _cauldrons.Where(c => !c.Value.Done).ToList();
        var current = choosingNow ? open.OrderBy(static c => c.Value.Distance).FirstOrDefault().Key : 0u;

        return open.Count(c => c.Key != current &&
                               (c.Value.FactionId == null || factionId == null || c.Value.FactionId == factionId));
    }

    /// <summary>The Cauldrons in the server's minimap icon list, by entity id, with the icon's grid position.</summary>
    private static Dictionary<uint, Vector2> ListedCauldrons(GameController gc)
    {
        var listed = new Dictionary<uint, Vector2>();
        var icons = Safe.Read(gc, static g => g.IngameState.ServerData.MinimapIcons, null) ?? [];

        foreach (var icon in icons)
        {
            if (!IsCauldronIcon(icon))
                continue;

            var id = Safe.Read(icon, static i => i.EntityId, 0u);
            var grid = Safe.Read(icon, static i => i.GridPosition, default);

            if (id != 0u)
                listed[id] = new Vector2(grid.X, grid.Y);
        }

        return listed;
    }

    /// <summary>Whether a server minimap icon is a Cauldron's: by the icon table's Id, else by its index.</summary>
    public static bool IsCauldronIcon(ExileCore.PoEMemory.MemoryObjects.ServerDataMinimapIcon icon) =>
        Safe.Read(icon, static i => i.MinimapIcon?.Id, null) is { } iconId
            ? iconId == CauldronIconId
            : Safe.Read(icon, static i => i.MinimapIconIndex, default) == MapIconsIndex.AuguryEncounter;

    /// <summary>
    /// The tracked Cauldron with this id, tracked from now if it was not, noting where it was first seen and how long
    /// after entering the area ("first seen in the entity list, 0.4 s after entering").
    /// </summary>
    private Tracked TrackedOf(uint id, string firstSeen)
    {
        if (_cauldrons.TryGetValue(id, out var tracked))
            return tracked;

        _cauldrons[id] = tracked = new Tracked();

        var seconds = SecondsSinceEntered();

        tracked.Note($"first seen {firstSeen}" + (seconds == null ? "" : $", {SecondsText(seconds)} s after entering"));
        return tracked;
    }

    /// <summary>
    /// The Cauldrons whose ground label reads as a waiting Cauldron's in any of the client's languages ("Select a Fate"
    /// in English, AuguryCatalogueLookup.IsCauldronLabel), by id; null when labels are unread.
    /// </summary>
    private static HashSet<uint> OwedIds(GameController gc)
    {
        var ids = new HashSet<uint>();
        var labels = Safe.Read(gc, static g => g.IngameState.IngameUi.ItemsOnGroundLabels.ToList(), null);

        if (labels == null)
            return null;

        foreach (var label in labels)
        {
            var entity = Safe.Read(label, static l => l.ItemOnGround, null);

            if (Safe.Read(entity, static e => e?.Path, null) != CauldronPath)
                continue;

            if (HasCauldronLabel(Safe.Read(label, static l => l.Label, null), 0))
                ids.Add(Safe.Read(entity, static e => e.Id, 0u));
        }

        ids.Remove(0u);
        return ids;
    }

    /// <summary>
    /// Whether an element or one of its descendants, three levels down at most, reads as a waiting Cauldron's label.
    /// </summary>
    private static bool HasCauldronLabel(Element element, int depth)
    {
        if (element == null || depth > 3)
            return false;

        if (AuguryCatalogueLookup.IsCauldronLabel(Safe.Read(element, static e => e.Text, null)))
            return true;

        return (Safe.ChildrenOf(element) ?? []).Any(c => HasCauldronLabel(c, depth + 1));
    }

    /// <summary>
    /// The faction of a Cauldron from its Animated base object: "WickerWell" Savage, "FilthWell" Filth, "WitchWell"
    /// Classic (City Square, 2026-11-14 05:57, offering Classic Fates); null otherwise.
    /// </summary>
    private static string FactionOf(ExileCore.PoEMemory.MemoryObjects.Entity entity)
    {
        var path = Safe.Read(entity, static e => e.GetComponent<Animated>()?.BaseAnimatedObjectEntity?.Path, null);

        if (path == null)
            return null;

        if (path.Contains("WickerWell", StringComparison.OrdinalIgnoreCase))
            return "Savage";

        if (path.Contains("FilthWell", StringComparison.OrdinalIgnoreCase))
            return "Filth";

        return path.Contains("WitchWell", StringComparison.OrdinalIgnoreCase) ? "Classic" : null;
    }
}
