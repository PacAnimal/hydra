import { describe, it, expect } from 'vitest'
import { validate } from '../utils/validation'
import type { HydraConfig } from '../types'

describe('validate', () => {
  it('passes a valid single master config', () => {
    const cfg: HydraConfig = { mode: 'Master' }
    expect(validate([cfg], false)).toHaveLength(0)
  })

  it('passes a valid single slave config', () => {
    const cfg: HydraConfig = { mode: 'Slave' }
    expect(validate([cfg], false)).toHaveLength(0)
  })

  it('errors when multiple unconditional configs in multi mode', () => {
    const configs: HydraConfig[] = [{ mode: 'Master' }, { mode: 'Slave' }]
    const errors = validate(configs, true)
    expect(errors.some(e => e.message.includes('unconditional'))).toBe(true)
  })

  it('passes when only one unconditional config in multi mode', () => {
    const configs: HydraConfig[] = [
      { mode: 'Master', conditions: { ssid: 'home' } },
      { mode: 'Slave' },
    ]
    expect(validate(configs, true)).toHaveLength(0)
  })

  it('errors on duplicate condition tuples', () => {
    const configs: HydraConfig[] = [
      { mode: 'Master', conditions: { ssid: 'home' } },
      { mode: 'Slave', conditions: { ssid: 'home' } },
    ]
    const errors = validate(configs, true)
    expect(errors.some(e => e.message.includes('duplicate'))).toBe(true)
  })

  it('passes conditions with same ssid but different screenCount', () => {
    const configs: HydraConfig[] = [
      { mode: 'Master', conditions: { ssid: 'home', screenCount: 2 } },
      { mode: 'Slave', conditions: { ssid: 'home', screenCount: 1 } },
    ]
    expect(validate(configs, true)).toHaveLength(0)
  })

  it('errors when screenCount < 1', () => {
    const cfg: HydraConfig = { mode: 'Master', conditions: { screenCount: 0 } }
    const errors = validate([cfg], false)
    expect(errors.some(e => e.message.includes('screenCount'))).toBe(true)
  })

  it('passes when screenCount >= 1', () => {
    const cfg: HydraConfig = { mode: 'Master', conditions: { screenCount: 1 } }
    expect(validate([cfg], false)).toHaveLength(0)
  })

  it('errors when screenDefinition has no match criteria', () => {
    const cfg: HydraConfig = { mode: 'Master', screenDefinitions: [{ mouseScale: 1.5 }] }
    const errors = validate([cfg], false)
    expect(errors.some(e => e.message.includes('displayName'))).toBe(true)
  })

  it('passes screenDefinition with displayName only', () => {
    const cfg: HydraConfig = { mode: 'Master', screenDefinitions: [{ displayName: 'DELL' }] }
    expect(validate([cfg], false)).toHaveLength(0)
  })

  it('errors when remoteOnly used with Slave mode', () => {
    const cfg: HydraConfig = { mode: 'Slave', remoteOnly: true }
    const errors = validate([cfg], false)
    expect(errors.some(e => e.message.includes('Master'))).toBe(true)
  })

  it('errors when remoteOnly with no remote hosts', () => {
    const cfg: HydraConfig = { mode: 'Master', name: 'mac', remoteOnly: true, hosts: [{ name: 'mac' }] }
    const errors = validate([cfg], false)
    expect(errors.some(e => e.message.includes('remote host'))).toBe(true)
  })

  it('passes when remoteOnly with a remote host', () => {
    const cfg: HydraConfig = {
      mode: 'Master', name: 'mac', remoteOnly: true,
      hosts: [{ name: 'mac' }, { name: 'pc' }],
    }
    expect(validate([cfg], false)).toHaveLength(0)
  })
})
