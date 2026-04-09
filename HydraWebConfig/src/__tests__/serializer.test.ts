import { describe, it, expect } from 'vitest'
import { serialize } from '../utils/serializer'
import type { HydraProfile, FormState } from '../types'

function p(overrides: Partial<HydraProfile>): HydraProfile {
  return { profileName: '', mode: 'Master', ...overrides }
}

function state(profile: Partial<HydraProfile>, root: Partial<FormState> = {}): FormState {
  return { profiles: [p(profile)], activeIndex: 0, ...root }
}

function stateMany(profiles: Partial<HydraProfile>[], root: Partial<FormState> = {}): FormState {
  return { profiles: profiles.map(p), activeIndex: 0, ...root }
}

describe('serialize', () => {
  it('serializes a minimal master config', () => {
    const json = JSON.parse(serialize(state({ mode: 'Master' })))
    expect(json.profiles[0].mode).toBe('Master')
    expect(json.logLevel).toBeUndefined()
    expect(json.autoUpdate).toBeUndefined()
    expect(json.profiles[0].syncScreensaver).toBeUndefined()
  })

  it('omits logLevel when info (default)', () => {
    const json = JSON.parse(serialize(state({ mode: 'Master' }, { logLevel: 'info' })))
    expect(json.logLevel).toBeUndefined()
  })

  it('includes logLevel when non-default', () => {
    const json = JSON.parse(serialize(state({ mode: 'Master' }, { logLevel: 'debug' })))
    expect(json.logLevel).toBe('debug')
  })

  it('omits autoUpdate when true (default)', () => {
    const json = JSON.parse(serialize(state({ mode: 'Master' }, { autoUpdate: true })))
    expect(json.autoUpdate).toBeUndefined()
  })

  it('includes autoUpdate when false', () => {
    const json = JSON.parse(serialize(state({ mode: 'Master' }, { autoUpdate: false })))
    expect(json.autoUpdate).toBe(false)
  })

  it('omits empty hosts array', () => {
    const json = JSON.parse(serialize(state({ mode: 'Master', hosts: [] })))
    expect(json.profiles[0].hosts).toBeUndefined()
  })

  it('omits hosts when mode is Slave', () => {
    const json = JSON.parse(serialize(state({
      mode: 'Slave',
      hosts: [{ name: 'pc', neighbours: [] }],
    })))
    expect(json.profiles[0].hosts).toBeUndefined()
  })

  it('omits neighbour sourceStart when 0', () => {
    const json = JSON.parse(serialize(state({
      mode: 'Master',
      hosts: [{ name: 'pc', neighbours: [{ direction: 'Right', name: 'mac', sourceStart: 0 }] }],
    })))
    expect(json.profiles[0].hosts[0].neighbours[0].sourceStart).toBeUndefined()
  })

  it('includes neighbour sourceStart when non-zero', () => {
    const json = JSON.parse(serialize(state({
      mode: 'Master',
      hosts: [{ name: 'pc', neighbours: [{ direction: 'Right', name: 'mac', sourceStart: 25 }] }],
    })))
    expect(json.profiles[0].hosts[0].neighbours[0].sourceStart).toBe(25)
  })

  it('omits mirror when true (default)', () => {
    const json = JSON.parse(serialize(state({
      mode: 'Master',
      hosts: [{ name: 'pc', neighbours: [{ direction: 'Right', name: 'mac', mirror: true }] }],
    })))
    expect(json.profiles[0].hosts[0].neighbours[0].mirror).toBeUndefined()
  })

  it('includes mirror: false', () => {
    const json = JSON.parse(serialize(state({
      mode: 'Master',
      hosts: [{ name: 'pc', neighbours: [{ direction: 'Right', name: 'mac', mirror: false }] }],
    })))
    expect(json.profiles[0].hosts[0].neighbours[0].mirror).toBe(false)
  })

  it('serializes multiple profiles', () => {
    const result = JSON.parse(serialize(stateMany([
      { mode: 'Master', conditions: { ssid: 'home' } },
      { mode: 'Slave' },
    ])))
    expect(Array.isArray(result.profiles)).toBe(true)
    expect(result.profiles).toHaveLength(2)
  })

  it('omits hosts with blank names', () => {
    const json = JSON.parse(serialize(state({
      mode: 'Master',
      hosts: [{ name: '' }, { name: 'pc' }],
    })))
    expect(json.profiles[0].hosts).toHaveLength(1)
    expect(json.profiles[0].hosts[0].name).toBe('pc')
  })

  it('omits screenDefinitions without match criteria', () => {
    const json = JSON.parse(serialize(state({
      mode: 'Slave',
      screenDefinitions: [{ mouseScale: 1.5 }, { displayName: 'DELL', mouseScale: 2 }],
    })))
    expect(json.profiles[0].screenDefinitions).toHaveLength(1)
    expect(json.profiles[0].screenDefinitions[0].displayName).toBe('DELL')
  })
})
