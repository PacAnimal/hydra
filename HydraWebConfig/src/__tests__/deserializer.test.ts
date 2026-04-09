import { describe, it, expect } from 'vitest'
import { deserialize } from '../utils/deserializer'

describe('deserialize', () => {
  it('parses a minimal master config', () => {
    const { configs, multiConfig } = deserialize('{"mode":"Master"}')
    expect(multiConfig).toBe(false)
    expect(configs[0].mode).toBe('Master')
  })

  it('parses a slave config', () => {
    const { configs } = deserialize('{"mode":"slave"}')
    expect(configs[0].mode).toBe('Slave')
  })

  it('parses an array as multi-config', () => {
    const { configs, multiConfig } = deserialize('[{"mode":"Master"},{"mode":"Slave"}]')
    expect(multiConfig).toBe(true)
    expect(configs).toHaveLength(2)
  })

  it('throws on invalid JSON', () => {
    expect(() => deserialize('not json')).toThrow('invalid JSON')
  })

  it('throws on empty array', () => {
    expect(() => deserialize('[]')).toThrow()
  })

  it('throws on missing mode', () => {
    expect(() => deserialize('{"name":"test"}')).toThrow()
  })

  it('throws on invalid mode', () => {
    expect(() => deserialize('{"mode":"Boss"}')).toThrow()
  })

  it('maps log level aliases', () => {
    const { configs } = deserialize('{"mode":"Master","logLevel":"dbug"}')
    expect(configs[0].logLevel).toBe('debug')
  })

  it('maps information to info', () => {
    const { configs } = deserialize('{"mode":"Master","logLevel":"information"}')
    expect(configs[0].logLevel).toBe('info')
  })

  it('parses hosts and neighbours', () => {
    const json = JSON.stringify({
      mode: 'Master',
      hosts: [
        {
          name: 'pc',
          neighbours: [{ direction: 'right', name: 'mac', sourceStart: 25, mirror: false }],
        },
      ],
    })
    const { configs } = deserialize(json)
    const host = configs[0].hosts![0]
    expect(host.name).toBe('pc')
    const n = host.neighbours![0]
    expect(n.direction).toBe('Right')
    expect(n.sourceStart).toBe(25)
    expect(n.mirror).toBe(false)
  })

  it('parses conditions', () => {
    const json = JSON.stringify({ mode: 'Master', conditions: { ssid: 'home', screenCount: 2 } })
    const { configs } = deserialize(json)
    expect(configs[0].conditions?.ssid).toBe('home')
    expect(configs[0].conditions?.screenCount).toBe(2)
  })

  it('preserves undefined for omitted optional fields', () => {
    const { configs } = deserialize('{"mode":"Slave"}')
    expect(configs[0].name).toBeUndefined()
    expect(configs[0].hosts).toBeUndefined()
    expect(configs[0].logLevel).toBeUndefined()
  })

  it('round-trips the master example', () => {
    const example = JSON.stringify({
      networkConfig: 'abc123',
      mode: 'Master',
      logLevel: 'info',
      name: 'macbook',
      lockFile: 'hydra.lock',
      hosts: [
        { name: 'macbook', neighbours: [{ direction: 'right', name: 'desktop' }] },
        { name: 'desktop', neighbours: [{ direction: 'right', name: 'monitor' }] },
      ],
    })
    const { configs } = deserialize(example)
    expect(configs[0].mode).toBe('Master')
    expect(configs[0].hosts).toHaveLength(2)
    expect(configs[0].hosts![0].neighbours![0].direction).toBe('Right')
  })
})
