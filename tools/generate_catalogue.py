"""Write AuguryCatalogue.cs, the plugin's static Augury data, and AuguryTexts.cs, the texts the plugin matches the
interface by in each of the client's languages, from an export of the Augury tables.

    python tools/generate_catalogue.py <augury tables export .json> [AuguryCatalogue.cs] [AuguryTexts.cs]

The export is written by tools/export_augury_tables.mjs from the game's patch files (since 3.30.0c, 2026-11-18), or
was written by a development build of the plugin from memory (AuguryTableExport, not in the published plugin); both
decode the tables by poe-tool-dev/dat-schema into the same shape. Run this again after a patch changes the tables.
The output is plain C#, ASCII only, every line within 120 columns, so it builds the same inside the HUD as outside it.
"""

import json
import re
import sys
from datetime import datetime

WIDTH = 120


def literal(text):
    """A C# string literal, ASCII only: anything else as a \\u escape."""
    if text is None:
        return 'null'

    out = []

    for ch in text:
        if ch == '\\':
            out.append('\\\\')
        elif ch == '"':
            out.append('\\"')
        elif ch == '\n':
            out.append('\\n')
        elif ch == '\r':
            continue
        elif 0x20 <= ord(ch) < 0x7F:
            out.append(ch)
        else:
            out.append('\\u%04X' % ord(ch))

    return '"' + ''.join(out) + '"'


def wrapped(text, indent):
    """
    A string literal split into concatenated pieces so that no line passes WIDTH: at spaces, and inside a word only
    where the word alone is longer than a line (text without spaces, as Japanese and Chinese are written), never inside
    an escape.
    """
    whole = literal(text)

    if text is None or len(indent) + len(whole) + 2 <= WIDTH:
        return whole

    room = WIDTH - len(indent) - 8
    pieces, current = [], ''

    for word in re.findall(r'\S+\s*|\s+', literal(text)[1:-1]):
        if current and len(current) + len(word) > room:
            pieces.append(current)
            current = ''

        if len(word) > room:
            for token in re.findall(r'\\u[0-9A-F]{4}|\\.|.', word):
                if len(current) + len(token) > room:
                    pieces.append(current)
                    current = ''

                current += token
        else:
            current += word

    pieces.append(current)

    return ('\n' + indent + '+ ').join('"%s"' % p for p in pieces)


def strings(values, indent):
    if not values:
        return '[]'

    one_line = '[' + ', '.join(literal(v) for v in values) + ']'

    if len(indent) + len(one_line) + 2 <= WIDTH:
        return one_line

    inner = indent + '    '
    return '[\n' + ''.join(inner + wrapped(v, inner) + ',\n' for v in values) + indent + ']'


def mod_of(reference):
    return (reference or {}).get('mod') or {}


def family_of(reward_id):
    """The reward without the hard-mode prefix or the level-band digit: WarstaffTransformSpeed1 and 2 share one."""
    return re.sub(r'\d+$', '', without_hard_mode(reward_id))


def without_hard_mode(reward_id):
    return reward_id[len('HardMode'):] if reward_id.startswith('HardMode') else reward_id


#: Families kept split whatever the rule finds, each with why. A family's Id keys saved weights and Must take marks,
#: so a split, once shipped, stays.
KEEP_SPLIT = {
    # Split by the rule in 3.30.0c (both tier 3). 3.30.0.4 made the single Divine Orb need two copies (tier 2) and
    # left "additional Divine Orbs" at three, so the same-tier test no longer finds them.
    'CurrencyOpulentDivineOrb',
}


def families_of(rewards):
    """
    Each reward's family by Id: family_of, except where two rows of one family (outside the HardMode set) read
    differently at overlapping area levels and the same tier, or the family is in KEEP_SPLIT. Those are two rewards,
    not one reward's level bands, and each keeps its digit: CurrencyOpulentDivineOrb1 "an additional Divine Orb" and 2
    "additional Divine Orbs", both levels 68-100 (3.30.0c), the only such pair then. Elsewhere differing texts are one
    reward's level bands ("additional" at low levels, "many additional" above), one family, so no family that existed
    before changes. The tier test is needed: without it RarityHigh1 and 2 (overlapping levels, different tiers) would
    split (checked on the 3.30.0.3 and 3.30.0.4 tables, 2026-11-19).
    """
    by_family = {}

    for reward in rewards:
        by_family.setdefault(family_of(reward['Id']), []).append(reward)

    split = set(KEEP_SPLIT)

    for family, rows in by_family.items():
        normal = [r for r in rows if r['Unknown6'] != 3]

        for i, a in enumerate(normal):
            for b in normal[i + 1:]:
                if (a['Description'] != b['Description'] and a['Tier'] == b['Tier']
                        and a['MinLevel'] <= b['MaxLevel'] and b['MinLevel'] <= a['MaxLevel']):
                    split.add(family)

    return {r['Id']: without_hard_mode(r['Id']) if family_of(r['Id']) in split else family_of(r['Id'])
            for r in rewards}


def main():
    source = sys.argv[1]
    target = sys.argv[2] if len(sys.argv) > 2 else 'AuguryCatalogue.cs'
    export = json.load(open(source, encoding='utf-8'))
    tables = export['tables']

    factions = tables['AuguryFactions']['rows']
    fates = tables['AuguryCards']['rows']
    tiers = tables['AuguryCardTiers']['rows']
    rewards = tables['AuguryRewardOutcomes']['rows']

    faction_ids = [f['Id'] for f in factions]
    tiers_by_fate = {}

    for tier in tiers:
        tiers_by_fate.setdefault(tier['Card']['row'], []).append(tier)

    lines = []
    w = lines.append

    w('// Generated by tools/generate_catalogue.py on %s. Do not edit by hand: regenerate.'
      % datetime.now().strftime('%Y-%m-%d %H:%M'))
    w('// From %s (exported %s),'
      % (source.replace('\\', '/').split('/')[-1], export.get('exported', '?')))
    w('// decoded by %s.' % export.get('schema', 'dat-schema'))
    w('')
    w('namespace AutoAugury;')
    w('')
    w('/// <summary>')
    w('/// The Augury league\'s static data, read off the client\'s tables: the three factions, the 25 Fates with')
    w('/// their faction and three tiers (ability text and the monster mod each gives the boss), and the reward')
    w('/// outcomes an offer can roll. Generated by tools/generate_catalogue.py; regenerate after a patch.')
    w('///')
    w('/// Read, not inferred: every Fate has one faction, and every offer seen came from one faction')
    w('/// (2026-11-14). Observed, not read: a reward\'s tier is the copies it needs; the panel marks tier 2 rewards')
    w('/// "To claim this reward, invoke two copies of it" and a tier 3 reward (CurrencyOpulent1) "three copies".')
    w('/// HardMode rewards are a separate set (the table\'s mode value 3 against 2 for the rest, matching the Id')
    w('/// prefix in all rows), 205 of the 360. None has been seen offered; they are taken to be Ruthless\'s')
    w('/// (suspected, not read), and the plugin weights them by their family\'s weight like the rest.')
    w('/// </summary>')
    w('internal static class AuguryCatalogue')
    w('{')

    w('    public static readonly AuguryFaction[] Factions =')
    w('    [')

    for f in factions:
        w('        new(%s, %s, %s),' % (literal(f['Id']), literal(f['Name']), literal(f['Icon'])))

    w('    ];')
    w('')
    w('    public static readonly AuguryFate[] Fates =')
    w('    [')

    for fate in fates:
        faction = fate['Unknown4']
        faction_id = faction_ids[faction['row']] if faction and faction.get('table') == 'AuguryFactions' else None
        indent = '            '

        head = '        new(%s, %s, %s,' % (literal(fate['Id']), literal(fate['Name']), literal(faction_id))

        if len(head) + len(literal(fate['Art'])) + 2 <= WIDTH:
            w('%s %s,' % (head, literal(fate['Art'])))
        else:
            w(head)
            w('            %s,' % literal(fate['Art']))
        w('        [')

        for tier in sorted(tiers_by_fate.get(fate['_index'], []), key=lambda t: t['Tier']):
            mod = mod_of(tier['Mod'])
            w('            new(%d, %s,' % (tier['Tier'], wrapped(tier['Description'], indent + '    ')))
            w('                %s,' % wrapped(tier['Body'], indent + '    '))
            w('                %s, %s),' % (literal(mod.get('key')), strings(mod.get('stats', []), indent + '    ')))

        w('        ]),')

    w('    ];')
    w('')
    w('    public static readonly AuguryReward[] Rewards =')
    w('    [')

    families = families_of(rewards)

    for reward in rewards:
        mods = [mod_of(e).get('key') for e in reward['Unknown3'].get('elements', []) if mod_of(e).get('key')]
        achievements = [e.get('id') for e in reward['Achievements'].get('elements', []) if e and e.get('id')]
        indent = '            '

        w('        new(%s, %s, %d, %s,' % (literal(reward['Id']), literal(families[reward['Id']]), reward['Tier'],
                                       'true' if reward['Unknown6'] == 3 else 'false'))
        w('            %s,' % wrapped(reward['Description'], indent))
        w('            %d, %d, %s,' % (reward['MinLevel'], reward['MaxLevel'], literal(reward['Icon'])))
        w('            %s, %s),' % (strings(mods, indent), literal(achievements[0] if achievements else None)))

    w('    ];')
    w('}')
    w('')
    w('/// <summary>A faction: its table Id (Savage, Filth, Classic), the name the game shows, and its icon.</summary>')
    w('internal sealed record AuguryFaction(string Id, string Name, string Icon);')
    w('')
    w('/// <summary>')
    w('/// One Fate: its table Id, the name the game shows, its faction\'s Id, its card art, and its three tiers.')
    w('/// </summary>')
    w('internal sealed record AuguryFate(string Id, string Name, string FactionId, string Art, '
      'AuguryFateTier[] Tiers);')
    w('')
    w('/// <summary>')
    w('/// One tier of a Fate: the card\'s short text, the full text in the game\'s markup, and the monster mod the')
    w('/// boss gets, with its stats and values ("stat_id min - max").')
    w('/// </summary>')
    w('internal sealed record AuguryFateTier(int Tier, string Description, string Body, string ModKey, '
      'string[] ModStats);')
    w('')
    w('/// <summary>')
    w('/// One reward outcome: its table Id; its family (the Id without the HardMode prefix or level-band digit, so')
    w('/// one family is one reward across area levels; the digit kept where two rows read differently at the same')
    w('/// levels and tier, as the two Divine Orb rewards do); its tier; whether it is of the HardMode set; the text')
    w('/// the panel shows; the area levels it can roll at; its icon; the monster mods that make its drops; and the')
    w('/// challenge it counts towards.')
    w('/// </summary>')
    w('internal sealed record AuguryReward(string Id, string Family, int Tier, bool HardMode, string Description,')
    w('    int MinLevel, int MaxLevel, string Icon, string[] DropMods, string Achievement);')

    text = '\n'.join(lines) + '\n'
    long_lines = [i + 1 for i, line in enumerate(text.split('\n')) if len(line) > WIDTH]

    if long_lines:
        print('lines over %d columns: %s' % (WIDTH, long_lines[:20]))

    open(target, 'w', encoding='ascii', newline='\n').write(text)
    print('wrote %s: %d factions, %d fates, %d tiers, %d rewards, %d reward families'
          % (target, len(factions), len(fates), len(tiers), len(rewards), len(set(families_of(rewards).values()))))

    write_texts(export, source, sys.argv[3] if len(sys.argv) > 3 else 'AuguryTexts.cs')


def collapsed(text):
    """A text as AuguryCatalogueLookup compares texts: its whitespace runs made one space, trimmed."""
    return re.sub(r'\s+', ' ', text or '').strip()


def note_words(note):
    """A copies note's words without the colour markup around them: "<rgb(122,122,122)>{To claim ...}"."""
    match = re.fullmatch(r'<[^>]*>\{(.*)\}', note or '', re.DOTALL)
    return match.group(1) if match else note


def texts_of(language, texts, tables, families):
    """
    One language's texts as AuguryTexts holds them, with the reward texts two families share in it: the interface's
    strings; every Fate's name; each tier's full text that no other tier of its Fate shares in this language (a shared
    one could not tell the tiers apart, so it is left out); each reward text once per tier, with the first row that has
    it, as AuguryCatalogueLookup keeps it.
    """
    strings = texts['ClientStrings']
    fate_ids = [f['Id'] for f in tables['AuguryCards']['rows']]
    reward_tiers = {r['Id']: r['Tier'] for r in tables['AuguryRewardOutcomes']['rows']}

    bodies_by_fate = {}

    for tier in texts['AuguryCardTiers']:
        bodies_by_fate.setdefault(tier['Card'], []).append(tier)

    tier_texts = []

    for card, tiers in bodies_by_fate.items():
        bodies = [collapsed(t['Body']) for t in tiers]

        for t in sorted(tiers, key=lambda t: t['Tier']):
            if bodies.count(collapsed(t['Body'])) == 1:
                tier_texts.append((fate_ids[card], t['Tier'], t['Body']))

    hard_mode = {r['Id']: r['Unknown6'] == 3 for r in tables['AuguryRewardOutcomes']['rows']}
    reward_texts, rows_by_key = [], {}

    for row in texts['AuguryRewardOutcomes']:
        key = (collapsed(row['Description']), reward_tiers[row['Id']])

        if key not in rows_by_key:
            reward_texts.append((row['Id'], row['Description']))

        rows_by_key.setdefault(key, []).append(row['Id'])

    shared = {}

    for key, ids in rows_by_key.items():
        named = list(dict.fromkeys(families[i] for i in ids))

        if len(named) > 1:
            only_hard_mode = all(hard_mode[i] for i in ids)
            shared[key] = ', '.join(named) + (' (HardMode rows only)' if only_hard_mode else '')

    return {
        'language': language,
        'choosing': strings['AuguryWindowSelectCard'],
        'invoking': strings['AuguryWindowPlayCards'],
        'two': note_words(strings['AuguryCardRewardTwo']),
        'three': note_words(strings['AuguryCardRewardThree']),
        'label': strings['AuguryCardSelectionWorldDescription'],
        'fates': [(c['Id'], c['Name']) for c in texts['AuguryCards']],
        'tiers': tier_texts,
        'rewards': reward_texts,
        'shared': shared,
    }


def checked_across_languages(languages, tables, families):
    """
    Stops on a text that would make AuguryCatalogueLookup read one thing as another, since it matches every language
    at once: a Fate name two Fates have (in any languages, case ignored), a panel title that reads as both views, or a
    three-copies note found inside a two-copies note. Returns the reward texts two families share across languages,
    which the lookup takes by its first language and row, as warnings.
    """
    fate_of_name = {f['Id'].upper(): f['Id'] for f in tables['AuguryCards']['rows']}
    reward_tiers = {r['Id']: r['Tier'] for r in tables['AuguryRewardOutcomes']['rows']}

    for texts in languages:
        for fate_id, name in texts['fates']:
            known = fate_of_name.setdefault(name.strip().upper(), fate_id)

            if known != fate_id:
                raise SystemExit('%s: the Fate name "%s" is also %s' % (texts['language'], name, known))

    choosing = {collapsed(t['choosing']) for t in languages}
    invoking = {collapsed(t['invoking']) for t in languages}

    if choosing & invoking:
        raise SystemExit('a panel title reads as both views: %s' % sorted(choosing & invoking))

    for a in languages:
        for b in languages:
            if collapsed(a['three']) in collapsed(b['two']):
                raise SystemExit('%s three-copies note is inside the %s two-copies note' % (a['language'],
                                                                                         b['language']))

    family_of_key, warnings = {}, []

    for texts in languages:
        for reward_id, description in texts['rewards']:
            key = (collapsed(description), reward_tiers[reward_id])
            first = family_of_key.setdefault(key, (texts['language'], families[reward_id]))

            if first[1] != families[reward_id]:
                warnings.append('%s and %s, tier %d: %s (%s), %s (%s)' % (first[0], texts['language'], key[1],
                                                                         first[1], first[0], families[reward_id],
                                                                         texts['language']))

    return warnings


def write_texts(export, source, target):
    """Writes AuguryTexts.cs, the texts the plugin matches the interface by in each language, from the export."""
    tables = export['tables']
    families = families_of(tables['AuguryRewardOutcomes']['rows'])
    languages = [texts_of(name, texts, tables, families) for name, texts in export['languages'].items()]
    across = checked_across_languages(languages, tables, families)

    lines = []
    w = lines.append

    w('// Generated by tools/generate_catalogue.py on %s. Do not edit by hand: regenerate.'
      % datetime.now().strftime('%Y-%m-%d %H:%M'))
    w('// From %s (exported %s),'
      % (source.replace('\\', '/').split('/')[-1], export.get('exported', '?')))
    w('// decoded by %s.' % export.get('schema', 'dat-schema'))
    w('')
    w('namespace AutoAugury;')
    w('')
    w('/// <summary>')
    w('/// The texts the Fates panel and a Cauldron\'s ground label show, in each of the client\'s languages,')
    w('/// read off the game\'s translated tables (ClientStrings and the Augury tables, row for row with the')
    w('/// English ones): the panel\'s titles, the note it adds to a reward that needs two or three copies (its')
    w('/// words, without the colour markup), the Cauldron\'s label while its choice waits, every Fate\'s name,')
    w('/// the tiers\' full texts and the rewards\' texts. AuguryCatalogueLookup matches against every language')
    w('/// at once, so the client\'s language is never needed; the generator stops on a text that would read as')
    w('/// two things across languages. Generated with AuguryCatalogue, whose English texts the settings show.')
    w('///')
    w('/// Kept to what matching needs: a tier\'s full text only where no other tier of its Fate reads the same')
    w('/// in that language (the lookup could not tell those tiers apart), and a reward\'s text once per text')
    w('/// and tier, with the first row that has it, as the lookup keeps it.')

    shared = [(t['language'], key, named) for t in languages for key, named in t['shared'].items()]

    if shared or across:
        w('///')
        w('/// Reward texts two families share, where the lookup takes the first row\'s family:')

        for language, key, named in shared:
            w('/// %s, tier %d: %s.' % (language, key[1], named))

        for warning in across:
            w('/// %s.' % warning)

    w('/// </summary>')
    w('internal static class AuguryTexts')
    w('{')
    w('    public static readonly AuguryLanguageTexts[] Languages =')
    w('    [')

    indent = '            '

    for t in languages:
        w('        new(%s,' % literal(t['language']))
        w('            %s,' % wrapped(t['choosing'], indent))
        w('            %s,' % wrapped(t['invoking'], indent))
        w('            %s,' % wrapped(t['two'], indent))
        w('            %s,' % wrapped(t['three'], indent))
        w('            %s,' % wrapped(t['label'], indent))
        w('            [')

        for fate_id, name in t['fates']:
            one_line = '                new(%s, %s),' % (literal(fate_id), literal(name))

            if len(one_line) <= WIDTH:
                w(one_line)
            else:
                w('                new(%s,' % literal(fate_id))
                w('                    %s),' % wrapped(name, indent + '        '))

        w('            ],')
        w('            [')

        for fate_id, tier, body in t['tiers']:
            w('                new(%s, %d,' % (literal(fate_id), tier))
            w('                    %s),' % wrapped(body, indent + '        '))

        w('            ],')
        w('            [')

        for reward_id, description in t['rewards']:
            w('                new(%s,' % literal(reward_id))
            w('                    %s),' % wrapped(description, indent + '        '))

        w('            ]),')

    w('    ];')
    w('}')
    w('')
    w('/// <summary>')
    w('/// The texts of one client language (Language, the game\'s folder name for it): the Fates panel\'s')
    w('/// titles while choosing and at the boss, the words of its two- and three-copies notes, a waiting')
    w('/// Cauldron\'s label, and the Fates\' names, tiers\' full texts and rewards\' texts, each by its')
    w('/// catalogue Id.')
    w('/// </summary>')
    w('internal sealed record AuguryLanguageTexts(string Language, string ChoosingTitle, string InvokingTitle,')
    w('    string TwoCopiesNote, string ThreeCopiesNote, string CauldronLabel, AuguryFateName[] FateNames,')
    w('    AuguryTierText[] TierTexts, AuguryRewardText[] RewardTexts);')
    w('')
    w('/// <summary>A Fate\'s name in one language, by the Fate\'s Id in AuguryCatalogue.</summary>')
    w('internal sealed record AuguryFateName(string FateId, string Name);')
    w('')
    w('/// <summary>One tier\'s full text in one language, in the game\'s markup, by Fate Id and tier.</summary>')
    w('internal sealed record AuguryTierText(string FateId, int Tier, string Body);')
    w('')
    w('/// <summary>A reward\'s text in one language, by the reward\'s Id in AuguryCatalogue.</summary>')
    w('internal sealed record AuguryRewardText(string RewardId, string Description);')

    text = '\n'.join(lines) + '\n'
    long_lines = [i + 1 for i, line in enumerate(text.split('\n')) if len(line) > WIDTH]

    if long_lines:
        print('lines over %d columns: %s' % (WIDTH, long_lines[:20]))

    open(target, 'w', encoding='ascii', newline='\n').write(text)
    print('wrote %s: %d languages, %d KB' % (target, len(languages), len(text) // 1024))

    for language, key, named in shared:
        print('  %s, tier %d: one text for %s' % (language, key[1], named))

    for warning in across:
        print('  across languages: %s' % warning)


if __name__ == '__main__':
    main()
