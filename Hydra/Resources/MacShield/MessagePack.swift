import Foundation

// minimal messagepack codec for the shield ipc — no ext types, string map keys only.
// spec: https://github.com/msgpack/msgpack/blob/master/spec.md

enum MsgPackValue {
  case `nil`
  case bool(Bool)
  case int(Int64)
  case uint(UInt64)
  case double(Double)
  case string(String)
  case binary(Data)
  case array([MsgPackValue])
  // array of pairs keeps insertion order
  case map([(String, MsgPackValue)])

  // first value under key in a map; nil for non-maps or missing keys
  subscript(key: String) -> MsgPackValue? {
    guard case let .map(pairs) = self else { return nil }
    return pairs.first { $0.0 == key }?.1
  }

  var stringValue: String? {
    if case let .string(s) = self { return s }
    return nil
  }

  // accepts both signed and unsigned encodings; nil if out of Int64 range
  var int64Value: Int64? {
    switch self {
    case let .int(i): return i
    case let .uint(u): return u <= UInt64(Int64.max) ? Int64(u) : nil
    default: return nil
    }
  }

  var boolValue: Bool? {
    if case let .bool(b) = self { return b }
    return nil
  }

  // float32 is widened on decode; integers convert
  var doubleValue: Double? {
    switch self {
    case let .double(d): return d
    case let .int(i): return Double(i)
    case let .uint(u): return Double(u)
    default: return nil
    }
  }

  var arrayValue: [MsgPackValue]? {
    if case let .array(a) = self { return a }
    return nil
  }
}

enum MsgPackError: Error {
  case truncated
  case trailingBytes(Int)
  case unsupportedType(UInt8)
  case nonStringKey
  case invalidUtf8
  case frameTooLarge(Int)
  case nestedTooDeeply
}

// the arrays and maps a value may sit inside, as ShieldChannel's MaxDepth
let MaxNestingDepth = 64

// --- encoder ---

private func appendBE<T: FixedWidthInteger>(_ out: inout Data, _ v: T) {
  withUnsafeBytes(of: v.bigEndian) { out.append(contentsOf: $0) }
}

// header for str/bin/array/map: fix form (if any), then 8/16/32-bit length
private func appendLength(_ out: inout Data, _ n: Int, fix: (base: UInt8, max: Int)?, b8: UInt8?, b16: UInt8, b32: UInt8) {
  if let fix = fix, n <= fix.max {
    out.append(fix.base | UInt8(n))
  } else if let b8 = b8, n <= 0xff {
    out.append(b8); out.append(UInt8(n))
  } else if n <= 0xffff {
    out.append(b16); appendBE(&out, UInt16(n))
  } else {
    out.append(b32); appendBE(&out, UInt32(n))
  }
}

private func encodeUInt(_ out: inout Data, _ u: UInt64) {
  if u <= 0x7f {
    out.append(UInt8(u))
  } else if u <= 0xff {
    out.append(0xcc); out.append(UInt8(u))
  } else if u <= 0xffff {
    out.append(0xcd); appendBE(&out, UInt16(u))
  } else if u <= 0xffff_ffff {
    out.append(0xce); appendBE(&out, UInt32(u))
  } else {
    out.append(0xcf); appendBE(&out, u)
  }
}

private func encodeInt(_ out: inout Data, _ i: Int64) {
  if i >= 0 {
    encodeUInt(&out, UInt64(i))
  } else if i >= -32 {
    out.append(UInt8(bitPattern: Int8(i)))
  } else if i >= Int64(Int8.min) {
    out.append(0xd0); appendBE(&out, Int8(i))
  } else if i >= Int64(Int16.min) {
    out.append(0xd1); appendBE(&out, Int16(i))
  } else if i >= Int64(Int32.min) {
    out.append(0xd2); appendBE(&out, Int32(i))
  } else {
    out.append(0xd3); appendBE(&out, i)
  }
}

private func encode(_ out: inout Data, _ v: MsgPackValue) {
  switch v {
  case .nil: out.append(0xc0)
  case let .bool(b): out.append(b ? 0xc3 : 0xc2)
  case let .int(i): encodeInt(&out, i)
  case let .uint(u): encodeUInt(&out, u)
  case let .double(d): out.append(0xcb); appendBE(&out, d.bitPattern)
  case let .string(s):
    let bytes = Data(s.utf8)
    appendLength(&out, bytes.count, fix: (0xa0, 31), b8: 0xd9, b16: 0xda, b32: 0xdb)
    out.append(bytes)
  case let .binary(d):
    appendLength(&out, d.count, fix: nil, b8: 0xc4, b16: 0xc5, b32: 0xc6)
    out.append(d)
  case let .array(a):
    appendLength(&out, a.count, fix: (0x90, 15), b8: nil, b16: 0xdc, b32: 0xdd)
    for e in a { encode(&out, e) }
  case let .map(m):
    appendLength(&out, m.count, fix: (0x80, 15), b8: nil, b16: 0xde, b32: 0xdf)
    for (k, e) in m { encode(&out, .string(k)); encode(&out, e) }
  }
}

func msgPackEncode(_ v: MsgPackValue) -> Data {
  var out = Data()
  encode(&out, v)
  return out
}

// --- decoder ---

private struct MsgPackReader {
  let bytes: [UInt8]
  var pos = 0

  var remaining: Int { bytes.count - pos }

  mutating func byte() throws -> UInt8 {
    guard remaining >= 1 else { throw MsgPackError.truncated }
    defer { pos += 1 }
    return bytes[pos]
  }

  mutating func be<T: FixedWidthInteger>(_: T.Type) throws -> T {
    let n = MemoryLayout<T>.size
    guard remaining >= n else { throw MsgPackError.truncated }
    var v: T = 0
    for i in 0..<n { v = (v << 8) | T(truncatingIfNeeded: bytes[pos + i]) }
    pos += n
    return v
  }

  mutating func take(_ n: Int) throws -> Data {
    guard n >= 0, remaining >= n else { throw MsgPackError.truncated }
    defer { pos += n }
    return Data(bytes[pos..<pos + n])
  }

  mutating func string(_ n: Int) throws -> String {
    guard let s = String(data: try take(n), encoding: .utf8) else { throw MsgPackError.invalidUtf8 }
    return s
  }

  // every element takes at least one byte, so a count beyond the remaining bytes is truncated input
  func checkCount(_ n: Int) throws {
    guard n <= remaining else { throw MsgPackError.truncated }
  }

  mutating func array(_ n: Int, _ depth: Int) throws -> MsgPackValue {
    try checkCount(n)
    var a: [MsgPackValue] = []
    a.reserveCapacity(n)
    for _ in 0..<n { a.append(try value(depth + 1)) }
    return .array(a)
  }

  mutating func map(_ n: Int, _ depth: Int) throws -> MsgPackValue {
    try checkCount(n)
    var m: [(String, MsgPackValue)] = []
    m.reserveCapacity(n)
    for _ in 0..<n {
      guard case let .string(k) = try value(depth + 1) else { throw MsgPackError.nonStringKey }
      m.append((k, try value(depth + 1)))
    }
    return .map(m)
  }

  // depth counts the arrays and maps around the value, which a hostile frame could nest without end
  mutating func value(_ depth: Int = 0) throws -> MsgPackValue {
    guard depth <= MaxNestingDepth else { throw MsgPackError.nestedTooDeeply }
    let t = try byte()
    switch t {
    case 0x00...0x7f: return .int(Int64(t))
    case 0x80...0x8f: return try map(Int(t & 0x0f), depth)
    case 0x90...0x9f: return try array(Int(t & 0x0f), depth)
    case 0xa0...0xbf: return .string(try string(Int(t & 0x1f)))
    case 0xc0: return .nil
    case 0xc2: return .bool(false)
    case 0xc3: return .bool(true)
    case 0xc4: return .binary(try take(Int(try be(UInt8.self))))
    case 0xc5: return .binary(try take(Int(try be(UInt16.self))))
    case 0xc6: return .binary(try take(Int(try be(UInt32.self))))
    case 0xca: return .double(Double(Float(bitPattern: try be(UInt32.self))))
    case 0xcb: return .double(Double(bitPattern: try be(UInt64.self)))
    case 0xcc: return .int(Int64(try be(UInt8.self)))
    case 0xcd: return .int(Int64(try be(UInt16.self)))
    case 0xce: return .int(Int64(try be(UInt32.self)))
    case 0xcf:
      // keep small values as .int so equal numbers compare equal regardless of encoder
      let u = try be(UInt64.self)
      return u <= UInt64(Int64.max) ? .int(Int64(u)) : .uint(u)
    case 0xd0: return .int(Int64(try be(Int8.self)))
    case 0xd1: return .int(Int64(try be(Int16.self)))
    case 0xd2: return .int(Int64(try be(Int32.self)))
    case 0xd3: return .int(try be(Int64.self))
    case 0xd9: return .string(try string(Int(try be(UInt8.self))))
    case 0xda: return .string(try string(Int(try be(UInt16.self))))
    case 0xdb: return .string(try string(Int(try be(UInt32.self))))
    case 0xdc: return try array(Int(try be(UInt16.self)), depth)
    case 0xdd: return try array(Int(try be(UInt32.self)), depth)
    case 0xde: return try map(Int(try be(UInt16.self)), depth)
    case 0xdf: return try map(Int(try be(UInt32.self)), depth)
    case 0xe0...0xff: return .int(Int64(Int8(bitPattern: t)))
    default: throw MsgPackError.unsupportedType(t)  // 0xc1 and ext types
    }
  }
}

// decodes exactly one value; throws on truncated input or trailing bytes
func msgPackDecode(_ data: Data) throws -> MsgPackValue {
  var r = MsgPackReader(bytes: [UInt8](data))
  let v = try r.value()
  guard r.remaining == 0 else { throw MsgPackError.trailingBytes(r.remaining) }
  return v
}

// --- framing: 4-byte little-endian length + payload ---

let MaxFrameLength = 16 * 1024 * 1024

func frame(_ payload: Data) -> Data {
  var out = Data(capacity: 4 + payload.count)
  withUnsafeBytes(of: UInt32(payload.count).littleEndian) { out.append(contentsOf: $0) }
  out.append(payload)
  return out
}

// accumulates stream bytes and yields complete frame payloads
struct FrameReader {
  private var buffer = Data()

  mutating func append(_ data: Data) {
    buffer.append(data)
  }

  // next complete payload, or nil if more bytes are needed; throws on an oversized declared length
  mutating func next() throws -> Data? {
    guard buffer.count >= 4 else { return nil }
    let s = buffer.startIndex
    let len = Int(buffer[s]) | Int(buffer[s + 1]) << 8 | Int(buffer[s + 2]) << 16 | Int(buffer[s + 3]) << 24
    guard len <= MaxFrameLength else { throw MsgPackError.frameTooLarge(len) }
    guard buffer.count >= 4 + len else { return nil }
    let payload = Data(buffer[(s + 4)..<(s + 4 + len)])
    buffer = Data(buffer[(s + 4 + len)...])
    return payload
  }
}
