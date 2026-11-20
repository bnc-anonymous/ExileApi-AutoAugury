// Exports the Augury tables from the game's patch files into the JSON shape tools/generate_catalogue.py reads, without
// reading the game's memory. The files come from GGG's patch server for the given patch version (the standalone
// install keeps them inside Content.ggpk), decoded by poe-tool-dev/dat-schema with SnosMe's pathofexile-dat.
//
//   1. In a scratch folder outside the repository: npm install pathofexile-dat@16.0.1
//      and download https://github.com/poe-tool-dev/dat-schema/releases/download/latest/schema-poe1.min.json
//   2. Copy this file there and run: node export_augury_tables.mjs schema-poe1.min.json <cache dir> <patch> <out.json>
//      The patch version is in https://raw.githubusercontent.com/poe-tool-dev/latest-patch-version/main/latest.txt.
//   3. python tools/generate_catalogue.py <out.json>
//
// Each table's columns are laid out by the schema and the row length checked against the file, so a schema behind a
// patch stops the export rather than misreading it. Unnamed columns are named Unknown1, Unknown2... by position among
// the unnamed ones, as the memory export (AuguryTableExport) named them. First run for 3.30.0.3 (2026-11-18): the
// tiers' mod stats matched the memory export of 2026-11-14 in all but the one stat the patch changed.
//
// The export's "languages" hold the texts the plugin matches the interface by, in each of the client's languages
// (LANGUAGES), from the translated tables, for AuguryTexts.cs; since 3.30.0.4 (2026-11-20).
import { FileLoader, toCdnUrl } from 'pathofexile-dat/bundles.js';
import { readDatFile, getFieldReader } from 'pathofexile-dat/dat.js';
import fs from 'node:fs/promises';
import path from 'node:path';

const [schemaPath, cacheRoot, patch, outPath] = process.argv.slice(2);

if (!outPath) {
    console.error('usage: node export_augury_tables.mjs <schema-poe1.min.json> <cache dir> <patch> <out.json>');
    process.exit(1);
}

const schema = JSON.parse(await fs.readFile(schemaPath, 'utf-8'));
const cacheDir = path.join(cacheRoot, patch);

await fs.mkdir(cacheDir, { recursive: true });

/** Bundles from the patch server, kept in the cache folder so a second run downloads nothing. */
const bundleLoader = {
    async fetchFile(name) {
        const cached = path.join(cacheDir, name.replace(/\//g, '@'));

        try {
            return new Uint8Array(await fs.readFile(cached));
        } catch {
            // Not cached yet.
        }

        console.log(`downloading ${name}`);
        const response = await fetch(toCdnUrl(patch, name));

        if (!response.ok)
            throw new Error(`patch server answered ${response.status} for ${name}`);

        const bytes = new Uint8Array(await response.arrayBuffer());
        await fs.writeFile(cached, bytes);
        return bytes;
    },
};

const loader = await FileLoader.create(bundleLoader);

/** Field sizes in a .datc64 row, by schema type; an array of anything is 16 bytes. */
const SIZE = { string: 8, foreignrow: 16, row: 8, bool: 1, i16: 2, u16: 2, i32: 4, u32: 4, f32: 4, enumrow: 4 };

function typeOf(column) {
    return {
        array: column.array,
        interval: column.interval,
        integer: column.type === 'u16' ? { unsigned: true, size: 2 }
            : column.type === 'u32' ? { unsigned: true, size: 4 }
            : column.type === 'i16' ? { unsigned: false, size: 2 }
            : (column.type === 'i32' || column.type === 'enumrow') ? { unsigned: false, size: 4 }
            : undefined,
        decimal: column.type === 'f32' ? { size: 4 } : undefined,
        string: column.type === 'string' ? {} : undefined,
        boolean: column.type === 'bool' ? {} : undefined,
        key: (column.type === 'row' || column.type === 'foreignrow')
            ? { foreign: column.type === 'foreignrow' }
            : undefined,
    };
}

/**
 * A table's rows, each with every column that has a readable type, and _index. The language is a folder under Data
 * ("French"), where the client keeps a translated copy of each table with text in it; English is Data itself.
 */
async function table(name, language = ENGLISH) {
    const candidates = schema.tables.filter(t => t.name === name);
    const tableSchema = candidates.find(t => t.validFor & 1) ?? candidates[0];

    if (!tableSchema)
        throw new Error(`no schema for ${name}`);

    const folder = language === ENGLISH ? 'Data' : `Data/${language}`;
    const dat = readDatFile('.datc64', await loader.getFileContents(`${folder}/${name}.datc64`));
    let offset = 0;
    let unnamed = 0;

    const columns = tableSchema.columns.map(column => {
        const length = column.array ? 16 : SIZE[column.type] * (column.interval ? 2 : 1);

        if (!Number.isFinite(length))
            throw new Error(`${name}: no size known for column type ${column.type}`);

        const header = { name: column.name || `Unknown${++unnamed}`, offset, column };
        offset += length;
        return header;
    });

    if (dat.rowCount > 0 && offset !== dat.rowLength)
        throw new Error(`${name}: the schema's row is ${offset} bytes, the file's ${dat.rowLength}`);

    const readers = columns.map(header => {
        const type = typeOf(header.column);
        const readable = type.key || type.string || type.boolean || type.integer || type.decimal;
        return readable ? getFieldReader({ name: header.name, offset: header.offset, type }, dat) : null;
    });

    const rows = [];

    for (let index = 0; index < dat.rowCount; index++) {
        const row = { _index: index };
        columns.forEach((header, i) => { if (readers[i]) row[header.name] = readers[i](index); });
        rows.push(row);
    }

    console.log(`${folder}/${name}: ${dat.rowCount} rows of ${dat.rowLength} bytes`);
    return { rowLength: dat.rowLength, rows };
}

const ENGLISH = 'English';

/**
 * The client's languages, by their folder under Data: every one the 3.30.0.4 bundle index holds a translated
 * AuguryCards for (2026-11-20); "Simplified Chinese" has none (that client is published apart).
 */
const LANGUAGES = [ENGLISH, 'French', 'German', 'Japanese', 'Korean', 'Portuguese', 'Russian', 'Spanish', 'Thai',
    'Traditional Chinese'];

/**
 * The ClientStrings rows the plugin compares the interface's text with: the Fates panel's titles while choosing and
 * at the boss, the notes the panel adds to a reward that needs two or three copies, and a Cauldron's ground label
 * while its choice waits.
 */
const CLIENT_STRINGS = ['AuguryWindowSelectCard', 'AuguryWindowPlayCards', 'AuguryCardRewardTwo',
    'AuguryCardRewardThree', 'AuguryCardSelectionWorldDescription'];

/** Stops the export when a translated table's rows are not the English table's, row for row. */
function checkSameRows(language, name, english, translated, keyOf) {
    if (translated.rows.length !== english.rows.length)
        throw new Error(`${language} ${name}: ${translated.rows.length} rows, English ${english.rows.length}`);

    translated.rows.forEach((row, i) => {
        if (keyOf(row) !== keyOf(english.rows[i]))
            throw new Error(`${language} ${name} row ${i}: ${keyOf(row)}, English ${keyOf(english.rows[i])}`);
    });
}

/**
 * The texts the Fates panel and a Cauldron's label show, in one language: the Fates' names, the tiers' full texts
 * and the rewards' texts, row for row with the English tables (checked by Id), and the CLIENT_STRINGS by Id.
 */
async function textsIn(language) {
    const translatedCards = await table('AuguryCards', language);
    const translatedTiers = await table('AuguryCardTiers', language);
    const translatedRewards = await table('AuguryRewardOutcomes', language);
    const clientStrings = await table('ClientStrings', language);

    checkSameRows(language, 'AuguryCards', cards, translatedCards, r => r.Id);
    checkSameRows(language, 'AuguryCardTiers', tiers, translatedTiers, r => `${r.Card}/${r.Tier}`);
    checkSameRows(language, 'AuguryRewardOutcomes', rewards, translatedRewards, r => r.Id);

    const strings = {};

    for (const id of CLIENT_STRINGS) {
        const row = clientStrings.rows.find(r => r.Id === id);

        if (!row)
            throw new Error(`${language} ClientStrings: no row ${id}`);

        strings[id] = row.Text;
    }

    return {
        ClientStrings: strings,
        AuguryCards: translatedCards.rows.map(r => ({ Id: r.Id, Name: r.Name })),
        AuguryCardTiers: translatedTiers.rows.map(r => ({ Card: r.Card, Tier: r.Tier, Body: r.Body })),
        AuguryRewardOutcomes: translatedRewards.rows.map(r => ({ Id: r.Id, Description: r.Description })),
    };
}

const factions = await table('AuguryFactions');
const cards = await table('AuguryCards');
const tiers = await table('AuguryCardTiers');
const rewards = await table('AuguryRewardOutcomes');
const mods = await table('Mods');
const stats = await table('Stats');
const achievements = await table('AchievementItems');

/** A Mods reference as the memory export wrote one; with stats, "stat_id min - max" for each stat the mod has. */
function modReference(index, withStats) {
    if (index == null)
        return null;

    const mod = mods.rows[index];
    const reference = { table: 'Mods', row: index, id: mod.Id, mod: { key: mod.Id } };

    if (withStats) {
        reference.mod.stats = [];

        for (let k = 1; k <= 8; k++) {
            const stat = mod[`StatsKey${k}`];

            if (stat != null)
                reference.mod.stats.push(`${stats.rows[stat].Id} ${mod[`Stat${k}Min`]} - ${mod[`Stat${k}Max`]}`);
        }
    }

    return reference;
}

const reference = (tableName, rows, index) =>
    index == null ? null : { table: tableName, row: index, id: rows[index]?.Id };

const languages = {};

for (const language of LANGUAGES)
    languages[language] = await textsIn(language);

const exported = {
    exported: `${patch} patch files, ${new Date().toISOString().slice(0, 16).replace('T', ' ')} UTC`,
    schema: `poe-tool-dev/dat-schema schema-poe1.min.json (createdAt ${schema.createdAt}), read by pathofexile-dat`,
    tables: {
        AuguryFactions: { rowLength: factions.rowLength, decoded: true, rows: factions.rows },
        AuguryCards: {
            rowLength: cards.rowLength,
            decoded: true,
            rows: cards.rows.map(r => ({ ...r, Unknown4: reference('AuguryFactions', factions.rows, r.Unknown4) })),
        },
        AuguryCardTiers: {
            rowLength: tiers.rowLength,
            decoded: true,
            rows: tiers.rows.map(r => ({
                ...r,
                Card: reference('AuguryCards', cards.rows, r.Card),
                Mod: modReference(r.Mod, true),
            })),
        },
        AuguryRewardOutcomes: {
            rowLength: rewards.rowLength,
            decoded: true,
            rows: rewards.rows.map(r => ({
                ...r,
                Unknown3: { elements: (r.Unknown3 ?? []).map(i => modReference(i, false)) },
                Achievements: {
                    elements: (r.Achievements ?? []).map(i => reference('AchievementItems', achievements.rows, i)),
                },
            })),
        },
    },
    languages,
};

await fs.writeFile(outPath, JSON.stringify(exported, null, 1));
console.log(`wrote ${outPath}`);
