import { describe, it, expect } from 'vitest'
import { serialize } from '../utils/serializer'
import type { HydraConfig } from '../types'

describe('serialize', () => {
  it('serializes a minimal master config', () => {
    const cfg: HydraConfig = { mode: 'Master' }
    const json = JSON.parse(serialize([cfg], false))
    expect(json.mode).toBe('Master')
    expect(json.logLevel).toBeUndefined()
    expect(json.autoUpdate).toBeUndefined()
    expect(json.syncScreensaver).toBeUndefined()
  })

  it('omits logLevel when info (default)', () => {
    const cfg: HydraConfig = { mode: 'Master', logLevel: 'info' }
    const json = JSON.parse(serialize([cfg], false))
    expect(json.logLevel).toBeUndefined()
  })

  it('includes logLevel when non-default', () => {
    const cfg: HydraConfig = { mode: 'Master', logLevel: 'debug' }
    const json = JSON.parse(serialize([cfg], false))
    expect(json.logLevel).toBe('debug')
  })

  it('omits autoUpdate when true (default)', () => {
    const cfg: HydraConfig = { mode: 'Master', autoUpdate: true }
    const json = JSON.parse(serialize([cfg], false))
    expect(json.autoUpdate).toBeUndefined()
  })

  it('includes autoUpdate when false', () => {
    const cfg: HydraConfig = { mode: 'Master', autoUpdate: false }
    const json = JSON.parse(serialize([cfg], false))
    expect(json.autoUpdate).toBe(false)
  })

  it('omits empty hosts array', () => {
    const cfg: HydraConfig = { mode: 'Master', hosts: [] }
    const json = JSON.parse(serialize([cfg], false))
    expect(json.hosts).toBeUndefined()
  })

  it('omits hosts when mode is Slave', () => {
    const cfg: HydraConfig = {
      mode: 'Slave',
      hosts: [{ name: 'pc', neighbours: [] }],
    }
    const json = JSON.parse(serialize([cfg], false))
    expect(json.hosts).toBeUndefined()
  })

  it('omits neighbour sourceStart when 0', () => {
    const cfg: HydraConfig = {
      mode: 'Master',
      hosts: [{ name: 'pc', neighbours: [{ direction: 'Right', name: 'mac', sourceStart: 0 }] }],
    }
    const json = JSON.parse(serialize([cfg], false))
    expect(json.hosts[0].neighbours[0].sourceStart).toBeUndefined()
  })

  it('includes neighbour sourceStart when non-zero', () => {
    const cfg: HydraConfig = {
      mode: 'Master',
      hosts: [{ name: 'pc', neighbours: [{ direction: 'Right', name: 'mac', sourceStart: 25 }] }],
    }
    const json = JSON.parse(serialize([cfg], false))
    expect(json.hosts[0].neighbours[0].sourceStart).toBe(25)
  })

  it('omits mirror when true (default)', () => {
    const cfg: HydraConfig = {
      mode: 'Master',
      hosts: [{ name: 'pc', neighbours: [{ direction: 'Right', name: 'mac', mirror: true }] }],
    }
    const json = JSON.parse(serialize([cfg], false))
    expect(json.hosts[0].neighbours[0].mirror).toBeUndefined()
  })

  it('includes mirror: false', () => {
    const cfg: HydraConfig = {
      mode: 'Master',
      hosts: [{ name: 'pc', neighbours: [{ direction: 'Right', name: 'mac', mirror: false }] }],
    }
    const json = JSON.parse(serialize([cfg], false))
    expect(json.hosts[0].neighbours[0].mirror).toBe(false)
  })

  it('serializes multi-config as array', () => {
    const configs: HydraConfig[] = [
      { mode: 'Master', conditions: { ssid: 'home' } },
      { mode: 'Slave' },
    ]
    const result = JSON.parse(serialize(configs, true))
    expect(Array.isArray(result)).toBe(true)
    expect(result).toHaveLength(2)
  })

  it('omits hosts with blank names', () => {
    const cfg: HydraConfig = {
      mode: 'Master',
      hosts: [{ name: '' }, { name: 'pc' }],
    }
    const json = JSON.parse(serialize([cfg], false))
    expect(json.hosts).toHaveLength(1)
    expect(json.hosts[0].name).toBe('pc')
  })

  it('omits screenDefinitions without match criteria', () => {
    const cfg: HydraConfig = {
      mode: 'Master',
      screenDefinitions: [{ mouseScale: 1.5 }, { displayName: 'DELL', mouseScale: 2 }],
    }
    const json = JSON.parse(serialize([cfg], false))
    expect(json.screenDefinitions).toHaveLength(1)
    expect(json.screenDefinitions[0].displayName).toBe('DELL')
  })
})
