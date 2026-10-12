// SCUM Modding Studio "Share your mod" gallery: a Cloudflare Worker over one R2 bucket (MODS) and one KV namespace (KV).
// Nothing here can write or delete on someone's behalf: an upload needs no secret, and a removal must carry an ECDSA P-256
// signature made with the moderator key that lives only on the owner's PC (this Worker holds the public half,
// MODERATOR_PUBLIC_KEY). Protocol, limits and deploy steps: README.md. Bodies, keys and raw IPs are never logged.

const MiB = 1024 * 1024;
const FORMAT = 'scumstudio.share/1';
const INDEX = 'index.json';

// The only names a package may hold, each with its largest uncompressed size.
const ENTRY_CAPS = new Map([
  ['manifest.json', 64 * 1024],
  ['edits.json', 16 * MiB],
  ['files/craftables.json', 16 * MiB],
  ['files/EconomyOverride.json', 16 * MiB],
  ['preview.jpg', 800 * 1024],
  ['thumb.jpg', 120 * 1024],
]);
const REQUIRED = ['manifest.json', 'edits.json', 'preview.jpg', 'thumb.jpg'];
const PICTURES = ['preview.jpg', 'thumb.jpg'];
const MAX_ENTRIES = 8;
const MAX_TOTAL = 24 * MiB;
const MAX_EXTRA = 64; // per header: room for a Zip64 or timestamp field, none for a hidden file
const ALLOWED_FLAGS = 0x0002 | 0x0004 | 0x0008 | 0x0800; // deflate level bits, data descriptor, UTF-8 names

const PACKAGE_MAGIC = [0xc1, 0x83, 0x2a, 0x9e]; // UE package 0x9E2A83C1
const PAK_MAGIC = [0xe1, 0x12, 0x6f, 0x5a]; // pak footer 0x5A6F12E1

const ID = /^[a-z0-9]{12}$/;
const SECTOR = /^[A-DZ]_[0-4]$/;
const NONCE = /^[A-Za-z0-9_-]{16,64}$/;
const SIGNATURE_WINDOW_SECONDS = 300;
const NONCE_TTL_SECONDS = 600;
const INDEX_ATTEMPTS = 5;

class Refused extends Error {
  constructor(status, code, headers) {
    super(code);
    this.status = status;
    this.code = code;
    this.headers = headers;
  }
}

export default {
  async fetch(request, env) {
    try {
      return await route(request, env);
    } catch (error) {
      if (error instanceof Refused) {
        return fail(error.status, error.code, error.headers);
      }

      console.error(`modshare: ${error?.name ?? 'error'} ${error?.message ?? ''}`.slice(0, 200));
      return fail(500, 'server');
    }
  },
};

async function route(request, env) {
  const path = new URL(request.url).pathname;
  const method = request.method;
  if (path === '/upload') {
    return method === 'POST' ? upload(request, env) : notAllowed('POST');
  }

  if (path === '/index.json') {
    return method === 'GET' ? getIndex(env) : notAllowed('GET');
  }

  const file = /^\/((?:mods\/[a-z0-9]{12}\.zip)|(?:previews|thumbs)\/[a-z0-9]{12}\.jpg)$/.exec(path);
  if (file) {
    return method === 'GET' ? getFile(env, file[1]) : notAllowed('GET');
  }

  const mod = /^\/mods\/([a-z0-9]{12})$/.exec(path);
  if (mod) {
    return method === 'DELETE' ? remove(request, env, mod[1]) : notAllowed('DELETE');
  }

  return fail(404, 'not_found');
}

// ---- POST /upload ----

async function upload(request, env) {
  const limits = limitsOf(env);
  const declared = request.headers.get('Content-Length');
  if (declared === null) {
    return fail(411, 'length_required');
  }

  if (!/^\d{1,12}$/.test(declared)) {
    return fail(400, 'bad_request');
  }

  if (Number(declared) > limits.maxZip) {
    return fail(413, 'too_large');
  }

  await rateLimit(request, env, limits);
  const zip = await readCapped(request.body, limits.maxZip);
  if (zip === null) {
    return fail(413, 'too_large');
  }

  const entries = readDirectory(zip);
  const manifestBytes = await extract(zip, entries.get('manifest.json'));
  const manifest = readManifest(manifestBytes);
  if (manifest === null) {
    return fail(422, 'bad_manifest');
  }

  const data = new Map();
  for (const entry of entries.values()) {
    const bytes = entry.name === 'manifest.json' ? manifestBytes : await extract(zip, entry);
    if (startsWith(bytes, PACKAGE_MAGIC) || contains(bytes, PAK_MAGIC)) {
      throw new Refused(400, 'game_data');
    }

    if (PICTURES.includes(entry.name)) {
      if (!startsWith(bytes, [0xff, 0xd8, 0xff])) {
        throw new Refused(400, 'bad_picture');
      }

      data.set(entry.name, bytes.slice());
    } else if (!isJsonText(bytes)) {
      throw new Refused(400, 'bad_entry');
    }
  }

  const now = clock(env);
  const id = newId();
  const keys = objectKeys(id);
  await Promise.all([
    env.MODS.put(keys[0], zip, { httpMetadata: { contentType: 'application/zip' } }),
    env.MODS.put(keys[1], data.get('preview.jpg'), { httpMetadata: { contentType: 'image/jpeg' } }),
    env.MODS.put(keys[2], data.get('thumb.jpg'), { httpMetadata: { contentType: 'image/jpeg' } }),
  ]);

  const row = { id, ...manifest, size: zip.length, created: new Date(now).toISOString() };
  const result = await updateIndex(env, (mods) => (mods.length >= limits.maxMods ? 'full' : [row, ...mods]));
  if (result !== 'ok') {
    await env.MODS.delete(keys);
    return result === 'full' ? fail(507, 'full') : fail(503, 'busy', { 'Retry-After': '30' });
  }

  return json(201, { id });
}

function limitsOf(env) {
  const number = (value, fallback) => {
    const n = Number.parseInt(value ?? '', 10);
    return Number.isSafeInteger(n) && n > 0 ? n : fallback;
  };
  return {
    maxZip: number(env.MAX_ZIP_BYTES, 3 * MiB),
    perHour: number(env.MAX_UPLOADS_PER_HOUR, 5),
    perDay: number(env.MAX_UPLOADS_PER_DAY, 300),
    maxMods: number(env.MAX_INDEX_ENTRIES, 1000),
  };
}

// Counts every upload that gets past the size check, valid or not, so junk costs the sender too. The day counter also
// bounds KV writes (two per counted upload) under the free tier's 1,000 a day. KV is eventually consistent, so a burst
// spread over several Cloudflare locations can go a little over; R2 and the index cap stay the hard limits.
async function rateLimit(request, env, limits) {
  const now = clock(env);
  const hour = Math.floor(now / 3_600_000);
  const day = Math.floor(now / 86_400_000);
  const ip = request.headers.get('CF-Connecting-IP') ?? 'unknown';
  const dayKey = `rl:day:${new Date(day * 86_400_000).toISOString().slice(0, 10)}`;
  const ipKey = `rl:${(await sha256Hex(`${hour}|${ip}`)).slice(0, 32)}:${hour}`;
  const [dayCount, ipCount] = (await Promise.all([env.KV.get(dayKey), env.KV.get(ipKey)]))
    .map((value) => Number.parseInt(value ?? '0', 10) || 0);
  if (dayCount >= limits.perDay) {
    throw new Refused(429, 'rate_limited', { 'Retry-After': String(Math.ceil(((day + 1) * 86_400_000 - now) / 1000)) });
  }

  if (ipCount >= limits.perHour) {
    throw new Refused(429, 'rate_limited', { 'Retry-After': String(Math.ceil(((hour + 1) * 3_600_000 - now) / 1000)) });
  }

  await Promise.all([
    env.KV.put(dayKey, String(dayCount + 1), { expirationTtl: 2 * 86_400 }),
    env.KV.put(ipKey, String(ipCount + 1), { expirationTtl: 3_600 }),
  ]);
}

async function readCapped(body, cap) {
  if (!body) {
    return new Uint8Array(0);
  }

  const reader = body.getReader();
  const chunks = [];
  let length = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) {
      break;
    }

    length += value.byteLength;
    if (length > cap) {
      reader.cancel().catch(() => {});
      return null;
    }

    chunks.push(value);
  }

  const bytes = new Uint8Array(length);
  let offset = 0;
  for (const chunk of chunks) {
    bytes.set(chunk, offset);
    offset += chunk.byteLength;
  }

  return bytes;
}

// ---- the zip, byte by byte ----

// Reads the central directory and checks every local header against it. Every byte of the upload must belong to a header,
// a name, a small extra field, entry data or a data descriptor, so nothing can ride along unseen in the stored zip.
function readDirectory(zip) {
  const view = new DataView(zip.buffer, zip.byteOffset, zip.byteLength);
  const u16 = (offset) => view.getUint16(offset, true);
  const u32 = (offset) => view.getUint32(offset, true);
  const bad = (code = 'bad_zip') => new Refused(400, code);

  const eocd = zip.length - 22;
  if (eocd < 0 || u32(eocd) !== 0x06054b50 || u16(eocd + 20) !== 0) {
    throw bad('not_zip'); // the end record must close the file: no archive comment
  }

  const count = u16(eocd + 10);
  const directorySize = u32(eocd + 12);
  const directoryOffset = u32(eocd + 16);
  if (u16(eocd + 4) !== 0 || u16(eocd + 6) !== 0 || u16(eocd + 8) !== count || count === 0 || count > MAX_ENTRIES
      || directoryOffset + directorySize !== eocd) {
    throw bad();
  }

  const entries = new Map();
  let total = 0;
  let p = directoryOffset;
  for (let i = 0; i < count; i++) {
    if (p + 46 > eocd || u32(p) !== 0x02014b50) {
      throw bad();
    }

    const nameLength = u16(p + 28);
    const extra = u16(p + 30);
    const next = p + 46 + nameLength + extra;
    if (next > eocd || extra > MAX_EXTRA || u16(p + 32) !== 0 || u16(p + 34) !== 0) {
      throw bad(); // also refuses an entry comment and a multi-disk archive
    }

    const entry = {
      flags: u16(p + 8),
      method: u16(p + 10),
      crc: u32(p + 16),
      compressed: u32(p + 20),
      size: u32(p + 24),
      nameBytes: zip.subarray(p + 46, p + 46 + nameLength),
      name: nameLength <= 64 ? latin1(zip.subarray(p + 46, p + 46 + nameLength)) : '',
      local: u32(p + 42),
    };
    const cap = ENTRY_CAPS.get(entry.name);
    if (cap === undefined || entries.has(entry.name)) {
      throw bad('bad_entry');
    }

    if ((entry.flags & ~ALLOWED_FLAGS) !== 0 || (entry.method !== 0 && entry.method !== 8)
        || (entry.method === 0 && entry.compressed !== entry.size)) {
      throw bad();
    }

    if (PICTURES.includes(entry.name) && entry.method !== 0) {
      throw bad('bad_picture');
    }

    total += entry.size;
    if (entry.size > cap || total > MAX_TOTAL) {
      throw bad('entry_too_large');
    }

    entries.set(entry.name, entry);
    p = next;
  }

  if (p !== eocd || REQUIRED.some((name) => !entries.has(name))) {
    throw bad(p !== eocd ? 'bad_zip' : 'bad_entry');
  }

  let expected = 0;
  for (const entry of [...entries.values()].sort((a, b) => a.local - b.local)) {
    const h = entry.local;
    if (h !== expected || h + 30 > directoryOffset || u32(h) !== 0x04034b50) {
      throw bad();
    }

    const flags = u16(h + 6);
    const nameLength = u16(h + 26);
    const extra = u16(h + 28);
    const descriptor = (flags & 0x0008) !== 0;
    const local = [u32(h + 14), u32(h + 18), u32(h + 22)];
    const agrees = local[0] === entry.crc && local[1] === entry.compressed && local[2] === entry.size;
    const deferred = descriptor && local.every((value) => value === 0); // sizes follow the data
    if (flags !== entry.flags || u16(h + 8) !== entry.method || extra > MAX_EXTRA || !(agrees || deferred)
        || nameLength !== entry.nameBytes.length || !equalBytes(zip.subarray(h + 30, h + 30 + nameLength), entry.nameBytes)) {
      throw bad();
    }

    entry.start = h + 30 + nameLength + extra;
    expected = entry.start + entry.compressed;
    if (descriptor) {
      // A data descriptor: CRC and 32-bit sizes, with or without its signature, matching the central directory.
      const signed = expected + 16 <= directoryOffset && u32(expected) === 0x08074b50;
      const d = signed ? expected + 4 : expected;
      if (d + 12 > directoryOffset || u32(d) !== entry.crc || u32(d + 4) !== entry.compressed || u32(d + 8) !== entry.size) {
        throw bad();
      }

      expected = d + 12;
    }

    if (expected > directoryOffset) {
      throw bad();
    }
  }

  if (expected !== directoryOffset) {
    throw bad();
  }

  return entries;
}

// The entry's bytes; a deflated entry is inflated up to its declared size and must come out at exactly that size.
async function extract(zip, entry) {
  const raw = zip.subarray(entry.start, entry.start + entry.compressed);
  if (entry.method === 0) {
    return raw;
  }

  const reader = new Blob([raw]).stream().pipeThrough(new DecompressionStream('deflate-raw')).getReader();
  const out = new Uint8Array(entry.size);
  let length = 0;
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) {
        break;
      }

      if (length + value.byteLength > entry.size) {
        reader.cancel().catch(() => {});
        throw new Refused(400, 'bad_zip');
      }

      out.set(value, length);
      length += value.byteLength;
    }
  } catch (error) {
    throw error instanceof Refused ? error : new Refused(400, 'bad_zip');
  }

  if (length !== entry.size) {
    throw new Refused(400, 'bad_zip');
  }

  return out;
}

// ---- the manifest ----

// The gallery row's fields, cleaned (control and bidi-override characters dropped, trimmed), or null when the manifest
// is not a valid one.
function readManifest(bytes) {
  let m;
  try {
    m = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
  } catch {
    return null;
  }

  if (m === null || typeof m !== 'object' || Array.isArray(m) || m.format !== FORMAT) {
    return null;
  }

  const name = clean(m.name, false);
  const author = clean(m.author, false);
  const description = clean(m.description ?? '', true);
  const appVersion = clean(m.appVersion, false);
  const gameBuild = clean(m.gameBuild ?? '', false);
  const edits = m.edits ?? 0;
  if (name === null || name.length < 1 || name.length > 60 || author === null || author.length < 1 || author.length > 32
      || description === null || description.length > 600 || appVersion === null || appVersion.length > 64
      || !/^\d+\.\d+\.\d+/.test(appVersion) || gameBuild === null || gameBuild.length > 40
      || !Number.isSafeInteger(edits) || edits < 0 || !Array.isArray(m.sectors) || m.sectors.length > 25) {
    return null;
  }

  const sectors = [];
  for (const sector of m.sectors) {
    if (typeof sector !== 'string' || !SECTOR.test(sector) || sectors.includes(sector)) {
      return null;
    }

    sectors.push(sector);
  }

  return { name, author, description, sectors, appVersion, gameBuild, edits };
}

function clean(value, keepLines) {
  if (typeof value !== 'string') {
    return null;
  }

  const controls = keepLines
    ? /[\u0000-\u0009\u000b-\u001f\u007f-\u009f‪-‮⁦-⁩]/g
    : /[\u0000-\u001f\u007f-\u009f‪-‮⁦-⁩]/g;
  return value.replace(/\r\n?/g, '\n').replace(controls, '').trim();
}

// A JSON entry must be UTF-8 text that opens an object or an array: no binary rides in under a JSON name.
function isJsonText(bytes) {
  let text;
  try {
    text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  } catch {
    return false;
  }

  return /^\s*[[{]/.test(text);
}

// ---- GET ----

async function getIndex(env) {
  const object = await env.MODS.get(INDEX);
  const headers = {
    'Content-Type': 'application/json; charset=utf-8',
    'Cache-Control': 'public, max-age=60',
    'X-Content-Type-Options': 'nosniff',
  };
  if (!object) {
    return new Response('{"mods":[]}', { headers });
  }

  return new Response(object.body, { headers: { ...headers, ETag: object.httpEtag } });
}

async function getFile(env, key) {
  const object = await env.MODS.get(key);
  if (!object) {
    return fail(404, 'not_found');
  }

  return new Response(object.body, {
    headers: {
      'Content-Type': key.endsWith('.zip') ? 'application/zip' : 'image/jpeg',
      'Cache-Control': 'public, max-age=31536000, immutable',
      'X-Content-Type-Options': 'nosniff',
      ETag: object.httpEtag,
    },
  });
}

// ---- DELETE /mods/<id> ----

async function remove(request, env, id) {
  const time = request.headers.get('X-ModShare-Time') ?? '';
  const nonce = request.headers.get('X-ModShare-Nonce') ?? '';
  const signature = fromBase64(request.headers.get('X-ModShare-Signature') ?? '');
  if (!/^\d{1,12}$/.test(time) || Math.abs(clock(env) / 1000 - Number(time)) > SIGNATURE_WINDOW_SECONDS
      || !NONCE.test(nonce) || signature?.length !== 64) {
    return fail(401, 'unauthorized');
  }

  const nonceKey = `nonce:${nonce}`;
  if (await env.KV.get(nonceKey) !== null) {
    return fail(401, 'unauthorized');
  }

  const key = await moderatorKey(env);
  const canonical = new TextEncoder().encode(`DELETE\n/mods/${id}\n${time}\n${nonce}`);
  if (!key || !(await crypto.subtle.verify({ name: 'ECDSA', hash: 'SHA-256' }, key, signature, canonical))) {
    return fail(401, 'unauthorized');
  }

  await env.KV.put(nonceKey, '1', { expirationTtl: NONCE_TTL_SECONDS });
  const result = await updateIndex(env, (mods) => (mods.some((m) => m?.id === id) ? mods.filter((m) => m?.id !== id) : 'absent'));
  if (result === 'busy') {
    return fail(503, 'busy', { 'Retry-After': '30' });
  }

  const keys = objectKeys(id);
  if (result === 'absent' && !(await env.MODS.head(keys[0]))) {
    return fail(404, 'not_found');
  }

  await env.MODS.delete(keys);
  return new Response(null, { status: 204 });
}

// The public key from MODERATOR_PUBLIC_KEY (SubjectPublicKeyInfo, Base64, PEM armour allowed), or null.
async function moderatorKey(env) {
  const text = String(env.MODERATOR_PUBLIC_KEY ?? '').replace(/-----[A-Z ]+-----/g, '').replace(/\s+/g, '');
  const spki = fromBase64(text);
  if (!spki) {
    return null;
  }

  try {
    return await crypto.subtle.importKey('spki', spki, { name: 'ECDSA', namedCurve: 'P-256' }, false, ['verify']);
  } catch {
    return null;
  }
}

// ---- the index ----

// Reads index.json, lets change() return the new row list (or a word that ends the update) and writes it back only if
// nobody wrote in between (R2 etag precondition), reading again up to five times. A missing index is created
// unconditionally: only the very first two uploads racing each other could lose a row.
async function updateIndex(env, change) {
  for (let attempt = 0; attempt < INDEX_ATTEMPTS; attempt++) {
    const object = await env.MODS.get(INDEX);
    const mods = object ? rowsOf(await object.text()) : [];
    const next = change(mods);
    if (!Array.isArray(next)) {
      return next;
    }

    const options = { httpMetadata: { contentType: 'application/json; charset=utf-8' } };
    if (object) {
      options.onlyIf = { etagMatches: object.etag };
    }

    if (await env.MODS.put(INDEX, JSON.stringify({ mods: next }), options)) {
      return 'ok';
    }
  }

  return 'busy';
}

function rowsOf(text) {
  const index = JSON.parse(text);
  if (Array.isArray(index?.mods)) {
    return index.mods;
  }

  throw new Error('index.json has no mods list');
}

// ---- small helpers ----

const objectKeys = (id) => [`mods/${id}.zip`, `previews/${id}.jpg`, `thumbs/${id}.jpg`];

// Tests pass a fixed clock as env.NOW (a function); a deployed Worker's bindings are strings, so it reads the real time.
const clock = (env) => (typeof env.NOW === 'function' ? env.NOW() : Date.now());

function newId() {
  const alphabet = 'abcdefghijklmnopqrstuvwxyz0123456789';
  let id = '';
  while (id.length < 12) {
    for (const byte of crypto.getRandomValues(new Uint8Array(16))) {
      if (byte < 252 && id.length < 12) {
        id += alphabet[byte % 36]; // 252 = 7 * 36: no bias
      }
    }
  }

  return id;
}

async function sha256Hex(text) {
  const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text)));
  return Array.from(digest, (b) => b.toString(16).padStart(2, '0')).join('');
}

function fromBase64(text) {
  if (!/^[A-Za-z0-9+/_-]+={0,2}$/.test(text)) {
    return null;
  }

  const plain = text.replace(/=+$/, '').replace(/-/g, '+').replace(/_/g, '/');
  try {
    const binary = atob(plain.padEnd(Math.ceil(plain.length / 4) * 4, '='));
    return Uint8Array.from(binary, (c) => c.charCodeAt(0));
  } catch {
    return null;
  }
}

const latin1 = (bytes) => String.fromCharCode(...bytes);
const startsWith = (bytes, magic) => magic.every((b, i) => bytes[i] === b);

function contains(bytes, magic) {
  for (let i = bytes.indexOf(magic[0]); i !== -1 && i + magic.length <= bytes.length; i = bytes.indexOf(magic[0], i + 1)) {
    if (magic.every((b, k) => bytes[i + k] === b)) {
      return true;
    }
  }

  return false;
}

function equalBytes(a, b) {
  return a.length === b.length && a.every((value, i) => value === b[i]);
}

function json(status, body, headers = {}) {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      'Content-Type': 'application/json; charset=utf-8',
      'Cache-Control': 'no-store',
      'X-Content-Type-Options': 'nosniff',
      ...headers,
    },
  });
}

const fail = (status, error, headers) => json(status, { error }, headers);
const notAllowed = (allow) => fail(405, 'method_not_allowed', { Allow: allow });
