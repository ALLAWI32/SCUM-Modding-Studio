// node:test suite for the mod share Worker, run with `npm test` (Node 22 or later, no packages). Every request goes
// through worker.fetch with the in-memory bindings of fake-env.mjs.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import worker from '../worker.js';
import { createEnv, START } from './fake-env.mjs';
import { makeZip, jpeg, concat } from './zip.mjs';

const BASE = 'https://modshare.test';
const FIXTURES = new URL('../../../tests/ScumStudio.Tests/', import.meta.url);
const MiB = 1024 * 1024;

const call = (env, request) => worker.fetch(request, env, { waitUntil() {}, passThroughOnException() {} });

function manifest(overrides = {}) {
  return {
    format: 'scumstudio.share/1',
    name: 'Quiet coast',
    author: 'ALLAWI',
    description: 'The bunker gate moved and two huts by the shore.',
    sectors: ['B_2', 'C_3'],
    appVersion: '3.1.0',
    gameBuild: '1.0.1.2.98765',
    created: '2026-10-12T11:59:00Z',
    edits: 3,
    ...overrides,
  };
}

const PREVIEW = jpeg(4096);
const THUMB = jpeg(1024);

function entries({ manifest: m = manifest(), replace = {}, add = [], omit = [] } = {}) {
  const list = [
    { name: 'manifest.json', data: typeof m === 'string' ? m : JSON.stringify(m) },
    { name: 'edits.json', data: JSON.stringify({ ops: [{ type: 'Move', level: 'B_2' }, { type: 'Add' }, { type: 'Remove' }] }) },
    { name: 'files/craftables.json', data: '[]' },
    { name: 'preview.jpg', data: PREVIEW, method: 0 },
    { name: 'thumb.jpg', data: THUMB, method: 0 },
  ]
    .filter((e) => !omit.includes(e.name))
    .map((e) => (replace[e.name] ? { ...e, ...replace[e.name] } : e));
  return [...list, ...add];
}

const packageZip = (options) => makeZip(entries(options));

function uploadRequest(bytes, { ip = '198.51.100.7', length = bytes.length } = {}) {
  const request = new Request(`${BASE}/upload`, {
    method: 'POST',
    body: bytes,
    headers: { 'Content-Type': 'application/zip', 'Content-Length': String(length), 'CF-Connecting-IP': ip },
  });
  assert.equal(request.headers.get('content-length'), String(length), 'the runtime keeps the Content-Length header');
  return request;
}

const upload = (env, bytes, options) => call(env, uploadRequest(bytes, options));
const get = (env, path) => call(env, new Request(BASE + path));

async function expectRefused(env, bytes, status, error) {
  const response = await upload(env, bytes);
  assert.equal(response.status, status);
  assert.equal((await response.json()).error, error);
  assert.deepEqual(env.MODS.keys().filter((k) => k !== 'index.json'), [], 'nothing stored');
}

// ---- uploads that pass ----

test('a valid upload answers 201 with an id, and the index, package and pictures can be read back', async () => {
  const { env, clock } = createEnv();
  const zip = packageZip();
  const response = await upload(env, zip);
  assert.equal(response.status, 201);
  const { id } = await response.json();
  assert.match(id, /^[a-z0-9]{12}$/);

  const index = await get(env, '/index.json');
  assert.equal(index.status, 200);
  assert.equal(index.headers.get('Cache-Control'), 'public, max-age=60');
  assert.equal(index.headers.get('Access-Control-Allow-Origin'), null);
  const { mods } = await index.json();
  assert.deepEqual(mods, [{
    id,
    name: 'Quiet coast',
    author: 'ALLAWI',
    description: 'The bunker gate moved and two huts by the shore.',
    sectors: ['B_2', 'C_3'],
    appVersion: '3.1.0',
    gameBuild: '1.0.1.2.98765',
    edits: 3,
    size: zip.length,
    created: new Date(clock.now).toISOString(),
  }]);

  for (const [path, type, bytes] of [[`/mods/${id}.zip`, 'application/zip', zip], [`/previews/${id}.jpg`, 'image/jpeg', PREVIEW], [`/thumbs/${id}.jpg`, 'image/jpeg', THUMB]]) {
    const file = await get(env, path);
    assert.equal(file.status, 200, path);
    assert.equal(file.headers.get('Content-Type'), type);
    assert.equal(file.headers.get('X-Content-Type-Options'), 'nosniff');
    assert.match(file.headers.get('Cache-Control'), /immutable/);
    assert.equal(file.headers.get('Access-Control-Allow-Origin'), null);
    assert.deepEqual(new Uint8Array(await file.arrayBuffer()), bytes);
  }
});

test('the C# package fixture (wip/share-package) is accepted', async (t) => {
  const fixture = new URL('Fixtures/Share/sample-share.zip', FIXTURES);
  if (!existsSync(fixture)) {
    t.skip('tests/ScumStudio.Tests/Fixtures/Share/sample-share.zip is not on this branch yet');
    return;
  }

  const { env } = createEnv();
  const response = await upload(env, new Uint8Array(readFileSync(fixture)));
  assert.equal(response.status, 201, JSON.stringify(await response.clone().json()));
});

test('a deflated entry with a data descriptor is read as the central directory says', async () => {
  const { env } = createEnv();
  const zip = packageZip({ replace: { 'edits.json': { descriptor: true } } });
  assert.equal((await upload(env, zip)).status, 201);
});

test('the manifest strings lose control and bidi-override characters; the description keeps its line breaks', async () => {
  const { env } = createEnv();
  const zip = packageZip({ manifest: manifest({ name: '  Quiet\u0007 coast‮ ', author: 'AL\nLAWI', description: 'Line one\r\nLine two\u0000' }) });
  assert.equal((await upload(env, zip)).status, 201);
  const [row] = env.MODS.index().mods;
  assert.equal(row.name, 'Quiet coast');
  assert.equal(row.author, 'ALLAWI');
  assert.equal(row.description, 'Line one\nLine two');
});

test('a description of 600 characters and no sectors (the whole map) are accepted', async () => {
  const { env } = createEnv();
  assert.equal((await upload(env, packageZip({ manifest: manifest({ description: 'd'.repeat(600), sectors: [] }) }))).status, 201);
});

// ---- size, rate ----

test('a Content-Length over 3 MiB answers 413 before anything is read', async () => {
  const { env } = createEnv();
  const response = await upload(env, packageZip(), { length: 3 * MiB + 1 });
  assert.equal(response.status, 413);
  assert.equal(env.KV.writes, 0, 'not even counted');
});

test('a body longer than its Content-Length says stops at 3 MiB + 1 and answers 413', async () => {
  const { env } = createEnv();
  const response = await upload(env, new Uint8Array(3 * MiB + 1), { length: 100 });
  assert.equal(response.status, 413);
});

test('no Content-Length answers 411', async () => {
  const { env } = createEnv();
  const request = new Request(`${BASE}/upload`, { method: 'POST', body: new Blob([packageZip()]).stream(), duplex: 'half' });
  assert.equal(request.headers.get('content-length'), null);
  assert.equal((await call(env, request)).status, 411);
});

test('the 6th upload from one address in an hour answers 429 with Retry-After; another address and the next hour pass', async () => {
  const { env, clock } = createEnv();
  for (let i = 0; i < 5; i++) {
    assert.equal((await upload(env, packageZip())).status, 201, `upload ${i + 1}`);
  }

  const refused = await upload(env, packageZip());
  assert.equal(refused.status, 429);
  const retry = Number(refused.headers.get('Retry-After'));
  assert.ok(retry > 0 && retry <= 3600, `Retry-After ${retry}`);
  assert.equal((await upload(env, packageZip(), { ip: '203.0.113.9' })).status, 201);
  clock.now += retry * 1000;
  assert.equal((await upload(env, packageZip())).status, 201);
  assert.ok([...env.KV.values.keys()].every((k) => !k.includes('198.51.100.7')), 'no raw IP in KV');
});

test('the day limit counts every address and answers 429 until the next UTC day', async () => {
  const { env } = createEnv({ vars: { MAX_UPLOADS_PER_DAY: '2' } });
  assert.equal((await upload(env, packageZip(), { ip: '192.0.2.1' })).status, 201);
  assert.equal((await upload(env, packageZip(), { ip: '192.0.2.2' })).status, 201);
  const refused = await upload(env, packageZip(), { ip: '192.0.2.3' });
  assert.equal(refused.status, 429);
  assert.equal(Number(refused.headers.get('Retry-After')), 12 * 3600); // START is 12:00 UTC
});

// ---- packages that are refused ----

test('not a zip answers 400', async () => {
  const { env } = createEnv();
  await expectRefused(env, new TextEncoder().encode('hello, this is not a zip file at all'), 400, 'not_zip');
});

test('names outside the list answer 400: ../x, a backslash, a .uasset, a duplicate, a missing picture', async () => {
  const cases = {
    '../manifest.json': packageZip({ replace: { 'manifest.json': { name: '../manifest.json' } } }),
    'files\\craftables.json': packageZip({ replace: { 'files/craftables.json': { name: 'files\\craftables.json' } } }),
    'files/Mod.uasset': packageZip({ add: [{ name: 'files/Mod.uasset', data: '{}' }] }),
    'duplicate manifest': packageZip({ add: [{ name: 'manifest.json', data: JSON.stringify(manifest()) }] }),
    'no thumb': packageZip({ omit: ['thumb.jpg'] }),
  };
  for (const [label, zip] of Object.entries(cases)) {
    const { env } = createEnv();
    const response = await upload(env, zip);
    assert.equal(response.status, 400, label);
    assert.equal((await response.json()).error, 'bad_entry', label);
  }
});

test('UE package or pak magic bytes in any entry answer 400', async () => {
  const pakFooter = new Uint8Array([0x7b, 0x7d, 0x20, 0xe1, 0x12, 0x6f, 0x5a, 0x0b, 0x00, 0x00, 0x00]);
  const cases = {
    'edits.json is a package': packageZip({ replace: { 'edits.json': { data: concat([new Uint8Array([0xc1, 0x83, 0x2a, 0x9e]), new TextEncoder().encode('{}')]) } } }),
    'craftables.json holds a pak footer': packageZip({ replace: { 'files/craftables.json': { data: pakFooter } } }),
    'the preview holds a pak footer': packageZip({ replace: { 'preview.jpg': { data: concat([PREVIEW.subarray(0, 100), pakFooter, PREVIEW.subarray(100)]) } } }),
  };
  for (const [label, zip] of Object.entries(cases)) {
    const { env } = createEnv();
    const response = await upload(env, zip);
    assert.equal(response.status, 400, label);
    assert.equal((await response.json()).error, 'game_data', label);
  }
});

test('a picture that is deflated or not a JPEG, or a JSON entry that is binary, answers 400', async () => {
  const cases = [
    [packageZip({ replace: { 'preview.jpg': { method: 8 } } }), 'bad_picture'],
    [packageZip({ replace: { 'thumb.jpg': { data: new TextEncoder().encode('GIF89a......') } } }), 'bad_picture'],
    [packageZip({ replace: { 'edits.json': { data: new Uint8Array([0x7b, 0xff, 0xfe, 0x7d]) } } }), 'bad_entry'],
    [packageZip({ replace: { 'edits.json': { data: 'plain text' } } }), 'bad_entry'],
  ];
  for (const [zip, error] of cases) {
    const { env } = createEnv();
    await expectRefused(env, zip, 400, error);
  }
});

test('zip bombs and forged headers answer 400', async () => {
  const cases = {
    'declared size over the 16 MiB cap': [packageZip({ replace: { 'edits.json': { size: 16 * MiB + 1 } } }), 'entry_too_large'],
    'declared total over 24 MiB': [packageZip({ replace: { 'edits.json': { size: 16 * MiB }, 'files/craftables.json': { size: 9 * MiB } } }), 'entry_too_large'],
    'inflates past its declared size': [packageZip({ replace: { 'edits.json': { data: `[${' '.repeat(100_000)}]`, size: 100 } } }), 'bad_zip'],
    'local size differs from the central directory': [packageZip({ replace: { 'edits.json': { localSize: 7 } } }), 'bad_zip'],
    'local name differs from the central directory': [packageZip({ replace: { 'edits.json': { localName: 'edits.jsoX' } } }), 'bad_zip'],
    'hidden bytes between entries': [packageZip({ replace: { 'edits.json': { after: 'PAYLOAD' } } }), 'bad_zip'],
    'an archive comment': [makeZip(entries(), { comment: 'hello' }), 'not_zip'],
    'an extra field over 64 bytes': [packageZip({ replace: { 'edits.json': { extra: new Uint8Array(65) } } }), 'bad_zip'],
    'compression method 12': [packageZip({ replace: { 'edits.json': { method: 12 } } }), 'bad_zip'],
    'the encrypted flag': [packageZip({ replace: { 'edits.json': { flags: 1 } } }), 'bad_zip'],
  };
  for (const [label, [zip, error]] of Object.entries(cases)) {
    const { env } = createEnv();
    const response = await upload(env, zip);
    assert.equal(response.status, 400, label);
    assert.equal((await response.json()).error, error, label);
  }
});

test('an invalid manifest answers 422: description of 601 characters, bad sector, wrong format, empty name, not JSON', async () => {
  const cases = {
    'description 601': manifest({ description: 'd'.repeat(601) }),
    'sector E_1': manifest({ sectors: ['E_1'] }),
    'repeated sector': manifest({ sectors: ['A_0', 'A_0'] }),
    'wrong format': manifest({ format: 'scumstudio.share/2' }),
    'empty name': manifest({ name: '\u0007 ' }),
    'name 61': manifest({ name: 'n'.repeat(61) }),
    'author 33': manifest({ author: 'a'.repeat(33) }),
    'no app version': manifest({ appVersion: 'latest' }),
    'game build 41': manifest({ gameBuild: 'g'.repeat(41) }),
    'negative edits': manifest({ edits: -1 }),
    'not JSON': '{"format":',
  };
  for (const [label, m] of Object.entries(cases)) {
    const { env } = createEnv();
    const response = await upload(env, packageZip({ manifest: m }));
    assert.equal(response.status, 422, label);
    assert.equal((await response.json()).error, 'bad_manifest', label);
  }
});

// ---- the index ----

test('the index lists the newest first', async () => {
  const { env, clock } = createEnv();
  const ids = [];
  for (const name of ['First', 'Second', 'Third']) {
    ids.push((await (await upload(env, packageZip({ manifest: manifest({ name }) }))).json()).id);
    clock.now += 60_000;
  }

  const { mods } = await (await get(env, '/index.json')).json();
  assert.deepEqual(mods.map((m) => m.id), ids.reverse());
  assert.deepEqual(mods.map((m) => m.name), ['Third', 'Second', 'First']);
});

test('a write to the index between the read and the conditional put makes the Worker read again and keep both rows', async () => {
  const { env } = createEnv();
  const first = (await (await upload(env, packageZip())).json()).id;
  let raced = false;
  env.MODS.beforePut = async (key, options) => {
    if (key === 'index.json' && options.onlyIf && !raced) {
      raced = true;
      const other = { id: 'zzzzzzzzzzzz', name: 'Other', created: new Date(START).toISOString() };
      await env.MODS.put('index.json', JSON.stringify({ mods: [other, ...env.MODS.index().mods] }));
    }
  };
  const second = (await (await upload(env, packageZip(), { ip: '203.0.113.9' })).json()).id;
  assert.ok(raced);
  assert.deepEqual(env.MODS.index().mods.map((m) => m.id), [second, 'zzzzzzzzzzzz', first]);
});

test('an index that keeps changing answers 503 after five tries and leaves no stored objects behind', async () => {
  const { env } = createEnv();
  await upload(env, packageZip());
  const before = env.MODS.keys();
  env.MODS.beforePut = async (key, options) => {
    if (key === 'index.json' && options.onlyIf) {
      await env.MODS.put('index.json', JSON.stringify(env.MODS.index()));
    }
  };
  const response = await upload(env, packageZip());
  assert.equal(response.status, 503);
  assert.equal(env.MODS.puts.filter((p) => p.key === 'index.json' && p.conditional).length, 5, 'five conditional tries');
  assert.deepEqual(env.MODS.keys(), before);
});

test('a gallery of 1000 mods answers 507 and deletes what it stored', async () => {
  const { env } = createEnv();
  const rows = Array.from({ length: 1000 }, (_, i) => ({ id: `m${String(i).padStart(11, '0')}`, name: `Mod ${i}`, created: new Date(START).toISOString() }));
  await env.MODS.put('index.json', JSON.stringify({ mods: rows }));
  const response = await upload(env, packageZip());
  assert.equal(response.status, 507);
  assert.deepEqual(env.MODS.keys(), ['index.json']);
  assert.equal(env.MODS.index().mods.length, 1000);
});

test('an empty bucket serves an empty gallery', async () => {
  const { env } = createEnv();
  const response = await get(env, '/index.json');
  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), { mods: [] });
});

// ---- routes ----

test('unknown paths answer 404, known paths with another method 405, and nothing answers CORS', async () => {
  const { env } = createEnv();
  assert.equal((await get(env, '/nope')).status, 404);
  assert.equal((await get(env, '/mods/ABCDEFGHIJKL.zip')).status, 404);
  assert.equal((await get(env, '/mods/abcdefghijkl.zip')).status, 404);
  assert.equal((await get(env, '/mods/../index.json')).status, 200, 'the URL parser resolves dot segments first');
  assert.equal((await get(env, '/upload')).status, 405);
  assert.equal((await get(env, '/mods/abcdefghijkl')).status, 405);
  const options = await call(env, new Request(`${BASE}/upload`, { method: 'OPTIONS' }));
  assert.equal(options.status, 405);
  assert.equal(options.headers.get('Access-Control-Allow-Origin'), null);
  assert.equal((await call(env, new Request(`${BASE}/index.json`, { method: 'POST', body: '{}' }))).status, 405);
});

// ---- DELETE /mods/<id> ----

const ecdsa = { name: 'ECDSA', namedCurve: 'P-256' };

async function keyPair() {
  const pair = await crypto.subtle.generateKey(ecdsa, true, ['sign', 'verify']);
  return { ...pair, spki: toBase64(new Uint8Array(await crypto.subtle.exportKey('spki', pair.publicKey))) };
}

async function signedDelete(privateKey, id, time, { nonce = toBase64Url(crypto.getRandomValues(new Uint8Array(16))), path = `/mods/${id}` } = {}) {
  const canonical = new TextEncoder().encode(`DELETE\n/mods/${id}\n${time}\n${nonce}`);
  const signature = new Uint8Array(await crypto.subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, privateKey, canonical));
  return deleteRequest(path, { time: String(time), nonce, signature: toBase64(signature) });
}

function deleteRequest(path, { time, nonce, signature }) {
  return new Request(BASE + path, {
    method: 'DELETE',
    headers: { 'X-ModShare-Time': time, 'X-ModShare-Nonce': nonce, 'X-ModShare-Signature': signature },
  });
}

async function seedMod(env, id) {
  await env.MODS.put(`mods/${id}.zip`, packageZip());
  await env.MODS.put(`previews/${id}.jpg`, PREVIEW);
  await env.MODS.put(`thumbs/${id}.jpg`, THUMB);
  const mods = env.MODS.index()?.mods ?? [];
  await env.MODS.put('index.json', JSON.stringify({ mods: [{ id, name: 'Seeded', created: new Date(START).toISOString() }, ...mods] }));
}

test('an unsigned DELETE answers 401 and removes nothing', async () => {
  const { spki } = await keyPair();
  const { env } = createEnv({ publicKey: spki });
  await seedMod(env, 'abcdefghijkl');
  assert.equal((await call(env, new Request(`${BASE}/mods/abcdefghijkl`, { method: 'DELETE' }))).status, 401);
  assert.equal(env.MODS.keys().length, 4);
});

test('the C# signature vector (wip/share-client) removes the mod; the same request again answers 401', async (t) => {
  const file = new URL('App/ModShareVectors.json', FIXTURES);
  if (!existsSync(file)) {
    t.skip('tests/ScumStudio.Tests/App/ModShareVectors.json is not on this branch yet');
    return;
  }

  const vector = JSON.parse(readFileSync(file, 'utf8'));
  const id = vector.path.slice('/mods/'.length);
  assert.equal(vector.canonical, `DELETE\n/mods/${id}\n${vector.time}\n${vector.nonce}`, 'the vector signs the canonical string the Worker checks');
  const { env } = createEnv({ publicKey: vector.publicKeySpki, now: vector.time * 1000 });
  await seedMod(env, id);
  await seedMod(env, 'keepkeepkeep');
  const request = () => deleteRequest(vector.path, { time: String(vector.time), nonce: vector.nonce, signature: vector.signature });

  assert.equal((await call(env, request())).status, 204);
  assert.deepEqual(env.MODS.keys(), ['index.json', 'mods/keepkeepkeep.zip', 'previews/keepkeepkeep.jpg', 'thumbs/keepkeepkeep.jpg']);
  assert.deepEqual(env.MODS.index().mods.map((m) => m.id), ['keepkeepkeep']);
  await seedMod(env, id);
  assert.equal((await call(env, request())).status, 401, 'replayed');
  assert.equal(env.MODS.keys().length, 7, 'the replay removed nothing');
});

test('a signed DELETE answers 204; replayed, stale, from the future, another key, another id or no key set: 401', async () => {
  const moderator = await keyPair();
  const stranger = await keyPair();
  const { env, clock } = createEnv({ publicKey: moderator.spki });
  const now = () => Math.floor(clock.now / 1000);
  await seedMod(env, 'abcdefghijkl');
  await seedMod(env, 'secondsecond');

  const nonce = 'yofGEHM23JzpGpAxzeVGcg';
  assert.equal((await call(env, await signedDelete(moderator.privateKey, 'secondsecond', now() - 301))).status, 401, 'stale');
  assert.equal((await call(env, await signedDelete(moderator.privateKey, 'secondsecond', now() + 301))).status, 401, 'future');
  assert.equal((await call(env, await signedDelete(stranger.privateKey, 'secondsecond', now()))).status, 401, 'another key');
  assert.equal((await call(env, await signedDelete(moderator.privateKey, 'abcdefghijkl', now(), { path: '/mods/secondsecond' }))).status, 401, 'signed for another id');
  assert.equal(env.MODS.keys().length, 7, 'nothing removed so far');

  assert.equal((await call(env, await signedDelete(moderator.privateKey, 'secondsecond', now() - 299, { nonce }))).status, 204);
  assert.deepEqual(env.MODS.index().mods.map((m) => m.id), ['abcdefghijkl']);
  assert.equal(env.MODS.bytes('mods/secondsecond.zip'), undefined);
  assert.equal((await call(env, await signedDelete(moderator.privateKey, 'abcdefghijkl', now(), { nonce }))).status, 401, 'nonce used');

  const unset = createEnv({ publicKey: undefined });
  await seedMod(unset.env, 'abcdefghijkl');
  assert.equal((await call(unset.env, await signedDelete(moderator.privateKey, 'abcdefghijkl', now()))).status, 401, 'no key set');
});

test('a signed DELETE of an unknown id answers 404', async () => {
  const moderator = await keyPair();
  const { env, clock } = createEnv({ publicKey: `-----BEGIN PUBLIC KEY-----\n${moderator.spki}\n-----END PUBLIC KEY-----\n` });
  await seedMod(env, 'abcdefghijkl');
  const response = await call(env, await signedDelete(moderator.privateKey, 'unknownmod00', Math.floor(clock.now / 1000)));
  assert.equal(response.status, 404);
  assert.equal(env.MODS.keys().length, 4);
});

test('a removed mod no longer serves its package or pictures', async () => {
  const moderator = await keyPair();
  const { env, clock } = createEnv({ publicKey: moderator.spki });
  const { id } = await (await upload(env, packageZip())).json();
  assert.equal((await call(env, await signedDelete(moderator.privateKey, id, Math.floor(clock.now / 1000)))).status, 204);
  for (const path of [`/mods/${id}.zip`, `/previews/${id}.jpg`, `/thumbs/${id}.jpg`]) {
    assert.equal((await get(env, path)).status, 404, path);
  }

  assert.deepEqual((await (await get(env, '/index.json')).json()).mods, []);
});

function toBase64(bytes) {
  return btoa(String.fromCharCode(...bytes));
}

function toBase64Url(bytes) {
  return toBase64(bytes).replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');
}
