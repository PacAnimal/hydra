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

  it('omits updateChannel when stable (default)', () => {
    expect(JSON.parse(serialize(state({ mode: 'Master' }))).updateChannel).toBeUndefined()
    expect(JSON.parse(serialize(state({ mode: 'Master' }, { updateChannel: 'stable' }))).updateChannel).toBeUndefined()
  })

  it('includes updateChannel when edge', () => {
    const json = JSON.parse(serialize(state({ mode: 'Master' }, { updateChannel: 'edge' })))
    expect(json.updateChannel).toBe('edge')
  })

  it('omits autoUpdate when true (default)', () => {
    const json = JSON.parse(serialize(state({ mode: 'Master' }, { autoUpdate: true })))
    expect(json.autoUpdate).toBeUndefined()
  })

  it('includes autoUpdate when false', () => {
    const json = JSON.parse(serialize(state({ mode: 'Master' }, { autoUpdate: false })))
    expect(json.autoUpdate).toBe(false)
  })

  it('omits managementListener when true (default)', () => {
    const json = JSON.parse(serialize(state({ mode: 'Master' }, { managementListener: true })))
    expect(json.managementListener).toBeUndefined()
  })

  it('includes managementListener when false', () => {
    const json = JSON.parse(serialize(state({ mode: 'Master' }, { managementListener: false })))
    expect(json.managementListener).toBe(false)
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

  it('serializes isPluggedIn: true', () => {
    const json = JSON.parse(serialize(state({ conditions: { isPluggedIn: true } })))
    expect(json.profiles[0].conditions.isPluggedIn).toBe(true)
  })

  it('serializes isPluggedIn: false', () => {
    const json = JSON.parse(serialize(state({ conditions: { isPluggedIn: false } })))
    expect(json.profiles[0].conditions.isPluggedIn).toBe(false)
  })

  it('omits isPluggedIn when undefined', () => {
    const json = JSON.parse(serialize(state({ conditions: { ssid: 'home' } })))
    expect(json.profiles[0].conditions.isPluggedIn).toBeUndefined()
  })

  it('serializes embeddedStyx when networkType is embeddedStyx', () => {
    const json = JSON.parse(serialize(state({
      mode: 'Master',
      networkType: 'embeddedStyx',
      embeddedStyx: { server: 'styx.example.com', password: 'secret' },
    })))
    expect(json.profiles[0].embeddedStyx).toEqual({ server: 'styx.example.com', password: 'secret' })
    expect(json.profiles[0].networkConfig).toBeUndefined()
  })

  it('serializes embeddedStyxServer when networkType is embeddedStyxServer', () => {
    const json = JSON.parse(serialize(state({
      mode: 'Master',
      networkType: 'embeddedStyxServer',
      embeddedStyxServer: { port: 5000, password: 'pass' },
    })))
    expect(json.profiles[0].embeddedStyxServer).toEqual({ port: 5000, password: 'pass' })
    expect(json.profiles[0].embeddedStyx).toBeUndefined()
  })

  it('serializes relativeMouseScale for slave profiles', () => {
    const json = JSON.parse(serialize(state({ mode: 'Slave', relativeMouseScale: 1.5 })))
    expect(json.profiles[0].relativeMouseScale).toBe(1.5)
  })

  it('omits relativeMouseScale for master profiles', () => {
    const json = JSON.parse(serialize(state({ mode: 'Master', relativeMouseScale: 1.5 })))
    expect(json.profiles[0].relativeMouseScale).toBeUndefined()
  })

  it('serializes logFile and sessionLogFile when set', () => {
    const json = JSON.parse(serialize(state({ mode: 'Master' }, { logFile: '/var/log/hydra.log', sessionLogFile: '/tmp/session.log' })))
    expect(json.logFile).toBe('/var/log/hydra.log')
    expect(json.sessionLogFile).toBe('/tmp/session.log')
  })

  it('omits logFile and sessionLogFile when not set', () => {
    const json = JSON.parse(serialize(state({ mode: 'Master' })))
    expect(json.logFile).toBeUndefined()
    expect(json.sessionLogFile).toBeUndefined()
  })

  it("writes the log cap only where it differs from Hydra's defaults", () => {
    const set = JSON.parse(serialize(state({ mode: 'Slave' }, { logMaxSize: ' 5 MiB ', logHistory: 0, logTrimPercent: 25 })))
    const bytes = JSON.parse(serialize(state({ mode: 'Slave' }, { logMaxSize: '1048576' })))
    const none = JSON.parse(serialize(state({ mode: 'Slave' }, { logMaxSize: 'None' })))
    const defaults = JSON.parse(serialize(state({ mode: 'Slave' }, { logHistory: 1, logTrimPercent: 50 })))
    expect([set.logMaxSize, set.logHistory, set.logTrimPercent]).toEqual(['5 MiB', 0, 25])
    expect(bytes.logMaxSize).toBe(1048576)
    expect('logMaxSize' in none && none.logMaxSize === null).toBe(true)
    expect(['logMaxSize' in defaults, 'logHistory' in defaults, 'logTrimPercent' in defaults]).toEqual([false, false, false])
  })

  it('derives hosts from layoutItems when layoutItems is non-empty', () => {
    const json = JSON.parse(serialize(state({
      mode: 'Master',
      layoutItems: [
        { id: '1', hostName: 'desktop', x: 0, y: 0, w: 1920, h: 1080 },
        { id: '2', hostName: 'laptop', x: 1920, y: 0, w: 1920, h: 1080 },
      ],
    })))
    const hosts = json.profiles[0].hosts
    expect(hosts).toBeDefined()
    const desktop = hosts.find((h: { name: string }) => h.name === 'desktop')
    expect(desktop.neighbours[0].direction).toBe('Right')
    expect(desktop.neighbours[0].name).toBe('laptop')
  })

  it('uses explicit hosts when layoutItems is empty', () => {
    const json = JSON.parse(serialize(state({
      mode: 'Master',
      layoutItems: [],
      hosts: [{ name: 'pc', neighbours: [{ direction: 'Right', name: 'mac' }] }],
    })))
    expect(json.profiles[0].hosts[0].name).toBe('pc')
  })

  it('serializes per-screen relativeMouseScale', () => {
    const json = JSON.parse(serialize(state({
      mode: 'Slave',
      screenDefinitions: [{ displayName: 'DELL', relativeMouseScale: 2.0 }],
    })))
    expect(json.profiles[0].screenDefinitions[0].relativeMouseScale).toBe(2)
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

  it('omits neighbour sourceEnd when 100 (default)', () => {
    const json = JSON.parse(serialize(state({
      mode: 'Master',
      hosts: [{ name: 'pc', neighbours: [{ direction: 'Right', name: 'mac', sourceEnd: 100 }] }],
    })))
    expect(json.profiles[0].hosts[0].neighbours[0].sourceEnd).toBeUndefined()
  })

  it('includes neighbour sourceEnd when non-100', () => {
    const json = JSON.parse(serialize(state({
      mode: 'Master',
      hosts: [{ name: 'pc', neighbours: [{ direction: 'Right', name: 'mac', sourceEnd: 75 }] }],
    })))
    expect(json.profiles[0].hosts[0].neighbours[0].sourceEnd).toBe(75)
  })

  it('omits neighbour destStart when 0 (default)', () => {
    const json = JSON.parse(serialize(state({
      mode: 'Master',
      hosts: [{ name: 'pc', neighbours: [{ direction: 'Right', name: 'mac', destStart: 0 }] }],
    })))
    expect(json.profiles[0].hosts[0].neighbours[0].destStart).toBeUndefined()
  })

  it('omits neighbour destEnd when 100 (default)', () => {
    const json = JSON.parse(serialize(state({
      mode: 'Master',
      hosts: [{ name: 'pc', neighbours: [{ direction: 'Right', name: 'mac', destEnd: 100 }] }],
    })))
    expect(json.profiles[0].hosts[0].neighbours[0].destEnd).toBeUndefined()
  })

  it('includes neighbour destEnd when non-100', () => {
    const json = JSON.parse(serialize(state({
      mode: 'Master',
      hosts: [{ name: 'pc', neighbours: [{ direction: 'Right', name: 'mac', destEnd: 80 }] }],
    })))
    expect(json.profiles[0].hosts[0].neighbours[0].destEnd).toBe(80)
  })

  it('omits neighbours with blank names', () => {
    const json = JSON.parse(serialize(state({
      mode: 'Master',
      hosts: [{ name: 'pc', neighbours: [
        { direction: 'Right', name: '' },
        { direction: 'Left', name: 'mac' },
      ] }],
    })))
    expect(json.profiles[0].hosts[0].neighbours).toHaveLength(1)
    expect(json.profiles[0].hosts[0].neighbours[0].name).toBe('mac')
  })

  describe('modifierNumLock', () => {
    it('is left out when unset', () => {
      const json = JSON.parse(serialize(state({ mode: 'Master' })))
      expect('modifierNumLock' in json.profiles[0]).toBe(false)
    })

    it('writes true and false on a master', () => {
      expect(JSON.parse(serialize(state({ mode: 'Master', modifierNumLock: true }))).profiles[0].modifierNumLock).toBe(true)
      expect(JSON.parse(serialize(state({ mode: 'Master', modifierNumLock: false }))).profiles[0].modifierNumLock).toBe(false)
    })

    it('is never written on a slave, which Hydra would refuse', () => {
      const json = JSON.parse(serialize(state({ mode: 'Slave', modifierNumLock: true })))
      expect('modifierNumLock' in json.profiles[0]).toBe(false)
    })
  })

  describe('the other profile settings Hydra has', () => {
    it('writes hideCursor and maxMouseHz on a master only', () => {
      const master = JSON.parse(serialize(state({ mode: 'Master', hideCursor: true, maxMouseHz: 250 }))).profiles[0]
      const slave = JSON.parse(serialize(state({ mode: 'Slave', hideCursor: true, maxMouseHz: 250 }))).profiles[0]
      expect([master.hideCursor, master.maxMouseHz]).toEqual([true, 250])
      expect(['hideCursor' in slave, 'maxMouseHz' in slave]).toEqual([false, false])
    })

    it('writes allowSystemSleep and clipboardSync only when not the default', () => {
      const set = JSON.parse(serialize(state({ mode: 'Slave', allowSystemSleep: true, clipboardSync: 'System' }))).profiles[0]
      const unset = JSON.parse(serialize(state({ mode: 'Slave', allowSystemSleep: false, clipboardSync: 'Hydra' }))).profiles[0]
      expect([set.allowSystemSleep, set.clipboardSync]).toEqual([true, 'System'])
      expect(['allowSystemSleep' in unset, 'clipboardSync' in unset]).toEqual([false, false])
      const off = JSON.parse(serialize(state({ mode: 'Slave', clipboardSync: 'Off' }))).profiles[0]
      expect(off.clipboardSync).toEqual('Off')
    })

    it('writes each hotkey action typed, one chord per line, and leaves out the empty ones', () => {
      const json = JSON.parse(serialize(state({
        mode: 'Master',
        hotkeys: { toggleCursorLock: 'Ctrl+Alt+Super+L\nScrollLock', copyFiles: '  \n', pasteFiles: 'Ctrl+,\r\nCtrl++\nCtrl + ,' },
      })))
      expect(json.profiles[0].hotkeys).toEqual({
        toggleCursorLock: ['Ctrl+Alt+Super+L', 'ScrollLock'],
        pasteFiles: ['Ctrl+,', 'Ctrl++', 'Ctrl + ,'],
      })
    })

    it('writes none as an empty list, which turns the action off, in any case', () => {
      const json = JSON.parse(serialize(state({ mode: 'Master', hotkeys: { switchMonitorUp: ' None ', copyFiles: 'none\n' } })))
      expect(json.profiles[0].hotkeys).toEqual({ switchMonitorUp: [], copyFiles: [] })
    })

    it('leaves hotkeys out when every action is empty', () => {
      const json = JSON.parse(serialize(state({ mode: 'Master', hotkeys: { toggleCursorLock: '' } })))
      expect('hotkeys' in json.profiles[0]).toBe(false)
    })
  })
})
