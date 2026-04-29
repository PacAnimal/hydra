import { useState, useCallback } from 'react'
import type { FormState, HydraProfile, HostConfig, NeighbourConfig, ScreenDefinition, ConfigConditions, LayoutItem } from '../types'
import { newFormState, newHost, newNeighbour, newScreenDefinition, newProfile } from '../defaults'
import { inferLayoutFromHosts, deriveHostsFromLayout } from '../utils/layout'
import { deserialize } from '../utils/deserializer'

const HISTORY_LIMIT = 50

interface HistorySlot {
  past: FormState[]
  present: FormState
  future: FormState[]
}

function withActive(s: FormState, fn: (p: HydraProfile) => HydraProfile): FormState {
  const profiles = [...s.profiles]
  profiles[s.activeIndex] = fn(profiles[s.activeIndex])
  return { ...s, profiles }
}

export function useHydraConfig() {
  const [hist, setHist] = useState<HistorySlot>(() => ({
    past: [],
    present: newFormState(),
    future: [],
  }))

  const state = hist.present

  // push to history + update state
  const push = useCallback((updater: (s: FormState) => FormState) => {
    setHist(h => {
      const next = updater(h.present)
      if (next === h.present) return h
      return {
        past: [...h.past.slice(-(HISTORY_LIMIT - 1)), h.present],
        present: next,
        future: [],
      }
    })
  }, [])

  // update state without pushing to history (tab navigation)
  const nav = useCallback((updater: (s: FormState) => FormState) => {
    setHist(h => ({ ...h, present: updater(h.present) }))
  }, [])

  const canUndo = hist.past.length > 0
  const canRedo = hist.future.length > 0

  const undo = useCallback(() => {
    setHist(h => {
      if (!h.past.length) return h
      const past = [...h.past]
      const present = past.pop()!
      return { past, present, future: [h.present, ...h.future.slice(0, HISTORY_LIMIT - 1)] }
    })
  }, [])

  const redo = useCallback(() => {
    setHist(h => {
      if (!h.future.length) return h
      const [present, ...future] = h.future
      return { past: [...h.past.slice(-(HISTORY_LIMIT - 1)), h.present], present, future }
    })
  }, [])

  const current = state.profiles[state.activeIndex] ?? state.profiles[0]

  const updateRoot = useCallback((patch: Partial<Pick<FormState, 'name' | 'autoUpdate' | 'logLevel' | 'lockFile' | 'logFile' | 'sessionLogFile'>>) => {
    push(s => ({ ...s, ...patch }))
  }, [push])

  const updateCurrent = useCallback((patch: Partial<HydraProfile>) => {
    push(s => withActive(s, p => ({ ...p, ...patch })))
  }, [push])

  const toggleVisualMode = useCallback(() => {
    push(s => {
      const p = s.profiles[s.activeIndex]
      const nowVisual = !p.visualMode
      if (nowVisual) {
        return withActive(s, p => ({ ...p, visualMode: true, layoutItems: inferLayoutFromHosts(p.hosts ?? []) }))
      } else {
        const hosts = p.layoutItems?.length ? deriveHostsFromLayout(p.layoutItems) : (p.hosts ?? [])
        return withActive(s, p => ({ ...p, visualMode: false, hosts }))
      }
    })
  }, [push])

  const updateLayoutItems = useCallback((items: LayoutItem[]) => {
    push(s => withActive(s, p => ({ ...p, layoutItems: items })))
  }, [push])

  // hosts (manual mode)
  const addHost = useCallback(() => {
    push(s => withActive(s, p => ({ ...p, hosts: [...(p.hosts ?? []), newHost()] })))
  }, [push])

  const removeHost = useCallback((hi: number) => {
    push(s => withActive(s, p => ({ ...p, hosts: (p.hosts ?? []).filter((_, i) => i !== hi) })))
  }, [push])

  const updateHost = useCallback((hi: number, patch: Partial<HostConfig>) => {
    push(s => withActive(s, p => {
      const hosts = [...(p.hosts ?? [])]
      hosts[hi] = { ...hosts[hi], ...patch }
      return { ...p, hosts }
    }))
  }, [push])

  // neighbours
  const addNeighbour = useCallback((hi: number) => {
    push(s => withActive(s, p => {
      const hosts = [...(p.hosts ?? [])]
      hosts[hi] = { ...hosts[hi], neighbours: [...(hosts[hi].neighbours ?? []), newNeighbour()] }
      return { ...p, hosts }
    }))
  }, [push])

  const removeNeighbour = useCallback((hi: number, ni: number) => {
    push(s => withActive(s, p => {
      const hosts = [...(p.hosts ?? [])]
      hosts[hi] = { ...hosts[hi], neighbours: (hosts[hi].neighbours ?? []).filter((_, i) => i !== ni) }
      return { ...p, hosts }
    }))
  }, [push])

  const updateNeighbour = useCallback((hi: number, ni: number, patch: Partial<NeighbourConfig>) => {
    push(s => withActive(s, p => {
      const hosts = [...(p.hosts ?? [])]
      const neighbours = [...(hosts[hi].neighbours ?? [])]
      neighbours[ni] = { ...neighbours[ni], ...patch }
      hosts[hi] = { ...hosts[hi], neighbours }
      return { ...p, hosts }
    }))
  }, [push])

  // screen definitions
  const addScreen = useCallback(() => {
    push(s => withActive(s, p => ({ ...p, screenDefinitions: [...(p.screenDefinitions ?? []), newScreenDefinition()] })))
  }, [push])

  const removeScreen = useCallback((si: number) => {
    push(s => withActive(s, p => ({ ...p, screenDefinitions: (p.screenDefinitions ?? []).filter((_, i) => i !== si) })))
  }, [push])

  const updateScreen = useCallback((si: number, patch: Partial<ScreenDefinition>) => {
    push(s => withActive(s, p => {
      const screenDefinitions = [...(p.screenDefinitions ?? [])]
      screenDefinitions[si] = { ...screenDefinitions[si], ...patch }
      return { ...p, screenDefinitions }
    }))
  }, [push])

  const updateConditions = useCallback((patch: Partial<ConfigConditions>) => {
    push(s => withActive(s, p => ({ ...p, conditions: { ...p.conditions, ...patch } })))
  }, [push])

  // profile tabs
  const addProfile = useCallback(() => {
    push(s => ({
      ...s,
      profiles: [...s.profiles, newProfile()],
      activeIndex: s.profiles.length,
    }))
  }, [push])

  const removeProfile = useCallback((i: number) => {
    push(s => {
      if (s.profiles.length <= 1) return s
      const profiles = s.profiles.filter((_, idx) => idx !== i)
      return { ...s, profiles, activeIndex: Math.min(s.activeIndex, profiles.length - 1) }
    })
  }, [push])

  const duplicateProfile = useCallback((i: number) => {
    push(s => {
      const src = s.profiles[i]
      const ts = Date.now()
      const rand = () => Math.random().toString(36).slice(2)
      const copy: HydraProfile = {
        ...src,
        profileName: `${src.profileName} (copy)`,
        hosts: src.hosts?.map(h => ({
          ...h,
          id: `h-${ts}-${rand()}`,
          neighbours: h.neighbours?.map(n => ({ ...n, id: `n-${ts}-${rand()}` })),
        })),
        layoutItems: src.layoutItems?.map(li => ({ ...li, id: `layout-${ts}-${rand()}` })),
        screenDefinitions: src.screenDefinitions?.map(sd => ({ ...sd, id: `s-${ts}-${rand()}` })),
      }
      const profiles = [...s.profiles.slice(0, i + 1), copy, ...s.profiles.slice(i + 1)]
      return { ...s, profiles, activeIndex: i + 1 }
    })
  }, [push])

  const moveProfile = useCallback((i: number, direction: 'left' | 'right') => {
    push(s => {
      const j = direction === 'left' ? i - 1 : i + 1
      if (j < 0 || j >= s.profiles.length) return s
      const profiles = [...s.profiles]
      ;[profiles[i], profiles[j]] = [profiles[j], profiles[i]]
      return { ...s, profiles, activeIndex: j }
    })
  }, [push])

  const setActiveIndex = useCallback((i: number) => {
    nav(s => ({ ...s, activeIndex: i }))
  }, [nav])

  const importJson = useCallback((json: string): string | null => {
    try {
      setHist({ past: [], present: deserialize(json), future: [] })
      return null
    } catch (e) {
      return e instanceof Error ? e.message : 'failed to parse config'
    }
  }, [])

  const reset = useCallback(() => {
    setHist({ past: [], present: newFormState(), future: [] })
  }, [])

  return {
    state,
    current,
    canUndo, canRedo, undo, redo,
    updateRoot,
    updateCurrent,
    toggleVisualMode,
    updateLayoutItems,
    addHost, removeHost, updateHost,
    addNeighbour, removeNeighbour, updateNeighbour,
    addScreen, removeScreen, updateScreen,
    updateConditions,
    addProfile, removeProfile, duplicateProfile, moveProfile, setActiveIndex,
    importJson,
    reset,
  }
}
