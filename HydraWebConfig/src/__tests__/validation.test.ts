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

  it('errors on duplicate gateway MACs however they are spelled', () => {
    const profiles: HydraProfile[] = [
      p({ profileName: 'A', conditions: { gatewayMac: 'a4:91:b1:0c:22:7e' } }),
      p({ profileName: 'B', mode: 'Slave', conditions: { gatewayMac: 'A4-91-B1-C-22-7E' } }),
    ]
    expect(validate(profiles).some(e => e.message.includes('duplicate condition'))).toBe(true)
  })

  it('errors on duplicate addresses however they are spelled', () => {
    const profiles: HydraProfile[] = [
      p({ profileName: 'A', conditions: { address: '10.0.0.7' } }),
      p({ profileName: 'B', mode: 'Slave', conditions: { address: '10.0.0.7/32' } }),
    ]
    expect(validate(profiles).some(e => e.message.includes('duplicate condition'))).toBe(true)
  })

  it('passes wired and not wired as different conditions', () => {
    const profiles: HydraProfile[] = [
      p({ profileName: 'A', conditions: { wired: true } }),
      p({ profileName: 'B', mode: 'Slave', conditions: { wired: false } }),
    ]
    expect(validate(profiles)).toHaveLength(0)
  })

  it('treats a link-only condition as conditional (not fallback)', () => {
    const profiles: HydraProfile[] = [
      p({ profileName: 'A', conditions: { address: '10.0.0.0/8' } }),
      p({ profileName: 'B', mode: 'Slave' }),
    ]
    expect(validate(profiles)).toHaveLength(0)
  })

  it.each(['192.168.1.300', 'office', '10', '10.0.0.0/33', '10.0.0.0/8/8', '192.168.1.7/24'])('errors on address %s', address => {
    const errors = validate([p({ profileName: 'A', conditions: { address } })])
    expect(errors.some(e => e.path.endsWith('conditions.address'))).toBe(true)
  })

  it.each(['192.168.1.7', '192.168.1.0/24', '2001:db8::/32'])('passes address %s', address => {
    expect(validate([p({ profileName: 'A', conditions: { address } })])).toHaveLength(0)
  })

  it.each(['a4:91:b1:0c:22', 'a4:91:b1:0c:22:7g', 'a4:91:b1:0c:22:7e7'])('errors on gateway MAC %s', gatewayMac => {
    const errors = validate([p({ profileName: 'A', conditions: { gatewayMac } })])
    expect(errors.some(e => e.path.endsWith('conditions.gatewayMac'))).toBe(true)
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
  describe('log cap', () => {
    const logErrors = (log: Parameters<typeof validate>[2]) => validate([p({})], undefined, log).map(e => e.path)

    it('passes sizes Hydra reads, and the bounds', () => {
      for (const logMaxSize of ['10 MiB', '512 KB', '1048576', '1.5 GiB', '2048 B', 'none', 'None', '10 mb', '0', '0 B'])
        expect(logErrors({ logMaxSize })).toEqual([])
      expect(logErrors({ logHistory: 0, logTrimPercent: 1 })).toEqual([])
      expect(logErrors({ logHistory: 9, logTrimPercent: 100 })).toEqual([])
    })

    it('refuses what Hydra would refuse at startup', () => {
      for (const logMaxSize of ['10 b', '1.5', 'lots', '10 MiB extra'])
        expect(logErrors({ logMaxSize })).toEqual(['logMaxSize'])
      expect(logErrors({ logHistory: -1, logTrimPercent: 0 })).toEqual(['logHistory', 'logTrimPercent'])
      expect(logErrors({ logHistory: 1.5, logTrimPercent: 101 })).toEqual(['logHistory', 'logTrimPercent'])
    })
  })
})
