import { useState, useCallback } from 'react'
import type { FormState, HydraProfile, HostConfig, NeighbourConfig, ScreenDefinition, ConfigConditions, LayoutItem } from '../types'
import { newFormState, newHost, newNeighbour, newScreenDefinition, newProfile } from '../defaults'
import { inferLayoutFromHosts, deriveHostsFromLayout } from '../utils/layout'
import { deserialize } from '../utils/deserializer'

// applies a profile update to the active profile in state
function withActive(s: FormState, fn: (p: HydraProfile) => HydraProfile): FormState {
  const profiles = [...s.profiles]
  profiles[s.activeIndex] = fn(profiles[s.activeIndex])
  return { ...s, profiles }
}

export function useHydraConfig() {
  const [state, setState] = useState<FormState>(newFormState)

  const current = state.profiles[state.activeIndex] ?? state.profiles[0]

  const updateRoot = useCallback((patch: Partial<Pick<FormState, 'name' | 'autoUpdate' | 'logLevel' | 'lockFile' | 'logFile' | 'sessionLogFile'>>) => {
    setState(s => ({ ...s, ...patch }))
  }, [])

  const updateCurrent = useCallback((patch: Partial<HydraProfile>) => {
    setState(s => withActive(s, p => ({ ...p, ...patch })))
  }, [])

  // switch between visual canvas and manual hosts editor
  const toggleVisualMode = useCallback(() => {
    setState(s => {
      const p = s.profiles[s.activeIndex]
      const nowVisual = !p.visualMode
      if (nowVisual) {
        // switching to visual: infer layout from current hosts
        return withActive(s, p => ({ ...p, visualMode: true, layoutItems: inferLayoutFromHosts(p.hosts ?? []) }))
      } else {
        // switching to manual: derive hosts from current layout
        const hosts = p.layoutItems?.length ? deriveHostsFromLayout(p.layoutItems) : (p.hosts ?? [])
        return withActive(s, p => ({ ...p, visualMode: false, hosts }))
      }
    })
  }, [])

  const updateLayoutItems = useCallback((items: LayoutItem[]) => {
    setState(s => withActive(s, p => ({ ...p, layoutItems: items })))
  }, [])

  // hosts (manual mode)
  const addHost = useCallback(() => {
    setState(s => withActive(s, p => ({ ...p, hosts: [...(p.hosts ?? []), newHost()] })))
  }, [])

  const removeHost = useCallback((hi: number) => {
    setState(s => withActive(s, p => ({ ...p, hosts: (p.hosts ?? []).filter((_, i) => i !== hi) })))
  }, [])

  const updateHost = useCallback((hi: number, patch: Partial<HostConfig>) => {
    setState(s => withActive(s, p => {
      const hosts = [...(p.hosts ?? [])]
      hosts[hi] = { ...hosts[hi], ...patch }
      return { ...p, hosts }
    }))
  }, [])

  // neighbours
  const addNeighbour = useCallback((hi: number) => {
    setState(s => withActive(s, p => {
      const hosts = [...(p.hosts ?? [])]
      hosts[hi] = { ...hosts[hi], neighbours: [...(hosts[hi].neighbours ?? []), newNeighbour()] }
      return { ...p, hosts }
    }))
  }, [])

  const removeNeighbour = useCallback((hi: number, ni: number) => {
    setState(s => withActive(s, p => {
      const hosts = [...(p.hosts ?? [])]
      hosts[hi] = { ...hosts[hi], neighbours: (hosts[hi].neighbours ?? []).filter((_, i) => i !== ni) }
      return { ...p, hosts }
    }))
  }, [])

  const updateNeighbour = useCallback((hi: number, ni: number, patch: Partial<NeighbourConfig>) => {
    setState(s => withActive(s, p => {
      const hosts = [...(p.hosts ?? [])]
      const neighbours = [...(hosts[hi].neighbours ?? [])]
      neighbours[ni] = { ...neighbours[ni], ...patch }
      hosts[hi] = { ...hosts[hi], neighbours }
      return { ...p, hosts }
    }))
  }, [])

  // screen definitions
  const addScreen = useCallback(() => {
    setState(s => withActive(s, p => ({ ...p, screenDefinitions: [...(p.screenDefinitions ?? []), newScreenDefinition()] })))
  }, [])

  const removeScreen = useCallback((si: number) => {
    setState(s => withActive(s, p => ({ ...p, screenDefinitions: (p.screenDefinitions ?? []).filter((_, i) => i !== si) })))
  }, [])

  const updateScreen = useCallback((si: number, patch: Partial<ScreenDefinition>) => {
    setState(s => withActive(s, p => {
      const screenDefinitions = [...(p.screenDefinitions ?? [])]
      screenDefinitions[si] = { ...screenDefinitions[si], ...patch }
      return { ...p, screenDefinitions }
    }))
  }, [])

  const updateConditions = useCallback((patch: Partial<ConfigConditions>) => {
    setState(s => withActive(s, p => ({ ...p, conditions: { ...p.conditions, ...patch } })))
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
    toggleVisualMode,
    updateLayoutItems,
    addHost, removeHost, updateHost,
    addNeighbour, removeNeighbour, updateNeighbour,
    addScreen, removeScreen, updateScreen,
    updateConditions,
    addProfile, removeProfile, setActiveIndex,
    importJson,
    reset,
  }
}
