#!/usr/bin/env node
// Read the .NET single-file bundle manifest and optionally extract its entries.
//
// The apphost carries a marker patched in at publish time:
//   int64 headerOffset, then 32-byte signature (SHA-256 of ".net core bundle").
// An unbundled apphost keeps the placeholder, so headerOffset is 0 and the file is
// not a bundle. From headerOffset the manifest is:
//   majorVersion(u32) minorVersion(u32) fileCount(u32) bundleId(string)
//   [v2+] depsJson offset/size, runtimeConfig offset/size, flags(u64)
//   fileCount x entry: offset(i64) size(i64) [v6+] compressedSize(i64) type(u8) name(string)
// All strings are 7-bit-length-prefixed UTF-8, as BinaryWriter.Write(string) does.
//
// Everything here is read-only with respect to the bundle: extraction writes only
// into the directory the caller names, after validating every entry against the
// file length and rejecting absolute, traversing or duplicate paths.

import { createHash } from 'node:crypto'
import { closeSync, existsSync, mkdirSync, openSync, readFileSync, readSync, statSync, writeFileSync } from "node:fs"
import { dirname, join, resolve, sep } from 'node:path'
import { inflateRawSync } from 'node:zlib'

const SIGNATURE = Buffer.from(
  '8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae',
  'hex',
)

const FILE_TYPE = { 0: 'unknown', 1: 'assembly', 2: 'native', 3: 'deps', 4: 'runtimeconfig', 5: 'symbols' }

class BundleError extends Error {}

function fail(message) {
  throw new BundleError(message)
}

function readString(buffer, state, limit) {
  let length = 0
  let shift = 0
  for (;;) {
    if (state.pos >= limit) fail('manifest ended inside a string length')
    const byte = buffer[state.pos++]
    length |= (byte & 0x7f) << shift
    if ((byte & 0x80) === 0) break
    shift += 7
    if (shift > 35) fail('string length uses more than five varint bytes')
  }
  if (length > 1 << 20) fail(`string length ${length} exceeds the 1 MiB manifest limit`)
  if (state.pos + length > limit) fail('manifest ended inside a string body')
  const value = buffer.toString('utf8', state.pos, state.pos + length)
  state.pos += length
  return value
}

function readU32(buffer, state, limit) {
  if (state.pos + 4 > limit) fail('manifest ended inside a u32')
  const value = buffer.readUInt32LE(state.pos)
  state.pos += 4
  return value
}

function readI64(buffer, state, limit) {
  if (state.pos + 8 > limit) fail('manifest ended inside an i64')
  const value = Number(buffer.readBigInt64LE(state.pos))
  state.pos += 8
  return value
}

function readU64(buffer, state, limit) {
  if (state.pos + 8 > limit) fail('manifest ended inside a u64')
  const value = buffer.readBigUInt64LE(state.pos).toString()
  state.pos += 8
  return value
}

// Rejects anything that would escape the extraction root or collide with another
// entry. Bundle paths always use '/' regardless of the host platform.
function safeRelativePath(name) {
  if (!name) fail('entry has an empty name')
  if (name.includes('\0')) fail(`entry name contains a NUL byte: ${JSON.stringify(name)}`)
  if (name.startsWith('/') || name.startsWith('\\')) fail(`entry name is absolute: ${name}`)
  if (/^[A-Za-z]:/.test(name)) fail(`entry name carries a drive letter: ${name}`)
  const parts = name.split(/[\\/]/)
  for (const part of parts) {
    if (part === '' || part === '.') fail(`entry name has an empty or dot segment: ${name}`)
    if (part === '..') fail(`entry name traverses upward: ${name}`)
  }
  return parts.join('/')
}

export function readBundle(path) {
  const full = resolve(path)
  if (!existsSync(full)) fail(`bundle does not exist: ${full}`)
  const size = statSync(full).size
  if (size < 64) fail(`file is too small to be a bundle: ${full}`)

  const whole = readFileSync(full)
  const candidates = []
  let at = whole.indexOf(SIGNATURE)
  while (at >= 0 && candidates.length < 32) {
    candidates.push(at)
    at = whole.indexOf(SIGNATURE, at + 1)
  }
  if (candidates.length === 0) {
    fail('single-file bundle marker not found; this is not a bundled .NET app')
  }

  // An unbundled apphost keeps the marker placeholder, whose header offset is 0.
  const problems = []
  for (const signatureAt of candidates) {
    if (signatureAt < 8) { problems.push('marker sits before the file start'); continue }
    const headerOffset = Number(whole.readBigInt64LE(signatureAt - 8))
    if (headerOffset === 0) { problems.push('marker is the unbundled placeholder (header offset 0)'); continue }
    if (headerOffset <= 0 || headerOffset >= size) {
      problems.push(`header offset ${headerOffset} is outside the file`)
      continue
    }
    try {
      const parsed = parseManifest(whole, size, headerOffset)
      return { ...parsed, path: full, size, markerOffset: signatureAt - 8 }
    } catch (error) {
      problems.push(error instanceof BundleError ? error.message : String(error))
    }
  }
  fail(`no valid bundle manifest: ${problems.join('; ')}`)
}

function parseManifest(buffer, fileSize, manifestOffset) {
  const manifestLength = fileSize - manifestOffset
  const manifest = buffer.subarray(manifestOffset, fileSize)
  const state = { pos: 0 }
  const major = readU32(manifest, state, manifestLength)
  const minor = readU32(manifest, state, manifestLength)
  if (major < 1 || major > 6) fail(`unsupported bundle major version ${major}`)
  const count = readU32(manifest, state, manifestLength)
  if (count > 100000) fail(`bundle declares ${count} entries, which is beyond the supported limit`)
  const bundleId = readString(manifest, state, manifestLength)

  let depsJson = null
  let runtimeConfig = null
  let flags = '0'
  if (major >= 2) {
    const depsOffset = readI64(manifest, state, manifestLength)
    const depsSize = readI64(manifest, state, manifestLength)
    const rcOffset = readI64(manifest, state, manifestLength)
    const rcSize = readI64(manifest, state, manifestLength)
    flags = readU64(manifest, state, manifestLength)
    depsJson = depsSize > 0 ? { offset: depsOffset, size: depsSize } : null
    runtimeConfig = rcSize > 0 ? { offset: rcOffset, size: rcSize } : null
  }

  const entries = []
  const seen = new Set()
  for (let index = 0; index < count; index++) {
    const offset = readI64(manifest, state, manifestLength)
    const length = readI64(manifest, state, manifestLength)
    const compressedSize = major >= 6 ? readI64(manifest, state, manifestLength) : 0
    if (state.pos >= manifestLength) fail('manifest ended before an entry type byte')
    const typeCode = manifest[state.pos++]
    const name = safeRelativePath(readString(manifest, state, manifestLength))
    const key = name.toLowerCase()
    if (seen.has(key)) fail(`bundle repeats an entry name: ${name}`)
    seen.add(key)
    if (offset < 0 || length < 0) fail(`entry ${name} has a negative offset or size`)
    const stored = compressedSize > 0 ? compressedSize : length
    if (offset + stored > manifestOffset) {
      fail(`entry ${name} runs past the manifest (${offset}+${stored} > ${manifestOffset})`)
    }
    entries.push({
      name,
      type: FILE_TYPE[typeCode] ?? `type-${typeCode}`,
      typeCode,
      offset,
      size: length,
      compressedSize,
      storedSize: stored,
    })
  }

  return {
    major,
    minor,
    bundleId,
    flags,
    depsJson,
    runtimeConfig,
    entries,
    entryCount: entries.length,
    compressedEntries: entries.filter(e => e.compressedSize > 0).length,
  }
}

export function readEntryBytes(bundle, entry) {
  const handle = openSync(bundle.path, 'r')
  try {
    const raw = Buffer.alloc(entry.storedSize)
    readSync(handle, raw, 0, entry.storedSize, entry.offset)
    if (entry.compressedSize > 0) {
      const inflated = inflateRawSync(raw)
      if (inflated.length !== entry.size) {
        fail(`entry ${entry.name} inflated to ${inflated.length} bytes, manifest says ${entry.size}`)
      }
      return inflated
    }
    if (raw.length !== entry.size) fail(`entry ${entry.name} is shorter than the manifest declares`)
    return raw
  } finally {
    closeSync(handle)
  }
}

export function readJsonEntry(bundle, entry) {
  if (!entry) return null
  return JSON.parse(readEntryBytes(bundle, entry).toString('utf8'))
}

export function describe(bundle) {
  const runtime = bundle.entries.find(e => e.type === 'runtimeconfig')
  const config = runtime ? readJsonEntry(bundle, runtime) : null
  return {
    path: bundle.path,
    bundleVersion: `${bundle.major}.${bundle.minor}`,
    bundleId: bundle.bundleId,
    entries: bundle.entryCount,
    compressed: bundle.compressedEntries,
    runtime: config?.runtimeOptions?.tfm ?? null,
    frameworks: (config?.runtimeOptions?.frameworks ?? []).map(f => `${f.name} ${f.version}`),
    assemblies: bundle.entries.filter(e => e.type === 'assembly').map(e => e.name),
  }
}

// Extraction writes into `target`, never into the bundle. Every destination is
// re-checked against the resolved root so a crafted name cannot escape it.
export function extractBundle(bundle, target, { filter = null } = {}) {
  const root = resolve(target)
  mkdirSync(root, { recursive: true })
  const written = []
  for (const entry of bundle.entries) {
    if (filter && !filter(entry)) continue
    const destination = resolve(root, ...entry.name.split('/'))
    if (destination !== root && !destination.startsWith(root + sep)) {
      fail(`entry ${entry.name} resolves outside the extraction root`)
    }
    mkdirSync(dirname(destination), { recursive: true })
    const bytes = readEntryBytes(bundle, entry)
    writeFileSync(destination, bytes)
    written.push({
      name: entry.name,
      bytes: bytes.length,
      sha256: createHash('sha256').update(bytes).digest('hex'),
    })
  }
  return written
}

function usage() {
  return [
    'usage:',
    '  node bundle-manifest.mjs inspect <bundle.exe>',
    '  node bundle-manifest.mjs extract <bundle.exe> <target-dir> [--assemblies-only]',
  ].join('\n')
}

if (process.argv[1] && import.meta.url === new URL(`file://${process.argv[1].replace(/\\/g, '/')}`).href) {
  const [, , command, bundlePath, target] = process.argv
  try {
    if (!command || !bundlePath) {
      console.error(usage())
      process.exit(2)
    }
    const bundle = readBundle(bundlePath)
    if (command === 'inspect') {
      console.log(JSON.stringify({ ...describe(bundle), files: bundle.entries }, null, 2))
    } else if (command === 'extract') {
      if (!target) {
        console.error(usage())
        process.exit(2)
      }
      const assembliesOnly = process.argv.includes('--assemblies-only')
      const written = extractBundle(bundle, target, {
        filter: assembliesOnly ? e => e.typeCode === 1 : null,
      })
      console.log(JSON.stringify({ target: resolve(target), files: written.length, entries: written }, null, 2))
    } else {
      console.error(usage())
      process.exit(2)
    }
  } catch (error) {
    console.error(error instanceof BundleError ? `bundle error: ${error.message}` : error.stack)
    process.exit(1)
  }
}

export { BundleError }
export const BUNDLE_SIGNATURE = SIGNATURE
export const bundleFilePath = join
