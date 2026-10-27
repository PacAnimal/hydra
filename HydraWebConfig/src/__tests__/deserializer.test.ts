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

  it('parses root updateChannel in any case', () => {
    expect(deserialize(asFile('[{"mode":"Master"}]', '"updateChannel":"edge"')).updateChannel).toBe('edge')
    expect(deserialize(asFile('[{"mode":"Master"}]', '"updateChannel":"Edge"')).updateChannel).toBe('edge')
    expect(deserialize(asFile('[{"mode":"Master"}]', '"updateChannel":"STABLE"')).updateChannel).toBe('stable')
  })

  it('leaves updateChannel undefined when absent or unknown', () => {
    expect(deserialize(asFile('[{"mode":"Master"}]')).updateChannel).toBeUndefined()
    expect(deserialize(asFile('[{"mode":"Master"}]', '"updateChannel":"nightly"')).updateChannel).toBeUndefined()
  })

  it('parses root autoUpdate: false', () => {
    const state = deserialize(asFile('[{"mode":"Master"}]', '"autoUpdate":false'))
    expect(state.autoUpdate).toBe(false)
  })

  it('leaves autoUpdate undefined when absent', () => {
    const state = deserialize(asFile('[{"mode":"Master"}]'))
    expect(state.autoUpdate).toBeUndefined()
  })

  it('parses root managementListener: false', () => {
    const state = deserialize(asFile('[{"mode":"Master"}]', '"managementListener":false'))
    expect(state.managementListener).toBe(false)
  })

  it('leaves managementListener undefined when absent', () => {
    const state = deserialize(asFile('[{"mode":"Master"}]'))
    expect(state.managementListener).toBeUndefined()
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

  it('parses isPluggedIn: true', () => {
    const json = asFile(JSON.stringify([{ mode: 'Master', conditions: { isPluggedIn: true } }]))
    const state = deserialize(json)
    expect(state.profiles[0].conditions?.isPluggedIn).toBe(true)
  })

  it('parses isPluggedIn: false', () => {
    const json = asFile(JSON.stringify([{ mode: 'Master', conditions: { isPluggedIn: false } }]))
    const state = deserialize(json)
    expect(state.profiles[0].conditions?.isPluggedIn).toBe(false)
  })

  it('leaves isPluggedIn undefined when absent', () => {
    const json = asFile(JSON.stringify([{ mode: 'Master', conditions: { ssid: 'home' } }]))
    const state = deserialize(json)
    expect(state.profiles[0].conditions?.isPluggedIn).toBeUndefined()
  })

  it('preserves undefined for omitted optional profile fields', () => {
    const state = deserialize(asFile('[{"mode":"Slave"}]'))
    expect(state.name).toBeUndefined()
    expect(state.profiles[0].hosts).toBeUndefined()
  })

  it('parses embeddedStyx', () => {
    const json = asFile(JSON.stringify([{
      mode: 'Master',
      embeddedStyx: { server: 'styx.example.com', password: 'secret' },
    }]))
    const state = deserialize(json)
    expect(state.profiles[0].embeddedStyx?.server).toBe('styx.example.com')
    expect(state.profiles[0].embeddedStyx?.password).toBe('secret')
    expect(state.profiles[0].networkType).toBe('embeddedStyx')
  })

  it('parses embeddedStyxServer', () => {
    const json = asFile(JSON.stringify([{
      mode: 'Master',
      embeddedStyxServer: { port: 5000, password: 'pass' },
    }]))
    const state = deserialize(json)
    expect(state.profiles[0].embeddedStyxServer?.port).toBe(5000)
    expect(state.profiles[0].networkType).toBe('embeddedStyxServer')
  })

  it('parses networkConfig and sets networkType to config', () => {
    const json = asFile(JSON.stringify([{ mode: 'Master', networkConfig: 'base64data' }]))
    const state = deserialize(json)
    expect(state.profiles[0].networkConfig).toBe('base64data')
    expect(state.profiles[0].networkType).toBe('config')
  })

  it('parses relativeMouseScale on profile', () => {
    const json = asFile(JSON.stringify([{ mode: 'Slave', relativeMouseScale: 1.5 }]))
    const state = deserialize(json)
    expect(state.profiles[0].relativeMouseScale).toBe(1.5)
  })

  it('parses per-screen relativeMouseScale', () => {
    const json = asFile(JSON.stringify([{
      mode: 'Slave',
      screenDefinitions: [{ displayName: 'DELL', relativeMouseScale: 2.0 }],
    }]))
    const state = deserialize(json)
    expect(state.profiles[0].screenDefinitions![0].relativeMouseScale).toBe(2)
  })

  it('parses root logFile and sessionLogFile', () => {
    const json = asFile('[{"mode":"Master"}]', '"logFile":"/var/log/h.log","sessionLogFile":"/tmp/s.log"')
    const state = deserialize(json)
    expect(state.logFile).toBe('/var/log/h.log')
    expect(state.sessionLogFile).toBe('/tmp/s.log')
  })

  it('parses the root log cap, a null size as none', () => {
    const set = deserialize(asFile('[{"mode":"Master"}]', '"logMaxSize":"5 MiB","logHistory":0,"logTrimPercent":"25"'))
    const bytes = deserialize(asFile('[{"mode":"Master"}]', '"logMaxSize":1048576'))
    const none = deserialize(asFile('[{"mode":"Master"}]', '"logMaxSize":null'))
    expect([set.logMaxSize, set.logHistory, set.logTrimPercent]).toEqual(['5 MiB', 0, 25])
    expect([bytes.logMaxSize, none.logMaxSize]).toEqual(['1048576', 'none'])
  })

  it('infers layoutItems from hosts on import', () => {
    const json = asFile(JSON.stringify([{
      mode: 'Master',
      networkConfig: 'abc',
      hosts: [
        { name: 'desktop', neighbours: [{ direction: 'Right', name: 'laptop' }] },
        { name: 'laptop', neighbours: [] },
      ],
    }]))
    const state = deserialize(json)
    const items = state.profiles[0].layoutItems ?? []
    expect(items.length).toBeGreaterThanOrEqual(2)
    const hostNames = items.map(i => i.hostName)
    expect(hostNames).toContain('desktop')
    expect(hostNames).toContain('laptop')
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

  describe('modifierNumLock', () => {
    it('reads true and false', () => {
      expect(deserialize(asFile('[{"mode":"Master","modifierNumLock":true}]')).profiles[0].modifierNumLock).toBe(true)
      expect(deserialize(asFile('[{"mode":"Master","modifierNumLock":false}]')).profiles[0].modifierNumLock).toBe(false)
    })

    it('reads null, like a missing key, as unset', () => {
      expect(deserialize(asFile('[{"mode":"Master","modifierNumLock":null}]')).profiles[0].modifierNumLock).toBeUndefined()
      expect(deserialize(asFile('[{"mode":"Master"}]')).profiles[0].modifierNumLock).toBeUndefined()
    })
  })

  describe('the other profile settings Hydra has', () => {
    it('reads them back, so loading and saving a file keeps them', () => {
      const p = deserialize(asFile(
        '[{"mode":"Master","accelerateMouseWheel":false,"allowSystemSleep":true,"clipboardSync":"system","hideCursor":true,"maxMouseHz":250}]',
      )).profiles[0]
      expect([p.accelerateMouseWheel, p.allowSystemSleep, p.clipboardSync, p.hideCursor, p.maxMouseHz]).toEqual([false, true, 'System', true, 250])
    })

    it('reads clipboardSync by number and maxMouseHz from a string, as Hydra does', () => {
      const p = deserialize(asFile('[{"mode":"Master","clipboardSync":1,"maxMouseHz":"300"}]')).profiles[0]
      expect([p.clipboardSync, p.maxMouseHz]).toEqual(['System', 300])
      expect(deserialize(asFile('[{"mode":"Slave","clipboardSync":2}]')).profiles[0].clipboardSync).toEqual('Off')
      expect(deserialize(asFile('[{"mode":"Slave","clipboardSync":"OFF"}]')).profiles[0].clipboardSync).toEqual('Off')
      expect(deserialize(asFile('[{"mode":"Slave","clipboardSync":3}]')).profiles[0].clipboardSync).toBeUndefined()
      expect(deserialize(asFile('[{"mode":"Master","clipboardSync":"1"}]')).profiles[0].clipboardSync).toBe('System')
    })

    it('does not read a number Hydra would not', () => {
      expect(deserialize(asFile('[{"mode":"Master","maxMouseHz":"0x10"}]')).profiles[0].maxMouseHz).toBeUndefined()
    })

    it('reads an empty list as none, so saving keeps the action off', () => {
      const p = deserialize(asFile('[{"mode":"Master","hotkeys":{"switchMonitorLeft":[]}}]')).profiles[0]
      expect(p.hotkeys).toEqual({ switchMonitorLeft: 'none' })
    })

    it('reads hotkeys as each action\'s chords, one per line', () => {
      const p = deserialize(asFile('[{"mode":"Master","hotkeys":{"toggleCursorLock":["Ctrl++","ScrollLock"]}}]')).profiles[0]
      expect(p.hotkeys).toEqual({ toggleCursorLock: 'Ctrl++\nScrollLock' })
    })

    it('files a hotkey action spelled in another case under its own name, the first of two winning, as in Hydra', () => {
      const p = deserialize(asFile('[{"mode":"Master","hotkeys":{"ToggleCursorLock":["F13"],"togglecursorlock":["F14"]}}]')).profiles[0]
      expect(p.hotkeys).toEqual({ toggleCursorLock: 'F13' })
    })
  })
})
