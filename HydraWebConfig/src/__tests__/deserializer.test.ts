import { describe, it, expect } from 'vitest'
import { deserialize } from '../utils/deserializer'

// wraps profile JSON in the root file format
function asFile(profilesJson: string, rootFields = ''): string {
  return `{${rootFields ? rootFields + ',' : ''}"profiles":${profilesJson}}`
}

describe('deserialize', () => {
  it('parses a minimal master config', () => {
    const state = deserialize(asFile('[{"mode":"Master"}]'))
    expect(state.profiles[0].mode).toBe('Master')
  })

  it('parses a slave config', () => {
    const state = deserialize(asFile('[{"mode":"slave"}]'))
    expect(state.profiles[0].mode).toBe('Slave')
  })

  it('parses multiple profiles', () => {
    const state = deserialize(asFile('[{"mode":"Master"},{"mode":"Slave"}]'))
    expect(state.profiles).toHaveLength(2)
  })

  it('throws on invalid JSON', () => {
    expect(() => deserialize('not json')).toThrow('invalid JSON')
  })

  it('throws on missing profiles array', () => {
    expect(() => deserialize('{"mode":"Master"}')).toThrow()
  })

  it('throws on empty profiles array', () => {
    expect(() => deserialize('{"profiles":[]}')).toThrow()
  })

  it('throws on missing mode', () => {
    expect(() => deserialize(asFile('[{"name":"test"}]'))).toThrow()
  })

  it('throws on invalid mode', () => {
    expect(() => deserialize(asFile('[{"mode":"Boss"}]'))).toThrow()
  })

  it('maps log level aliases in profiles', () => {
    const state = deserialize(asFile('[{"mode":"Master","logLevel":"dbug"}]'))
    // logLevel is no longer on profile — it's on root; profile entry should not have it
    expect(state.profiles[0]).not.toHaveProperty('logLevel')
  })

  it('parses root logLevel', () => {
    const state = deserialize(asFile('[{"mode":"Master"}]', '"logLevel":"dbug"'))
    expect(state.logLevel).toBe('debug')
  })

  it('maps information to info for root logLevel', () => {
    const state = deserialize(asFile('[{"mode":"Master"}]', '"logLevel":"information"'))
    expect(state.logLevel).toBe('info')
  })

  it('parses root autoUpdate: false', () => {
    const state = deserialize(asFile('[{"mode":"Master"}]', '"autoUpdate":false'))
    expect(state.autoUpdate).toBe(false)
  })

  it('leaves autoUpdate undefined when absent', () => {
    const state = deserialize(asFile('[{"mode":"Master"}]'))
    expect(state.autoUpdate).toBeUndefined()
  })

  it('parses hosts and neighbours', () => {
    const json = asFile(JSON.stringify([{
      mode: 'Master',
      hosts: [
        {
          name: 'pc',
          neighbours: [{ direction: 'right', name: 'mac', sourceStart: 25, mirror: false }],
        },
      ],
    }]))
    const state = deserialize(json)
    const host = state.profiles[0].hosts![0]
    expect(host.name).toBe('pc')
    const n = host.neighbours![0]
    expect(n.direction).toBe('Right')
    expect(n.sourceStart).toBe(25)
    expect(n.mirror).toBe(false)
  })

  it('parses conditions', () => {
    const json = asFile(JSON.stringify([{ mode: 'Master', conditions: { ssid: 'home', screenCount: 2 } }]))
    const state = deserialize(json)
    expect(state.profiles[0].conditions?.ssid).toBe('home')
    expect(state.profiles[0].conditions?.screenCount).toBe(2)
  })

  it('preserves undefined for omitted optional profile fields', () => {
    const state = deserialize(asFile('[{"mode":"Slave"}]'))
    expect(state.name).toBeUndefined()
    expect(state.profiles[0].hosts).toBeUndefined()
  })

  it('round-trips the master example', () => {
    const example = asFile(JSON.stringify([{
      networkConfig: 'abc123',
      mode: 'Master',
      hosts: [
        { name: 'macbook', neighbours: [{ direction: 'right', name: 'desktop' }] },
        { name: 'desktop', neighbours: [{ direction: 'right', name: 'monitor' }] },
      ],
    }]), '"name":"macbook","lockFile":"hydra.lock"')
    const state = deserialize(example)
    expect(state.name).toBe('macbook')
    expect(state.profiles[0].mode).toBe('Master')
    expect(state.profiles[0].hosts).toHaveLength(2)
    expect(state.profiles[0].hosts![0].neighbours![0].direction).toBe('Right')
  })
})
