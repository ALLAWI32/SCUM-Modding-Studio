// In-memory stand-ins for the Worker's bindings: R2 with etags and onlyIf, KV with expirationTtl, and a clock the test
// sets (the Worker reads env.NOW when it is a function). They follow the documented behaviour the Worker relies on:
// a put whose precondition fails returns null and stores nothing; a KV value is gone once its TTL has passed.

export const START = Date.UTC(2026, 9, 12, 12, 0, 0);

export function createEnv({ now = START, publicKey, vars = {} } = {}) {
  const clock = { now };
  const env = {
    MODS: new FakeR2(),
    KV: new FakeKV(clock),
    MODERATOR_PUBLIC_KEY: publicKey,
    MAX_ZIP_BYTES: '3145728',
    MAX_UPLOADS_PER_HOUR: '5',
    MAX_UPLOADS_PER_DAY: '300',
    MAX_INDEX_ENTRIES: '1000',
    ...vars,
    NOW: () => clock.now,
  };
  return { env, clock };
}

export class FakeR2 {
  objects = new Map();
  puts = [];
  // Runs before every put (not for puts made inside it): a test writes here to stand for another Worker in between.
  beforePut = null;
  #version = 0;
  #inHook = false;

  async put(key, value, options = {}) {
    if (this.beforePut && !this.#inHook) {
      this.#inHook = true;
      try {
        await this.beforePut(key, options);
      } finally {
        this.#inHook = false;
      }
    }

    const bytes = await toBytes(value);
    const current = this.objects.get(key);
    const only = options.onlyIf ?? {};
    this.puts.push({ key, conditional: options.onlyIf !== undefined });
    if (only.etagMatches !== undefined && current?.etag !== only.etagMatches) {
      return null;
    }

    if (only.etagDoesNotMatch !== undefined && current !== undefined && current.etag === only.etagDoesNotMatch) {
      return null;
    }

    const object = { key, bytes, etag: `etag${++this.#version}`, httpMetadata: { ...options.httpMetadata }, uploaded: new Date() };
    this.objects.set(key, object);
    return describe(object);
  }

  async get(key) {
    const object = this.objects.get(key);
    if (!object) {
      return null;
    }

    return {
      ...describe(object),
      body: new Blob([object.bytes]).stream(),
      arrayBuffer: async () => object.bytes.slice().buffer,
      text: async () => new TextDecoder().decode(object.bytes),
      json: async () => JSON.parse(new TextDecoder().decode(object.bytes)),
    };
  }

  async head(key) {
    const object = this.objects.get(key);
    return object ? describe(object) : null;
  }

  async delete(keys) {
    for (const key of [].concat(keys)) {
      this.objects.delete(key);
    }
  }

  keys() {
    return [...this.objects.keys()].sort();
  }

  bytes(key) {
    return this.objects.get(key)?.bytes;
  }

  index() {
    const object = this.objects.get('index.json');
    return object ? JSON.parse(new TextDecoder().decode(object.bytes)) : null;
  }
}

export class FakeKV {
  values = new Map();
  writes = 0;

  constructor(clock) {
    this.clock = clock;
  }

  async get(key) {
    const entry = this.values.get(key);
    if (!entry) {
      return null;
    }

    if (entry.expires !== undefined && entry.expires <= this.clock.now) {
      this.values.delete(key);
      return null;
    }

    return entry.value;
  }

  async put(key, value, options = {}) {
    const ttl = options.expirationTtl;
    if (ttl !== undefined && !(ttl >= 60)) {
      throw new Error('KV: expirationTtl must be at least 60 seconds');
    }

    if (new TextEncoder().encode(key).length > 512) {
      throw new Error('KV: keys are at most 512 bytes');
    }

    this.writes++;
    this.values.set(key, { value: String(value), expires: ttl === undefined ? undefined : this.clock.now + ttl * 1000 });
  }

  async delete(key) {
    this.values.delete(key);
  }
}

function describe(object) {
  return {
    key: object.key,
    size: object.bytes.length,
    etag: object.etag,
    httpEtag: `"${object.etag}"`,
    httpMetadata: object.httpMetadata,
    uploaded: object.uploaded,
  };
}

async function toBytes(value) {
  if (typeof value === 'string') {
    return new TextEncoder().encode(value);
  }

  if (value instanceof ArrayBuffer) {
    return new Uint8Array(value.slice(0));
  }

  if (ArrayBuffer.isView(value)) {
    return new Uint8Array(value.buffer.slice(value.byteOffset, value.byteOffset + value.byteLength));
  }

  if (value instanceof Blob) {
    return new Uint8Array(await value.arrayBuffer());
  }

  if (value instanceof ReadableStream) {
    return new Uint8Array(await new Response(value).arrayBuffer());
  }

  throw new TypeError(`R2 put: unsupported value ${Object.prototype.toString.call(value)}`);
}
