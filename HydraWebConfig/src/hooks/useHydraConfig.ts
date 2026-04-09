import { useState, useCallback } from 'react'
import type { FormState, HydraProfile, HostConfig, NeighbourConfig, ScreenDefinition, ConfigConditions } from '../types'
import { newFormState, newHost, newNeighbour, newScreenDefinition, newProfile } from '../defaults'
import { deserialize } from '../utils/deserializer'

export function useHydraConfig() {
  const [state, setState] = useState<FormState>(newFormState)

  const current = state.profiles[state.activeIndex] ?? state.profiles[0]

  const updateRoot = useCallback((patch: Partial<Pick<FormState, 'name' | 'autoUpdate' | 'logLevel' | 'lockFile'>>) => {
    setState(s => ({ ...s, ...patch }))
  }, [])

  const updateCurrent = useCallback((patch: Partial<HydraProfile>) => {
    setState(s => {
      const profiles = [...s.profiles]
      profiles[s.activeIndex] = { ...profiles[s.activeIndex], ...patch }
      return { ...s, profiles }
    })
  }, [])

  // hosts
  const addHost = useCallback(() => {
    setState(s => {
      const profiles = [...s.profiles]
      const p = profiles[s.activeIndex]
      profiles[s.activeIndex] = { ...p, hosts: [...(p.hosts ?? []), newHost()] }
      return { ...s, profiles }
    })
  }, [])

  const removeHost = useCallback((hi: number) => {
    setState(s => {
      const profiles = [...s.profiles]
      const p = profiles[s.activeIndex]
      const hosts = (p.hosts ?? []).filter((_, i) => i !== hi)
      profiles[s.activeIndex] = { ...p, hosts }
      return { ...s, profiles }
    })
  }, [])

  const updateHost = useCallback((hi: number, patch: Partial<HostConfig>) => {
    setState(s => {
      const profiles = [...s.profiles]
      const p = profiles[s.activeIndex]
      const hosts = [...(p.hosts ?? [])]
      hosts[hi] = { ...hosts[hi], ...patch }
      profiles[s.activeIndex] = { ...p, hosts }
      return { ...s, profiles }
    })
  }, [])

  // neighbours
  const addNeighbour = useCallback((hi: number) => {
    setState(s => {
      const profiles = [...s.profiles]
      const p = profiles[s.activeIndex]
      const hosts = [...(p.hosts ?? [])]
      hosts[hi] = { ...hosts[hi], neighbours: [...(hosts[hi].neighbours ?? []), newNeighbour()] }
      profiles[s.activeIndex] = { ...p, hosts }
      return { ...s, profiles }
    })
  }, [])

  const removeNeighbour = useCallback((hi: number, ni: number) => {
    setState(s => {
      const profiles = [...s.profiles]
      const p = profiles[s.activeIndex]
      const hosts = [...(p.hosts ?? [])]
      hosts[hi] = { ...hosts[hi], neighbours: (hosts[hi].neighbours ?? []).filter((_, i) => i !== ni) }
      profiles[s.activeIndex] = { ...p, hosts }
      return { ...s, profiles }
    })
  }, [])

  const updateNeighbour = useCallback((hi: number, ni: number, patch: Partial<NeighbourConfig>) => {
    setState(s => {
      const profiles = [...s.profiles]
      const p = profiles[s.activeIndex]
      const hosts = [...(p.hosts ?? [])]
      const neighbours = [...(hosts[hi].neighbours ?? [])]
      neighbours[ni] = { ...neighbours[ni], ...patch }
      hosts[hi] = { ...hosts[hi], neighbours }
      profiles[s.activeIndex] = { ...p, hosts }
      return { ...s, profiles }
    })
  }, [])

  // screen definitions
  const addScreen = useCallback(() => {
    setState(s => {
      const profiles = [...s.profiles]
      const p = profiles[s.activeIndex]
      profiles[s.activeIndex] = { ...p, screenDefinitions: [...(p.screenDefinitions ?? []), newScreenDefinition()] }
      return { ...s, profiles }
    })
  }, [])

  const removeScreen = useCallback((si: number) => {
    setState(s => {
      const profiles = [...s.profiles]
      const p = profiles[s.activeIndex]
      profiles[s.activeIndex] = { ...p, screenDefinitions: (p.screenDefinitions ?? []).filter((_, i) => i !== si) }
      return { ...s, profiles }
    })
  }, [])

  const updateScreen = useCallback((si: number, patch: Partial<ScreenDefinition>) => {
    setState(s => {
      const profiles = [...s.profiles]
      const p = profiles[s.activeIndex]
      const screenDefinitions = [...(p.screenDefinitions ?? [])]
      screenDefinitions[si] = { ...screenDefinitions[si], ...patch }
      profiles[s.activeIndex] = { ...p, screenDefinitions }
      return { ...s, profiles }
    })
  }, [])

  const updateConditions = useCallback((patch: Partial<ConfigConditions>) => {
    setState(s => {
      const profiles = [...s.profiles]
      const p = profiles[s.activeIndex]
      profiles[s.activeIndex] = { ...p, conditions: { ...p.conditions, ...patch } }
      return { ...s, profiles }
    })
  }, [])

  // profile tabs
  const addProfile = useCallback(() => {
    setState(s => ({
      ...s,
      profiles: [...s.profiles, newProfile()],
      activeIndex: s.profiles.length,
    }))
  }, [])

  const removeProfile = useCallback((i: number) => {
    setState(s => {
      if (s.profiles.length <= 1) return s
      const profiles = s.profiles.filter((_, idx) => idx !== i)
      return { ...s, profiles, activeIndex: Math.min(s.activeIndex, profiles.length - 1) }
    })
  }, [])

  const setActiveIndex = useCallback((i: number) => {
    setState(s => ({ ...s, activeIndex: i }))
  }, [])

  const importJson = useCallback((json: string): string | null => {
    try {
      setState(deserialize(json))
      return null
    } catch (e) {
      return e instanceof Error ? e.message : 'failed to parse config'
    }
  }, [])

  const reset = useCallback(() => setState(newFormState()), [])

  return {
    state,
    current,
    updateRoot,
    updateCurrent,
    addHost, removeHost, updateHost,
    addNeighbour, removeNeighbour, updateNeighbour,
    addScreen, removeScreen, updateScreen,
    updateConditions,
    addProfile, removeProfile, setActiveIndex,
    importJson,
    reset,
  }
}
