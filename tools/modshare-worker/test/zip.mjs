// A minimal zip writer for the tests: good packages, and bad ones with exactly one thing wrong (a declared size, a local
// header that disagrees with the central directory, hidden bytes, a comment, a method or flag the Worker refuses).
import { deflateRawSync } from 'node:zlib';

/**
 * entries: [{ name, data, method = 8, flags = 0, size, localSize, localName, extra, descriptor, after }]
 *   size       declared uncompressed size in both headers (default: the real one)
 *   localSize  declared uncompressed size in the local header only
 *   localName  the name in the local header only
 *   extra      extra-field bytes in both headers
 *   descriptor sizes in a data descriptor after the data (flag 8), zeros in the local header
 *   after      bytes written after the entry's data, belonging to no entry
 * options: { comment } archive comment
 */
export function makeZip(entries, { comment = '' } = {}) {
  const parts = [];
  const central = [];
  let offset = 0;
  for (const entry of entries) {
    const data = bytesOf(entry.data);
    const method = entry.method ?? 8;
    const stored = method === 8 ? new Uint8Array(deflateRawSync(data)) : data;
    const crc = crc32(data);
    const size = entry.size ?? data.length;
    const name = bytesOf(entry.name);
    const localName = bytesOf(entry.localName ?? entry.name);
    const extra = entry.extra ?? new Uint8Array(0);
    const flags = (entry.flags ?? 0) | (entry.descriptor ? 0x0008 : 0);

    const local = new Writer(30 + localName.length + extra.length);
    local.u32(0x04034b50).u16(20).u16(flags).u16(method).u16(0).u16(0x5521);
    local.u32(entry.descriptor ? 0 : crc).u32(entry.descriptor ? 0 : stored.length).u32(entry.descriptor ? 0 : entry.localSize ?? size);
    local.u16(localName.length).u16(extra.length).bytes(localName).bytes(extra);
    parts.push(local.out, stored);
    let length = local.out.length + stored.length;
    if (entry.descriptor) {
      const descriptor = new Writer(16).u32(0x08074b50).u32(crc).u32(stored.length).u32(size);
      parts.push(descriptor.out);
      length += 16;
    }

    if (entry.after) {
      parts.push(bytesOf(entry.after));
      length += bytesOf(entry.after).length;
    }

    central.push({ name, extra, flags, method, crc, compressed: stored.length, size, offset });
    offset += length;
  }

  const directoryStart = offset;
  for (const e of central) {
    const record = new Writer(46 + e.name.length + e.extra.length);
    record.u32(0x02014b50).u16(20).u16(20).u16(e.flags).u16(e.method).u16(0).u16(0x5521);
    record.u32(e.crc).u32(e.compressed).u32(e.size).u16(e.name.length).u16(e.extra.length).u16(0).u16(0).u16(0).u32(0).u32(e.offset);
    record.bytes(e.name).bytes(e.extra);
    parts.push(record.out);
    offset += record.out.length;
  }

  const commentBytes = bytesOf(comment);
  const end = new Writer(22 + commentBytes.length);
  end.u32(0x06054b50).u16(0).u16(0).u16(central.length).u16(central.length).u32(offset - directoryStart).u32(directoryStart);
  end.u16(commentBytes.length).bytes(commentBytes);
  parts.push(end.out);
  return concat(parts);
}

/** A JPEG-looking picture of `length` bytes (FF D8 FF ... FF D9) whose filler never holds a pak or package magic. */
export function jpeg(length) {
  const bytes = new Uint8Array(length);
  for (let i = 0; i < length; i++) {
    bytes[i] = (i * 7) % 200;
  }

  bytes.set([0xff, 0xd8, 0xff, 0xe0], 0);
  bytes.set([0xff, 0xd9], length - 2);
  return bytes;
}

export function concat(parts) {
  const out = new Uint8Array(parts.reduce((sum, part) => sum + part.length, 0));
  let offset = 0;
  for (const part of parts) {
    out.set(part, offset);
    offset += part.length;
  }

  return out;
}

export function bytesOf(value) {
  return typeof value === 'string' ? new TextEncoder().encode(value) : new Uint8Array(value);
}

const CRC_TABLE = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) {
    c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  }

  return c >>> 0;
});

export function crc32(bytes) {
  let crc = 0xffffffff;
  for (const byte of bytes) {
    crc = CRC_TABLE[(crc ^ byte) & 0xff] ^ (crc >>> 8);
  }

  return (crc ^ 0xffffffff) >>> 0;
}

class Writer {
  constructor(length) {
    this.out = new Uint8Array(length);
    this.view = new DataView(this.out.buffer);
    this.at = 0;
  }

  u16(value) {
    this.view.setUint16(this.at, value, true);
    this.at += 2;
    return this;
  }

  u32(value) {
    this.view.setUint32(this.at, value >>> 0, true);
    this.at += 4;
    return this;
  }

  bytes(value) {
    this.out.set(value, this.at);
    this.at += value.length;
    return this;
  }
}
