// Builds the Duration Pad page for Touch Portal: src/Pages/DoriDeck Duration Pad.tpz2 (import via Pages > Import page).
// Usage: node build/duration-pad/make-page.mjs   (no npm packages; rerun after changing the layout or icons)
// The .tpz2 layout (data.json, version.json, img/) and the button fields follow pages exported from TP 4.x.
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const durationsDir = path.join(root, 'src/Assets/Durations');
const output = path.join(root, 'src/Pages/DoriDeck Duration Pad.tpz2');

const pluginId = 'com.sarti2004.dorideck';
const state = name => `${pluginId}.state.${name}`;

// Colours from the original pad's style.
const Legend = [0xe6, 0xe4, 0xde];
const Amber = [0xff, 0xb5, 0x47];
const Red = [0xff, 0x4b, 0x3e];
const Panel = 0x3a3b3e;
const Face = 0x303237;
const FaceRim = 0x5e6168;

const Durations = [
	['Breve', 'breve'],
	['Whole', 'whole'],
	['Half', 'half'],
	['Quarter', 'quarter'],
	['Eighth', 'eighth'],
	['16th', 'sixteenth'],
	['32nd', 'thirty-second'],
	['64th', 'sixty-fourth'],
];
// Must match DoricoStatePublisher.DurationValues.
const DurationValues = ['', ...Durations.map(d => d[0]), ...Durations.map(d => d[0] + ' dotted')];

// ---- PNG ----

const crcTable = Array.from({ length: 256 }, (_, n) => {
	let c = n;
	for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
	return c >>> 0;
});
const crc32 = buf => {
	let c = 0xffffffff;
	for (const b of buf) c = crcTable[(c ^ b) & 255] ^ (c >>> 8);
	return (c ^ 0xffffffff) >>> 0;
};

// Only 8-bit RGBA, non-interlaced: the format of the duration glyphs.
function readPng(file) {
	const b = fs.readFileSync(file);
	let pos = 8, w, h;
	const idat = [];
	while (pos < b.length) {
		const len = b.readUInt32BE(pos), type = b.toString('ascii', pos + 4, pos + 8), data = b.subarray(pos + 8, pos + 8 + len);
		if (type === 'IHDR') {
			w = data.readUInt32BE(0);
			h = data.readUInt32BE(4);
			if (data[8] !== 8 || data[9] !== 6 || data[12] !== 0) throw new Error(`${file}: expected 8-bit RGBA, non-interlaced`);
		} else if (type === 'IDAT') idat.push(data);
		else if (type === 'IEND') break;
		pos += 12 + len;
	}
	const raw = zlib.inflateSync(Buffer.concat(idat));
	const stride = w * 4, px = Buffer.alloc(w * h * 4);
	let prev = Buffer.alloc(stride);
	for (let y = 0; y < h; y++) {
		const filter = raw[y * (stride + 1)], line = raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1));
		const out = px.subarray(y * stride, (y + 1) * stride);
		for (let x = 0; x < stride; x++) {
			const a = x >= 4 ? out[x - 4] : 0, up = prev[x], c = x >= 4 ? prev[x - 4] : 0;
			let v = line[x];
			if (filter === 1) v += a;
			else if (filter === 2) v += up;
			else if (filter === 3) v += (a + up) >> 1;
			else if (filter === 4) {
				const p = a + up - c, pa = Math.abs(p - a), pb = Math.abs(p - up), pc = Math.abs(p - c);
				v += pa <= pb && pa <= pc ? a : pb <= pc ? up : c;
			}
			out[x] = v & 255;
		}
		prev = out;
	}
	return { w, h, px };
}

function pngBytes({ w, h, px }) {
	const raw = Buffer.alloc(h * (w * 4 + 1));
	for (let y = 0; y < h; y++) px.copy(raw, y * (w * 4 + 1) + 1, y * w * 4, (y + 1) * w * 4);
	const chunk = (type, data) => {
		const td = Buffer.concat([Buffer.from(type, 'ascii'), data]);
		const len = Buffer.alloc(4), crc = Buffer.alloc(4);
		len.writeUInt32BE(data.length);
		crc.writeUInt32BE(crc32(td));
		return Buffer.concat([len, td, crc]);
	};
	const ihdr = Buffer.alloc(13);
	ihdr.writeUInt32BE(w, 0);
	ihdr.writeUInt32BE(h, 4);
	ihdr[8] = 8;
	ihdr[9] = 6;
	return Buffer.concat([
		Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
		chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(raw, { level: 9 })), chunk('IEND', Buffer.alloc(0)),
	]);
}

// Recolours a white glyph, keeping its alpha (the original tinted the same images at runtime).
function tint(image, [r, g, b]) {
	const px = Buffer.from(image.px);
	for (let i = 0; i < px.length; i += 4) [px[i], px[i + 1], px[i + 2]] = [r, g, b];
	return { ...image, px };
}

// ---- Drawn glyphs: 120 x 120, shapes as inside(x, y) tests, 4 x 4 supersampled ----

const Size = 120;
function draw(shapes) {
	const px = Buffer.alloc(Size * Size * 4);
	for (const [inside, [r, g, b]] of shapes) {
		for (let y = 0; y < Size; y++) for (let x = 0; x < Size; x++) {
			let hits = 0;
			for (let sy = 0; sy < 4; sy++) for (let sx = 0; sx < 4; sx++) if (inside(x + (sx + 0.5) / 4, y + (sy + 0.5) / 4)) hits++;
			if (!hits) continue;
			const i = (y * Size + x) * 4, a = hits / 16, under = px[i + 3] / 255, out = a + under * (1 - a);
			for (const [k, v] of [[0, r], [1, g], [2, b]]) px[i + k] = Math.round((v * a + px[i + k] * under * (1 - a)) / out);
			px[i + 3] = Math.round(out * 255);
		}
	}
	return { w: Size, h: Size, px };
}

const polygon = points => (x, y) => {
	let inside = false;
	for (let i = 0, j = points.length - 1; i < points.length; j = i++) {
		const [xi, yi] = points[i], [xj, yj] = points[j];
		if (yi > y !== yj > y && x < ((xj - xi) * (y - yi)) / (yj - yi) + xi) inside = !inside;
	}
	return inside;
};
const circle = (cx, cy, r) => (x, y) => (x - cx) ** 2 + (y - cy) ** 2 <= r * r;
const roundedSquare = (c, half, radius) => (x, y) => {
	const dx = Math.max(Math.abs(x - c) - (half - radius), 0), dy = Math.max(Math.abs(y - c) - (half - radius), 0);
	return dx * dx + dy * dy <= radius * radius;
};
// An arrow pointing right, rotated by quarter turns around the centre.
const arrow = turns => {
	const points = [[30, 54], [62, 54], [62, 36], [92, 60], [62, 84], [62, 66], [30, 66]];
	const rotate = ([x, y]) => {
		for (let t = 0; t < turns; t++) [x, y] = [Size - y, x];
		return [x, y];
	};
	return polygon(points.map(rotate));
};
const glyphs = {
	play: draw([[polygon([[44, 34], [44, 86], [88, 60]]), Legend]]),
	stop: draw([[roundedSquare(60, 20, 4), Legend]]),
	record: draw([[circle(60, 60, 22), Red]]),
	up: draw([[arrow(3), Legend]]),
	down: draw([[arrow(1), Legend]]),
	left: draw([[arrow(2), Legend]]),
	right: draw([[arrow(0), Legend]]),
};

// ---- Page ----

// Icon file names follow TP's own export naming (millisecond timestamps).
const images = new Map();
function image(key, pixels) {
	if (!images.has(key)) images.set(key, { name: `${1800000000000 + images.size}.png`, bytes: pngBytes(pixels) });
	return images.get(key).name;
}

const durationIcon = (file, colour, key) => image(key, tint(readPng(path.join(durationsDir, `${file}.png`)), colour));
const glyph = name => image(name, glyphs[name]);
const noteInputIcon = lit => durationIcon('8th plus', lit ? Amber : Legend, `noteInput-${lit}`);

const argb = rgb => (0xff000000 | rgb) >> 0;

// Fixed ids so regenerating the page doesn't change anything that didn't move.
let buttonCount = 0;
const buttonId = () => `u${String(100 + buttonCount++).padStart(3, '0')}dor${String(100 + buttonCount).padStart(3, '0')}pad`;

const runCommand = command => ({
	kPlugType: 2,
	kmald: JSON.stringify([{ data: [{ lineFormat: `Run Dorico command {$${pluginId}.run_command.command$} in all flows {$${pluginId}.run_command.all_flows$}` }], language: 'default' }]),
	kCTS: '',
	kID: `${pluginId}.action.run_command`,
	kInline: 'false',
	kHHF: 'false',
	kcD: -14470582,
	kPID: pluginId,
	kData: [
		{ default: '', id: `${pluginId}.run_command.command`, type: 'text' },
		{ default: false, id: `${pluginId}.run_command.all_flows`, type: 'switch' },
	],
	kVals: [
		{ VAL: command, ID: `${pluginId}.run_command.command`, TYPE: 'text' },
		{ VAL: 'Off', ID: `${pluginId}.run_command.all_flows`, TYPE: 'switch' },
	],
	kmold: '',
	kStatic: 'false',
	kcL: -12952948,
	kIEn: true,
	kET: 0,
	KEY_TYPE: 'PLUGIN_ACTION',
	kName: 'Run Dorico command',
	kpip: '',
});

// The On Hold action, saved in the button's HA field; TP sends it down and up rather than action.
const keyChoices = [...Durations.map(d => d[0]), 'Play', 'Left', 'Right'];
const holdKey = key => ({
	kPlugType: 2,
	kmald: JSON.stringify([{ data: [{ lineFormat: `Duration Pad key {$${pluginId}.duration_pad_key.key$}` }], language: 'default' }]),
	kCTS: '',
	kID: `${pluginId}.action.duration_pad_key`,
	kInline: 'false',
	kHHF: 'true',
	kcD: -14470582,
	kPID: pluginId,
	kData: [{ default: 'Quarter', id: `${pluginId}.duration_pad_key.key`, valueChoices: keyChoices, type: 'choice' }],
	kVals: [{ VAL: key, ID: `${pluginId}.duration_pad_key.key`, TYPE: 'choice' }],
	kmold: '',
	kStatic: 'false',
	kcL: -12952948,
	kIEn: true,
	kET: 0,
	KEY_TYPE: 'PLUGIN_ACTION',
	kName: 'Duration Pad key (long press)',
	kpip: '',
});

const onState = (stateId, value) => ({
	KEY_STATE_CACHED_NAME: '',
	KEY_STATE_DESCRIPTION: 'On state changes to',
	kPSC: true,
	kIEn: true,
	KEY_IS_NOT_EQUAL: false,
	kCSC: 0,
	KEY_STATE: value,
	KEY_STATE_ID: stateId,
	KEY_TYPE: 'ON_STATE_AWARENESS_CHANGE',
	kICustom: false,
});

const setIcon = file => ({
	KEY_IS_CHANGE_IS_FULL_ICON: false,
	kIAs: [],
	kiTF: false,
	kiTOV: false,
	KEY_IS_CHANGE_TITLE: false,
	KEY_IS_CHANGE_TEXT_COLOR: false,
	KEY_ALT_FILENAME: '',
	KEY_FILENAME: file,
	KEY_IS_CHANGE_ICON: true,
	kiBD: false,
	KEY_IS_CHANGE_ALIGN_HOR: false,
	KEY_IS_CHANGE_IS_ROUNDED: false,
	kIEn: true,
	kC: false,
	kiTOH: false,
	KEY_TYPE: 'CHANGE_BUTTON_VISUALS_ACTION',
	KEY_IS_CHANGE_BG_COLOR: false,
	KEY_IS_CHANGE_IS_TRANSPARENT: false,
	KEY_IS_CHANGE_ALIGN_VERT: false,
});

// One "state changes to" event per value, each followed by the icon that value shows.
const iconEvents = (stateId, iconFor) => iconFor.flatMap(([value, file]) => [onState(stateId, value), setIcon(file)]);

// Field set copied from a TP 4.x button export; only the marked fields vary. A key with a long press has only its
// On Hold action: anything in On Pressed would also run on release, and a plain duration there drops the dot.
const button = ({ command, hold, icon, title = '', events = [], rows = 1, cols = 1 }) => ({
	kSCM: 25, kIAPBKC: -14803426, ITS: true, kWvKA: 0, kKnZinT: false, IiS: false, kSCC: -4611631,
	id: buttonId(),
	GUdata: '', kCT: 1, TELS: 5, kSCI: '', kKnRotIImg2: { a: [], f: '', fe: '' }, kIAs: [], GUid: -1, kKnSt: 0, kKnBGT: 0,
	kSVP: 0, kSVAC: -10575407, inB: false, kSCTM: 0, inC: 0, FF: 'Default',
	A: hold ? [] : [runCommand(command)],
	...(hold && { HA: holdKey(hold) }),
	kMB: 0, kWvZ: 1, BD: 1, kKnFC3: -7951228, C: [],
	BE: argb(Face), BG: argb(Face),
	E: events,
	I: icon ?? '',
	BiR: true, kSCTY: 0, kML: 0, BiT: true, kSCHS: false, kKnFC1: -11040415, kKnFC2: -4073532, kKnRotIS: 5, inS: '',
	kMR: 0, kMT: 0,
	T: title,
	kKnMR: '360', kKnS: '18.0', kSCAC: argb(FaceRim), kWvRB: 0, kKnT: 0, kSCHRC: false, THO: 4, kWvU: '', kSCIUFATS: false,
	kKnIStV: false, kSIP: 0, kKnRotTh: '10.0', Ialt: '', kKnRIS: '100', kKnRE: '100', kKnRS: '0', kSCIIVA: true,
	kKnSca: '100', kKnRP: 0,
	COLS: cols,
	TA: 5, kKnM: 2,
	TC: argb((Legend[0] << 16) | (Legend[1] << 8) | Legend[2]),
	kSTP: 0, kKnRAO: '0', kKnSC: '10', TO: 4, kKnFT: 2, TP: 2, kKnBgI: { a: [], f: '', fe: '' }, kSD: 0,
	TS: 24,
	ROWS: rows,
});

function durationButton([name, file]) {
	const normal = durationIcon(file, Legend, `${file}-normal`);
	const lit = durationIcon(file, Amber, `${file}-lit`);
	const litDotted = durationIcon(`${file}_dotted`, Amber, `${file}-dotted-lit`);
	return button({
		hold: name,
		icon: normal,
		events: iconEvents(state('dorico_duration'), DurationValues.map(value =>
			[value, value === name ? lit : value === `${name} dotted` ? litDotted : normal])),
	});
}

const toggleButton = (action, stateId, onIcon, offIcon) => button({
	...action,
	icon: offIcon,
	events: iconEvents(stateId, [['true', onIcon], ['false', offIcon]]),
});

const plainButton = (action, icon) => button({ ...action, icon });

// Durations in a 3 x 3 block on the bottom rows, a gap column, then the 3 x 4 controls with blank corners as in
// the original. Long presses: a duration is selected dotted, Play plays from the selection, Left and Right move a bar.
const Columns = 7, Rows = 4, ControlsColumn = 4;
const DurationLayout = [
	['Breve', 'Whole', 'Half'],
	['Quarter', 'Eighth', '16th'],
	[null, '32nd', '64th'],
];
const grid = Array.from({ length: 15 }, () => Array.from({ length: 15 }, () => ({})));
DurationLayout.forEach((row, r) => row.forEach((name, c) => {
	if (name) grid[Rows - DurationLayout.length + r][c] = durationButton(Durations.find(d => d[0] === name));
}));

const controls = [
	[
		button({ command: 'Edit.Delete', title: 'Del' }),
		toggleButton({ hold: 'Play' }, state('dorico_inPlayback'), glyph('stop'), glyph('play')),
		toggleButton({ command: 'Play.Record' }, state('dorico_isRecording'), glyph('stop'), glyph('record')),
	],
	[null, plainButton({ command: 'EventEdit.NavigateUp' }, glyph('up')), null],
	[
		plainButton({ hold: 'Left' }, glyph('left')),
		toggleButton({ command: 'NoteInput.Enter' }, state('dorico_noteInputActive'), noteInputIcon(true), noteInputIcon(false)),
		plainButton({ hold: 'Right' }, glyph('right')),
	],
	[null, plainButton({ command: 'EventEdit.NavigateDown' }, glyph('down')), null],
];
controls.forEach((row, r) => row.forEach((b, c) => { if (b) grid[r][ControlsColumn + c] = b; }));

// TP keeps a page background as BG_<timestamp>.jpg next to the icons.
const background = { name: 'BG_1800000000999.jpg', bytes: fs.readFileSync(path.join(root, 'src/Assets/Backgrounds/brushed-metal.jpg')) };

const page = {
	kATO: '', BG: argb(Panel), MAX: false, BGI: background.name, kPL: 0, kGB: false, GUid: -1, KEY_ID: '1800000000000', kATY: 0,
	BUTTONS: grid,
	KEY_COLUMNS: Columns, kFM: 0, kBN: false, VERSION: 2, kENA: 0, GUdata: '', KEY_TITLE: 'DoriDeck Duration Pad',
	KEY_ROWS: Rows, BTN_MARGIN: 1, PO: 1,
};

// ---- .tpz2 (a zip) ----

function zip(entries) {
	const local = [], central = [];
	let offset = 0;
	for (const [name, data] of entries) {
		const nameBytes = Buffer.from(name, 'utf8'), packed = zlib.deflateRawSync(data, { level: 9 }), crc = crc32(data);
		const header = Buffer.alloc(30);
		header.writeUInt32LE(0x04034b50, 0);
		header.writeUInt16LE(20, 4);
		header.writeUInt16LE(8, 8);
		header.writeUInt16LE(0x21, 12); // 1980-01-01, so the output only changes with the content
		header.writeUInt32LE(crc, 14);
		header.writeUInt32LE(packed.length, 18);
		header.writeUInt32LE(data.length, 22);
		header.writeUInt16LE(nameBytes.length, 26);
		local.push(header, nameBytes, packed);

		const entry = Buffer.alloc(46);
		entry.writeUInt32LE(0x02014b50, 0);
		entry.writeUInt16LE(20, 4);
		entry.writeUInt16LE(20, 6);
		entry.writeUInt16LE(8, 10);
		entry.writeUInt16LE(0x21, 14);
		entry.writeUInt32LE(crc, 16);
		entry.writeUInt32LE(packed.length, 20);
		entry.writeUInt32LE(data.length, 24);
		entry.writeUInt16LE(nameBytes.length, 28);
		entry.writeUInt32LE(offset, 42);
		central.push(entry, nameBytes);
		offset += 30 + nameBytes.length + packed.length;
	}
	const centralBytes = Buffer.concat(central), end = Buffer.alloc(22);
	end.writeUInt32LE(0x06054b50, 0);
	end.writeUInt16LE(entries.length, 8);
	end.writeUInt16LE(entries.length, 10);
	end.writeUInt32LE(centralBytes.length, 12);
	end.writeUInt32LE(offset, 16);
	return Buffer.concat([...local, centralBytes, end]);
}

const data = { pages: [JSON.stringify(page)], flows: [], values: [] };
fs.mkdirSync(path.dirname(output), { recursive: true });
fs.writeFileSync(output, zip([
	['data.json', Buffer.from(JSON.stringify(data), 'utf8')],
	...[...images.values(), background].map(({ name, bytes }) => [`img/${name}`, bytes]),
	['version.json', Buffer.from(JSON.stringify({ version: 2 }), 'utf8')],
]));
console.log(`Wrote ${path.relative(root, output)} (${images.size} icons)`);
