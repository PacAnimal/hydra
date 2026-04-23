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
