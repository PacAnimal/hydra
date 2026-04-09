import { useState, useCallback } from 'react'
import type { FormState, HydraConfig, HostConfig, NeighbourConfig, ScreenDefinition, ConfigConditions } from '../types'
import { newFormState, newHost, newNeighbour, newScreenDefinition, newConfig } from '../defaults'
import { deserialize } from '../utils/deserializer'

export function useHydraConfig() {
  const [state, setState] = useState<FormState>(newFormState)

  const current = state.configs[state.activeIndex] ?? state.configs[0]

  const updateCurrent = useCallback((patch: Partial<HydraConfig>) => {
    setState(s => {
      const configs = [...s.configs]
      configs[s.activeIndex] = { ...configs[s.activeIndex], ...patch }
      return { ...s, configs }
    })
  }, [])

  // hosts
  const addHost = useCallback(() => {
    setState(s => {
      const configs = [...s.configs]
      const cfg = configs[s.activeIndex]
      configs[s.activeIndex] = { ...cfg, hosts: [...(cfg.hosts ?? []), newHost()] }
      return { ...s, configs }
    })
  }, [])

  const removeHost = useCallback((hi: number) => {
    setState(s => {
      const configs = [...s.configs]
      const cfg = configs[s.activeIndex]
      const hosts = (cfg.hosts ?? []).filter((_, i) => i !== hi)
      configs[s.activeIndex] = { ...cfg, hosts }
      return { ...s, configs }
    })
  }, [])

  const updateHost = useCallback((hi: number, patch: Partial<HostConfig>) => {
    setState(s => {
      const configs = [...s.configs]
      const cfg = configs[s.activeIndex]
      const hosts = [...(cfg.hosts ?? [])]
      hosts[hi] = { ...hosts[hi], ...patch }
      configs[s.activeIndex] = { ...cfg, hosts }
      return { ...s, configs }
    })
  }, [])

  // neighbours
  const addNeighbour = useCallback((hi: number) => {
    setState(s => {
      const configs = [...s.configs]
      const cfg = configs[s.activeIndex]
      const hosts = [...(cfg.hosts ?? [])]
      hosts[hi] = { ...hosts[hi], neighbours: [...(hosts[hi].neighbours ?? []), newNeighbour()] }
      configs[s.activeIndex] = { ...cfg, hosts }
      return { ...s, configs }
    })
  }, [])

  const removeNeighbour = useCallback((hi: number, ni: number) => {
    setState(s => {
      const configs = [...s.configs]
      const cfg = configs[s.activeIndex]
      const hosts = [...(cfg.hosts ?? [])]
      hosts[hi] = { ...hosts[hi], neighbours: (hosts[hi].neighbours ?? []).filter((_, i) => i !== ni) }
      configs[s.activeIndex] = { ...cfg, hosts }
      return { ...s, configs }
    })
  }, [])

  const updateNeighbour = useCallback((hi: number, ni: number, patch: Partial<NeighbourConfig>) => {
    setState(s => {
      const configs = [...s.configs]
      const cfg = configs[s.activeIndex]
      const hosts = [...(cfg.hosts ?? [])]
      const neighbours = [...(hosts[hi].neighbours ?? [])]
      neighbours[ni] = { ...neighbours[ni], ...patch }
      hosts[hi] = { ...hosts[hi], neighbours }
      configs[s.activeIndex] = { ...cfg, hosts }
      return { ...s, configs }
    })
  }, [])

  // screen definitions
  const addScreen = useCallback(() => {
    setState(s => {
      const configs = [...s.configs]
      const cfg = configs[s.activeIndex]
      configs[s.activeIndex] = { ...cfg, screenDefinitions: [...(cfg.screenDefinitions ?? []), newScreenDefinition()] }
      return { ...s, configs }
    })
  }, [])

  const removeScreen = useCallback((si: number) => {
    setState(s => {
      const configs = [...s.configs]
      const cfg = configs[s.activeIndex]
      configs[s.activeIndex] = { ...cfg, screenDefinitions: (cfg.screenDefinitions ?? []).filter((_, i) => i !== si) }
      return { ...s, configs }
    })
  }, [])

  const updateScreen = useCallback((si: number, patch: Partial<ScreenDefinition>) => {
    setState(s => {
      const configs = [...s.configs]
      const cfg = configs[s.activeIndex]
      const screenDefinitions = [...(cfg.screenDefinitions ?? [])]
      screenDefinitions[si] = { ...screenDefinitions[si], ...patch }
      configs[s.activeIndex] = { ...cfg, screenDefinitions }
      return { ...s, configs }
    })
  }, [])

  const updateConditions = useCallback((patch: Partial<ConfigConditions>) => {
    setState(s => {
      const configs = [...s.configs]
      const cfg = configs[s.activeIndex]
      configs[s.activeIndex] = { ...cfg, conditions: { ...cfg.conditions, ...patch } }
      return { ...s, configs }
    })
  }, [])

  // multi-config
  const setMultiConfig = useCallback((enabled: boolean) => {
    setState(s => ({ ...s, multiConfig: enabled, activeIndex: 0 }))
  }, [])

  const addConfigEntry = useCallback(() => {
    setState(s => ({
      ...s,
      configs: [...s.configs, newConfig()],
      activeIndex: s.configs.length,
    }))
  }, [])

  const removeConfigEntry = useCallback((i: number) => {
    setState(s => {
      if (s.configs.length <= 1) return s
      const configs = s.configs.filter((_, idx) => idx !== i)
      return { ...s, configs, activeIndex: Math.min(s.activeIndex, configs.length - 1) }
    })
  }, [])

  const setActiveIndex = useCallback((i: number) => {
    setState(s => ({ ...s, activeIndex: i }))
  }, [])

  const importJson = useCallback((json: string): string | null => {
    try {
      const { configs, multiConfig } = deserialize(json)
      setState({ configs, multiConfig, activeIndex: 0 })
      return null
    } catch (e) {
      return e instanceof Error ? e.message : 'failed to parse config'
    }
  }, [])

  const reset = useCallback(() => setState(newFormState()), [])

  return {
    state,
    current,
    updateCurrent,
    addHost, removeHost, updateHost,
    addNeighbour, removeNeighbour, updateNeighbour,
    addScreen, removeScreen, updateScreen,
    updateConditions,
    setMultiConfig, addConfigEntry, removeConfigEntry, setActiveIndex,
    importJson,
    reset,
  }
}
