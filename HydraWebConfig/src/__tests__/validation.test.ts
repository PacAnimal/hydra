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

  it('errors when remoteOnly with no hosts', () => {
    const errors = validate([p({ remoteOnly: true, hosts: [] })])
    expect(errors.some(e => e.message.includes('remote host'))).toBe(true)
  })

  it('passes when remoteOnly with a remote host', () => {
    expect(validate([p({ remoteOnly: true, hosts: [{ name: 'pc' }] })])).toHaveLength(0)
  })
})
