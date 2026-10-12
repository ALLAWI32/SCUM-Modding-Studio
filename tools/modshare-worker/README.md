# Mod share Worker ("Share your mod")

The hosting behind the app's **Share your mod** gallery: one Cloudflare Worker (`worker.js`, no npm packages), one R2
bucket for the packages and pictures, one KV namespace for the rate limit and the used signature nonces. All of it fits
Cloudflare's free tier.

What it holds, and what it does not:

- **No secret that can write or delete for someone else.** Uploading needs no key at all. Removing a mod needs a
  signature made with the **moderator key**, a file that exists only on the owner's PC. The Worker keeps only the
  public half (`MODERATOR_PUBLIC_KEY`), which can check a signature but cannot make one. The app's exe contains nothing
  that can remove a mod. This is the lesson of the Discord report webhook (audit F029 / N045), which ships a write and
  delete credential inside every exe.
- **Only the project's own edits.** A package may contain exactly these files: `manifest.json`, `edits.json`,
  `files/craftables.json`, `files/EconomyOverride.json`, `preview.jpg`, `thumb.jpg`. Anything else, an Unreal package
  or pak inside any of them, or bytes hidden between them, is refused. See [What the Worker checks](#what-the-worker-checks).

## Deploy (owner, once)

You need a terminal (PowerShell) in `tools\modshare-worker` of the ScumStudio repository.

1. **Cloudflare account.** Make a free account at <https://dash.cloudflare.com/sign-up>. On first use of Workers the
   dashboard asks you to pick a `workers.dev` subdomain (for example `allawi`); pick one.
2. **Node.js.** Install the LTS version (22 or later) from <https://nodejs.org>. Open a new terminal afterwards.
   `node --version` must print `v22` or higher.
3. **Log in.** `npx wrangler@4 login` (a browser window asks you to allow Wrangler; allow it).
4. **The bucket.** `npx wrangler@4 r2 bucket create scum-modshare` (if Wrangler offers to add the binding to the config
   for you, answer no: `wrangler.toml` already has it, named `MODS`).
5. **The KV namespace.** `npx wrangler@4 kv namespace create KV`. It prints an `id = "..."` line. Open
   `wrangler.toml` and replace `<paste the id from wrangler kv namespace create>` with that id (keep the quotes).
   If Wrangler offers to add the binding to the config for you, answer no: the binding must stay named `KV`.
6. **The moderator key.** In the repository root:
   `dotnet run --project src\ScumStudio.Cli -- modshare keygen`
   (or, with an installed build, `scumstudio modshare keygen`). It writes
   `Desktop\ScumStudio-ModShare\moderator.key` and prints the **public** key.
   - Never send, upload or commit `moderator.key`: whoever has it can remove any mod.
   - Back it up (a USB stick). Without it you can still remove mods by hand (below), but not from the app.
   - `scumstudio modshare pubkey` prints the public key again later.
7. **Give the Worker the public key.** Back in `tools\modshare-worker`:
   `npx wrangler@4 secret put MODERATOR_PUBLIC_KEY` and paste the public key from step 6 when asked.
8. **Deploy.** `npx wrangler@4 deploy` (or `npm run deploy`). It prints the address, for example
   `https://scum-modshare.allawi.workers.dev`. Open `<address>/index.json` in a browser: it must show `{"mods":[]}`.
9. **Point the app at it.** For a test, close the app, open `%LOCALAPPDATA%\ScumStudio\settings.json` and set
   `"modShareUrl": "https://scum-modshare.allawi.workers.dev"` (your address, no slash at the end), then start the
   app: Share your mod should list the empty gallery and accept an upload. For the release, ask for
   `ModShareService.DefaultUrl` (in `src\ScumStudio.App\Services\ModShareService.cs`) to be set to that address, so
   every user gets it without editing settings. The environment variable `SCUMSTUDIO_MODSHARE_URL` also works for tests.

The Worker serves the gallery from the root of its address. Do not put it behind a route with a path prefix
(`example.com/modshare/*`): the signed path would no longer match.

## Moderation

- **From the app.** The Moderate view appears only on a PC where `Desktop\ScumStudio-ModShare\moderator.key` exists
  (or where `SCUMSTUDIO_MODSHARE_KEY` names another key file). Remove signs the request with that key.
- **By hand** (no app, or the key is lost). Replace `<id>` with the mod's 12-character id (it is in `index.json`):

  ```
  npx wrangler@4 r2 object delete scum-modshare/mods/<id>.zip --remote
  npx wrangler@4 r2 object delete scum-modshare/previews/<id>.jpg --remote
  npx wrangler@4 r2 object delete scum-modshare/thumbs/<id>.jpg --remote
  npx wrangler@4 r2 object get scum-modshare/index.json --remote --file index.json
  ```

  Open `index.json`, delete the row whose `"id"` is `<id>` (keep the JSON valid), then upload it back:

  ```
  npx wrangler@4 r2 object put scum-modshare/index.json --remote --file index.json --content-type "application/json; charset=utf-8"
  ```

  Do this when nobody is uploading (an upload between your get and your put would be lost from the list).
- **Rotate the key** (lost, copied, or a new PC):
  `dotnet run --project src\ScumStudio.Cli -- modshare keygen --force`, then step 7 again with the new public key.
  The old key stops working the moment the secret is replaced.

## Protocol

Every error answer is JSON `{"error":"<code>"}`; the app turns the status code into a sentence in the user's language.
No answer carries CORS headers (only the app talks to the Worker, never a web page).

| Request | Answers |
|---|---|
| `POST /upload` (body: the package zip) | **201** `{"id":"<12 chars a-z0-9>"}` · 400 `bad_request` (Content-Length not a number) · 411 `length_required` · 413 `too_large` · 429 `rate_limited` + `Retry-After` · 400 `not_zip` / `bad_zip` / `bad_entry` / `entry_too_large` / `bad_picture` / `game_data` · 422 `bad_manifest` · 507 `full` · 503 `busy` + `Retry-After` |
| `GET /index.json` | **200** `{"mods":[row, ...]}`, newest first; `Cache-Control: public, max-age=60`; `{"mods":[]}` before the first upload |
| `GET /mods/<id>.zip` | **200** `application/zip` · 404 |
| `GET /previews/<id>.jpg` | **200** `image/jpeg` (the large picture) · 404 |
| `GET /thumbs/<id>.jpg` | **200** `image/jpeg` (the gallery picture) · 404 |
| `DELETE /mods/<id>` (signed) | **204** · 401 `unauthorized` · 404 `not_found` · 503 `busy` |
| another path / another method | 404 `not_found` / 405 `method_not_allowed` with `Allow` |

Files are served with `Cache-Control: public, max-age=31536000, immutable` (an id never changes content) and
`X-Content-Type-Options: nosniff`.

An index row: `{"id","name","author","description","sectors":["B_2",...],"appVersion","gameBuild","edits","size","created"}`.
`size` is the zip's length in bytes, `created` is the server's time (ISO 8601, UTC), the strings are the manifest's after
cleaning (below).

### The package

A zip (written to a seekable stream, for example a `MemoryStream`, so the sizes sit in the local headers; data descriptors
with 32-bit sizes are accepted too) holding:

| Entry | Needed | Method | Largest (unpacked) |
|---|---|---|---|
| `manifest.json` | yes | stored or deflate | 64 KiB |
| `edits.json` | yes | stored or deflate | 16 MiB |
| `files/craftables.json` | no | stored or deflate | 16 MiB |
| `files/EconomyOverride.json` | no | stored or deflate | 16 MiB |
| `preview.jpg` | yes | **stored** | 800 KiB |
| `thumb.jpg` | yes | **stored** | 120 KiB |

At most 8 entries, 24 MiB unpacked in all, 3 MiB for the zip. `manifest.json`:

```json
{
  "format": "scumstudio.share/1",
  "name": "North airfield",
  "author": "ALLAWI",
  "description": "Hangars and a trader.",
  "sectors": ["B_2", "B_3"],
  "appVersion": "3.1.0",
  "gameBuild": "1.0.2.3.98765",
  "edits": 42,
  "created": "2026-10-12T11:00:00Z"
}
```

`format` must be exactly `scumstudio.share/1`. `name` 1-60 characters, `author` 1-32, `description` at most 600 (line
breaks kept), `sectors` at most 25 distinct cells `A_0` ... `Z_4` (`^[A-DZ]_[0-4]$`; an empty list is the whole map),
`appVersion` starts with `major.minor.patch` (at most 64 characters), `gameBuild` at most 40 (may be empty or missing),
`edits` a whole number of edits (missing: 0). Lengths are counted after cleaning: control characters (line breaks too,
except in the description) and the bidirectional override characters U+202A-U+202E and U+2066-U+2069 are removed and
the text is trimmed. Other fields are ignored (`created` from the app is not trusted; the row gets the server's time).

### What the Worker checks

In this order, before anything is stored:

1. `Content-Length` is present and at most `MAX_ZIP_BYTES`; reading the body stops at that size plus one byte.
2. The rate limit (below).
3. The zip, byte by byte: the end record closes the file (no archive comment), one disk, no Zip64; every central
   directory entry has a name from the list above (so no `../`, no `\`, no `.uasset`), no repeat, no comment, method 0
   or 8, no encryption, declared sizes within the caps; every local header sits exactly where the previous entry ends,
   has the same name, flags, method, CRC and sizes as the central directory; extra fields at most 64 bytes. Every byte of
   the upload belongs to an entry, so nothing can ride along unseen in the stored zip.
4. `manifest.json` (inflated with `DecompressionStream('deflate-raw')`, never past its declared size) is a valid manifest,
   else 422.
5. Every entry inflates to exactly its declared size (a zip bomb stops at the cap); none starts with the Unreal package
   magic `C1 83 2A 9E` or contains the pak magic `E1 12 6F 5A`; the pictures start with `FF D8 FF`; the JSON entries are
   UTF-8 text that opens an object or an array.

The app checks the same things (and much more about the edits themselves) before it uploads and after it downloads.
The Worker's checks are the second line: what reaches R2 is only JSON text and JPEG pictures.

### Removal signature

`DELETE /mods/<id>` carries three headers:

| Header | Value |
|---|---|
| `X-ModShare-Time` | Unix time in seconds, within 300 s of the Worker's clock |
| `X-ModShare-Nonce` | 16-64 characters `A-Z a-z 0-9 - _` (the app sends 16 random bytes in Base64url), never used before |
| `X-ModShare-Signature` | Base64 of the 64-byte ECDSA P-256 / SHA-256 signature in IEEE P1363 form (r then s) |

The signed text is UTF-8 of

```
DELETE\n/mods/<id>\n<time>\n<nonce>
```

(`\n` is one line feed; `<time>` exactly as sent in the header). It is what .NET's
`ECDsa.SignData(..., DSASignatureFormat.IeeeP1363FixedFieldConcatenation)` makes and WebCrypto's `verify` takes.
`MODERATOR_PUBLIC_KEY` is the SubjectPublicKeyInfo in Base64 (PEM armour is accepted too).

Replay: after a good signature the nonce is kept in KV for 600 s (twice the time window), and a signature older than
300 s is refused, so a captured request cannot be sent again. KV is eventually consistent (a write can take up to about
60 s to reach other Cloudflare locations), so a replay sent at once through another location might pass the nonce check;
it can only repeat the same removal of the same id, which then answers 404. The signature binds the method, the id, the
time and the nonce: it cannot be moved to another mod.

## Limits

| Limit | Value | Where |
|---|---|---|
| Package (zip) | 3 MiB | `MAX_ZIP_BYTES` in `wrangler.toml`; the app refuses more before sending |
| Uploads per address | 5 per hour (clock hour, UTC) | `MAX_UPLOADS_PER_HOUR` |
| Uploads in all | 300 per UTC day | `MAX_UPLOADS_PER_DAY` |
| Gallery rows | 1000, then 507 | `MAX_INDEX_ENTRIES` |
| Entries, sizes, manifest fields | see [The package](#the-package) | `worker.js` (the protocol; the app relies on them) |
| Signature time window | ± 300 s; nonce kept 600 s | `worker.js` |

Every upload that passes the size check counts toward both upload limits, valid or not, so junk costs the sender too.
The limits are counted in KV, which is eventually consistent: a burst spread over several locations can go a little
over. R2 and the gallery cap stay the hard limits.

### Cloudflare free tier (check <https://developers.cloudflare.com/workers/platform/pricing/> for today's numbers)

| Service | Free | What the gallery uses |
|---|---|---|
| Workers | 100,000 requests a day; 10 ms CPU per request | one request per upload, gallery read, picture, download or removal |
| R2 storage | 10 GB-month | at most 1000 mods x (3 MiB + 0.9 MiB) = about 4 GB |
| R2 Class A (writes) | 1 million a month | 4 per upload (package, 2 pictures, index), 1 per removal |
| R2 Class B (reads) | 10 million a month | 1 per gallery read, picture or download |
| R2 egress | free | |
| KV reads | 100,000 a day | 2 per upload, 1 per removal |
| KV writes | 1,000 a day | 2 per counted upload (at most 600 a day with 300 uploads), 1 per removal |

CPU: a typical package (a few hundred KB of JSON) takes well under the free 10 ms. A package near the caps (many MB of
JSON to inflate and check) can go over; Cloudflare then stops that request with an error and the upload fails. If that
happens often, the Workers Paid plan (5 USD a month, 30 s CPU) removes the problem; lowering the caps also works.

## Tests

`npm test` in `tools/modshare-worker` (Node 22 or later; nothing to install). The suite (`test/worker.test.mjs`, with
`node:test`) runs every request through `worker.fetch` with in-memory R2 and KV (`test/fake-env.mjs`) and zips made by
`test/zip.mjs`: a good upload read back, every refusal above, the rate limits, the index order and its concurrent-write
retry, the 1000-row cap, and removal (unsigned, signed, replayed, stale, another key, unknown id). Two cases read the
app's own fixtures when present: the package written by the C# side
(`tests/ScumStudio.Tests/Fixtures/Share/sample-share.zip`) must be accepted, and the C# signature test vector
(`tests/ScumStudio.Tests/App/ModShareVectors.json`) must remove its mod.

## Privacy

- A mod's name, author name, description, sectors, size, date and pictures are public: anyone can read `index.json`.
- IP addresses are used only for the hourly upload limit: the KV key is a SHA-256 of the hour and the address (not the
  address itself) and expires after one hour. Nothing else about the sender is kept, and the Worker logs no request
  body, key or address.
