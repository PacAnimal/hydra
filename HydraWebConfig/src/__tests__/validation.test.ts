import { describe, it, expect } from 'vitest'
import { validate } from '../utils/validation'
import type { HydraProfile } from '../types'

const p = (overrides: Partial<HydraProfile>): HydraProfile => ({
  profileName: 'test',
  mode: 'Master',
  ...overrides,
})

describe('validate', () => {
  it('passes a valid single master config', () => {
    expect(validate([p({ mode: 'Master' })])).toHaveLength(0)
  })

  it('passes a valid single slave config', () => {
    expect(validate([p({ mode: 'Slave' })])).toHaveLength(0)
  })

  it('errors on an http:// embeddedStyx server', () => {
    const errors = validate([p({ networkType: 'embeddedStyx', embeddedStyx: { server: 'HTTP://10.0.0.1:5000', password: 'x' } })])
    expect(errors.some(e => e.path === 'profiles[0].embeddedStyx.server')).toBe(true)
  })

  it('passes an https:// or scheme-less embeddedStyx server', () => {
    expect(validate([p({ networkType: 'embeddedStyx', embeddedStyx: { server: 'https://10.0.0.1:5000', password: 'x' } })])).toHaveLength(0)
    expect(validate([p({ networkType: 'embeddedStyx', embeddedStyx: { server: '10.0.0.1:5000', password: 'x' } })])).toHaveLength(0)
  })

  it('errors when profileName is empty', () => {
    const errors = validate([p({ profileName: '' })])
    expect(errors.some(e => e.message.includes('profile name is required'))).toBe(true)
  })

  it('errors on duplicate profile names', () => {
    const profiles: HydraProfile[] = [
      p({ profileName: 'Home', conditions: { ssid: 'HomeWifi' } }),
      p({ profileName: 'home', conditions: { ssid: 'OfficeWifi' } }),
    ]
    const errors = validate(profiles)
    expect(errors.some(e => e.message.includes('duplicate profile name'))).toBe(true)
  })

  it('errors when multiple unconditional profiles', () => {
    const profiles: HydraProfile[] = [p({ profileName: 'A' }), p({ profileName: 'B', mode: 'Slave' })]
    const errors = validate(profiles)
    expect(errors.some(e => e.message.includes('unconditional'))).toBe(true)
  })

  it('passes when only one unconditional profile', () => {
    const profiles: HydraProfile[] = [
      p({ profileName: 'A', conditions: { ssid: 'home' } }),
      p({ profileName: 'B', mode: 'Slave' }),
    ]
    expect(validate(profiles)).toHaveLength(0)
  })

  it('errors on duplicate condition tuples', () => {
    const profiles: HydraProfile[] = [
      p({ profileName: 'A', conditions: { ssid: 'home' } }),
      p({ profileName: 'B', mode: 'Slave', conditions: { ssid: 'home' } }),
    ]
    const errors = validate(profiles)
    expect(errors.some(e => e.message.includes('duplicate condition'))).toBe(true)
  })

  it('passes conditions with same ssid but different screenCount', () => {
    const profiles: HydraProfile[] = [
      p({ profileName: 'A', conditions: { ssid: 'home', screenCount: 2 } }),
      p({ profileName: 'B', mode: 'Slave', conditions: { ssid: 'home', screenCount: 1 } }),
    ]
    expect(validate(profiles)).toHaveLength(0)
  })

  it('errors on duplicate isPluggedIn conditions', () => {
    const profiles: HydraProfile[] = [
      p({ profileName: 'A', conditions: { isPluggedIn: true } }),
      p({ profileName: 'B', mode: 'Slave', conditions: { isPluggedIn: true } }),
    ]
    const errors = validate(profiles)
    expect(errors.some(e => e.message.includes('duplicate condition'))).toBe(true)
  })

  it('passes conditions with same ssid but different isPluggedIn', () => {
    const profiles: HydraProfile[] = [
      p({ profileName: 'A', conditions: { ssid: 'home', isPluggedIn: true } }),
      p({ profileName: 'B', mode: 'Slave', conditions: { ssid: 'home', isPluggedIn: false } }),
    ]
    expect(validate(profiles)).toHaveLength(0)
  })

  it('treats isPluggedIn-only condition as conditional (not fallback)', () => {
    const profiles: HydraProfile[] = [
      p({ profileName: 'A', conditions: { isPluggedIn: true } }),
      p({ profileName: 'B', mode: 'Slave' }),
    ]
    expect(validate(profiles)).toHaveLength(0)
  })

  it('errors when screenCount < 1', () => {
    const errors = validate([p({ conditions: { screenCount: 0 } })])
    expect(errors.some(e => e.message.includes('screenCount'))).toBe(true)
  })

  it('passes when screenCount >= 1', () => {
    expect(validate([p({ conditions: { screenCount: 1 } })])).toHaveLength(0)
  })

  it('errors when screenDefinition has no match criteria', () => {
    const errors = validate([p({ mode: 'Slave', screenDefinitions: [{ mouseScale: 1.5 }] })])
    expect(errors.some(e => e.message.includes('displayName'))).toBe(true)
  })

  it('passes screenDefinition with displayName only', () => {
    expect(validate([p({ mode: 'Slave', screenDefinitions: [{ displayName: 'DELL' }] })])).toHaveLength(0)
  })

  it('errors when remoteOnly used with Slave mode', () => {
    const errors = validate([p({ mode: 'Slave', remoteOnly: true })])
    expect(errors.some(e => e.message.includes('Master'))).toBe(true)
  })

  it('errors when syncScreensaver is set on Slave mode', () => {
    const errors = validate([p({ mode: 'Slave', syncScreensaver: false })])
    expect(errors.some(e => e.message.includes('syncScreensaver'))).toBe(true)
  })

  it('passes when syncScreensaver is set on Master mode', () => {
    expect(validate([p({ mode: 'Master', syncScreensaver: false })])).toHaveLength(0)
  })

  it('errors when remoteOnly with no hosts', () => {
    const errors = validate([p({ remoteOnly: true, hosts: [] })])
    expect(errors.some(e => e.message.includes('remote host'))).toBe(true)
  })

  it('passes when remoteOnly with a remote host', () => {
    expect(validate([p({ remoteOnly: true, hosts: [{ name: 'pc' }] })])).toHaveLength(0)
  })

  it('errors when a host name contains a colon', () => {
    const errors = validate([p({ hosts: [{ name: 'a' }, { name: 'a:1' }] })])
    expect(errors).toEqual([{ path: 'profiles[0].hosts[1].name', message: "host name 'a:1' cannot contain ':' (reserved for monitor numbers)" }])
  })

  it('errors when a mirrored neighbour would create a host whose name contains a colon', () => {
    const errors = validate([p({ hosts: [{ name: 'a', neighbours: [{ direction: 'Right', name: 'a:1' }] }] })])
    expect(errors).toEqual([{ path: 'profiles[0].hosts[0].neighbours[0].name', message: "host name 'a:1' cannot contain ':' (reserved for monitor numbers)" }])
  })

  it('errors when the root name contains a colon', () => {
    expect(validate([p({})], 'a:1')).toEqual([{ path: 'name', message: "host name 'a:1' cannot contain ':' (reserved for monitor numbers)" }])
  })

  it('passes a root name without a colon', () => {
    expect(validate([p({})], 'laptop')).toHaveLength(0)
  })

  it('passes monitor names in sourceScreen and destScreen', () => {
    const hosts = [{ name: 'laptop', neighbours: [{ direction: 'Up' as const, name: 'desk', sourceScreen: 'laptop:1', destScreen: 'desk:0' }] }, { name: 'desk' }]
    expect(validate([p({ hosts })])).toHaveLength(0)
  })

  describe('maxMouseHz', () => {
    const master = (maxMouseHz: number) => ({ profileName: 'a', mode: 'Master' as const, maxMouseHz })

    it('accepts 1 to 1000', () => {
      expect(validate([master(1)]).filter(e => e.path.endsWith('maxMouseHz'))).toEqual([])
      expect(validate([master(1000)]).filter(e => e.path.endsWith('maxMouseHz'))).toEqual([])
    })

    it('refuses 0, more than 1000, and fractions', () => {
      for (const hz of [0, 1001, 12.5]) {
        expect(validate([master(hz)]).some(e => e.path === 'profiles[0].maxMouseHz')).toBe(true)
      }
    })
  })
})
