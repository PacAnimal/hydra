import { describe, it, expect } from 'vitest'
import { renderHook, act } from '@testing-library/react'
import { useHydraConfig } from '../hooks/useHydraConfig'

describe('useHydraConfig', () => {
  describe('importJson', () => {
    it('updates state from valid json', () => {
      const { result } = renderHook(() => useHydraConfig())
      const json = JSON.stringify({
        profiles: [{ mode: 'Slave', hosts: [{ name: 'laptop', neighbours: [] }] }],
      })
      let err: string | null
      act(() => { err = result.current.importJson(json) })
      expect(err!).toBeNull()
      expect(result.current.current.mode).toBe('Slave')
    })

    it('returns error message and leaves state unchanged on invalid json', () => {
      const { result } = renderHook(() => useHydraConfig())
      const before = result.current.state
      let err: string | null
      act(() => { err = result.current.importJson('not json at all!!!') })
      expect(err!).toBeTruthy()
      expect(result.current.state).toBe(before)
    })

    it('returns error message and leaves state unchanged on invalid config schema', () => {
      const { result } = renderHook(() => useHydraConfig())
      const before = result.current.state
      let err: string | null
      // valid json, but invalid mode value
      act(() => { err = result.current.importJson(JSON.stringify({ profiles: [{ mode: 'Overlord' }] })) })
      expect(err!).toBeTruthy()
      expect(result.current.state).toBe(before)
    })
  })

  describe('toggleVisualMode', () => {
    it('derives hosts from layoutItems when switching to manual', () => {
      const { result } = renderHook(() => useHydraConfig())

      // set up two layout items side-by-side so deriveHostsFromLayout creates neighbours
      act(() => {
        result.current.updateLayoutItems([
          { id: 'a', hostName: 'alpha', x: 0, y: 0, w: 1920, h: 1080 },
          { id: 'b', hostName: 'beta', x: 1920, y: 0, w: 1920, h: 1080 },
        ])
      })
      // start in visual mode, toggle to manual
      act(() => { result.current.toggleVisualMode() })

      expect(result.current.current.visualMode).toBe(false)
      const hosts = result.current.current.hosts ?? []
      expect(hosts.map(h => h.name).sort()).toEqual(['alpha', 'beta'])
    })

    it('infers layout from hosts when switching back to visual', () => {
      const { result } = renderHook(() => useHydraConfig())

      // switch to manual first
      act(() => { result.current.toggleVisualMode() })
      // add a host manually
      act(() => { result.current.addHost() })
      act(() => { result.current.updateHost(0, { name: 'desktop' }) })

      // switch back to visual
      act(() => { result.current.toggleVisualMode() })

      expect(result.current.current.visualMode).toBe(true)
      const items = result.current.current.layoutItems ?? []
      expect(items.some(i => i.hostName === 'desktop')).toBe(true)
    })
  })

  describe('removeProfile', () => {
    it('is a no-op when only one profile exists', () => {
      const { result } = renderHook(() => useHydraConfig())
      expect(result.current.state.profiles).toHaveLength(1)
      act(() => { result.current.removeProfile(0) })
      expect(result.current.state.profiles).toHaveLength(1)
    })

    it('removes a middle profile and clamps activeIndex', () => {
      const { result } = renderHook(() => useHydraConfig())
      // add two more profiles (total: 3)
      act(() => { result.current.addProfile() })
      act(() => { result.current.addProfile() })
      // set active to last (index 2)
      act(() => { result.current.setActiveIndex(2) })

      // remove the middle profile (index 1)
      act(() => { result.current.removeProfile(1) })

      expect(result.current.state.profiles).toHaveLength(2)
      // activeIndex should be clamped to new length - 1 = 1
      expect(result.current.state.activeIndex).toBe(1)
    })
  })

  describe('addProfile / removeProfile', () => {
    it('addProfile appends and sets activeIndex to the new profile', () => {
      const { result } = renderHook(() => useHydraConfig())
      expect(result.current.state.profiles).toHaveLength(1)
      act(() => { result.current.addProfile() })
      expect(result.current.state.profiles).toHaveLength(2)
      expect(result.current.state.activeIndex).toBe(1)
    })

    it('removeProfile on active last entry moves activeIndex back', () => {
      const { result } = renderHook(() => useHydraConfig())
      act(() => { result.current.addProfile() })
      // active is now 1 (the new profile)
      act(() => { result.current.removeProfile(1) })
      expect(result.current.state.profiles).toHaveLength(1)
      expect(result.current.state.activeIndex).toBe(0)
    })
  })

  describe('undo / redo', () => {
    it('undoes the last change', () => {
      const { result } = renderHook(() => useHydraConfig())
      act(() => { result.current.addProfile() })
      expect(result.current.state.profiles).toHaveLength(2)
      act(() => { result.current.undo() })
      expect(result.current.state.profiles).toHaveLength(1)
    })

    it('redoes after undo', () => {
      const { result } = renderHook(() => useHydraConfig())
      act(() => { result.current.addProfile() })
      act(() => { result.current.undo() })
      act(() => { result.current.redo() })
      expect(result.current.state.profiles).toHaveLength(2)
    })

    it('clears redo stack on new change after undo', () => {
      const { result } = renderHook(() => useHydraConfig())
      act(() => { result.current.addProfile() })
      act(() => { result.current.undo() })
      expect(result.current.canRedo).toBe(true)
      act(() => { result.current.addProfile() })
      expect(result.current.canRedo).toBe(false)
    })

    it('setActiveIndex does not push to history', () => {
      const { result } = renderHook(() => useHydraConfig())
      act(() => { result.current.addProfile() })
      act(() => { result.current.undo() })
      // only the addProfile is in history; setActiveIndex shouldn't add more
      act(() => { result.current.setActiveIndex(0) })
      expect(result.current.canUndo).toBe(false)
    })

    it('import clears history', () => {
      const { result } = renderHook(() => useHydraConfig())
      act(() => { result.current.addProfile() })
      const json = JSON.stringify({ profiles: [{ mode: 'Slave' }] })
      act(() => { result.current.importJson(json) })
      expect(result.current.canUndo).toBe(false)
    })

    it('reset clears history', () => {
      const { result } = renderHook(() => useHydraConfig())
      act(() => { result.current.addProfile() })
      act(() => { result.current.reset() })
      expect(result.current.canUndo).toBe(false)
    })
  })

  describe('duplicateProfile', () => {
    it('inserts a copy after the source with (copy) suffix', () => {
      const { result } = renderHook(() => useHydraConfig())
      act(() => { result.current.updateCurrent({ profileName: 'Home' }) })
      act(() => { result.current.duplicateProfile(0) })
      expect(result.current.state.profiles).toHaveLength(2)
      expect(result.current.state.profiles[1].profileName).toBe('Home (copy)')
      expect(result.current.state.activeIndex).toBe(1)
    })
  })

  describe('moveProfile', () => {
    it('swaps a profile left', () => {
      const { result } = renderHook(() => useHydraConfig())
      act(() => { result.current.addProfile() })
      act(() => { result.current.updateCurrent({ profileName: 'B' }) })
      // profiles: [untitled, B], activeIndex: 1
      act(() => { result.current.moveProfile(1, 'left') })
      expect(result.current.state.profiles[0].profileName).toBe('B')
      expect(result.current.state.activeIndex).toBe(0)
    })

    it('swaps a profile right', () => {
      const { result } = renderHook(() => useHydraConfig())
      act(() => { result.current.updateCurrent({ profileName: 'A' }) })
      act(() => { result.current.addProfile() })
      act(() => { result.current.setActiveIndex(0) })
      act(() => { result.current.moveProfile(0, 'right') })
      expect(result.current.state.profiles[1].profileName).toBe('A')
      expect(result.current.state.activeIndex).toBe(1)
    })

    it('is a no-op when moving left from index 0', () => {
      const { result } = renderHook(() => useHydraConfig())
      const before = result.current.state
      act(() => { result.current.moveProfile(0, 'left') })
      expect(result.current.state).toBe(before)
    })
  })

  describe('addHost / removeHost', () => {
    it('addHost appends an empty host to the active profile', () => {
      const { result } = renderHook(() => useHydraConfig())
      act(() => { result.current.toggleVisualMode() }) // switch to manual
      act(() => { result.current.addHost() })
      expect(result.current.current.hosts).toHaveLength(1)
      expect(result.current.current.hosts![0].name).toBe('')
    })

    it('removeHost removes the host at the given index', () => {
      const { result } = renderHook(() => useHydraConfig())
      act(() => { result.current.toggleVisualMode() })
      act(() => { result.current.addHost() })
      act(() => { result.current.addHost() })
      act(() => { result.current.updateHost(0, { name: 'first' }) })
      act(() => { result.current.updateHost(1, { name: 'second' }) })

      act(() => { result.current.removeHost(0) })

      expect(result.current.current.hosts).toHaveLength(1)
      expect(result.current.current.hosts![0].name).toBe('second')
    })

    it('mutations on one profile do not affect another', () => {
      const { result } = renderHook(() => useHydraConfig())
      act(() => { result.current.addProfile() })
      // add a host on profile 1 (active)
      act(() => { result.current.toggleVisualMode() })
      act(() => { result.current.addHost() })
      // switch back to profile 0
      act(() => { result.current.setActiveIndex(0) })
      expect(result.current.current.hosts ?? []).toHaveLength(0)
    })
  })
})
