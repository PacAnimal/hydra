import { describe, it, expect } from 'vitest'
import { deriveHostsFromLayout, inferLayoutFromHosts } from '../utils/layout'
import type { LayoutItem, HostConfig } from '../types'

// helpers
function item(id: string, hostName: string, col: number, row: number, screenId?: string): LayoutItem {
  return { id, hostName, col, row, screenId }
}

describe('deriveHostsFromLayout', () => {
  it('returns empty array for empty layout', () => {
    expect(deriveHostsFromLayout([])).toEqual([])
  })

  it('creates a host with no neighbours for a single block', () => {
    const hosts = deriveHostsFromLayout([item('a', 'desktop', 0, 0)])
    expect(hosts).toHaveLength(1)
    expect(hosts[0].name).toBe('desktop')
    expect(hosts[0].neighbours).toBeUndefined()
  })

  it('creates a right neighbour for adjacent different-host blocks', () => {
    const items = [
      item('a', 'desktop', 0, 0),
      item('b', 'laptop', 1, 0),
    ]
    const hosts = deriveHostsFromLayout(items)
    const desktop = hosts.find(h => h.name === 'desktop')!
    expect(desktop.neighbours).toHaveLength(1)
    expect(desktop.neighbours![0].direction).toBe('Right')
    expect(desktop.neighbours![0].name).toBe('laptop')
  })

  it('creates a left neighbour from the other side', () => {
    const items = [
      item('a', 'desktop', 0, 0),
      item('b', 'laptop', 1, 0),
    ]
    const hosts = deriveHostsFromLayout(items)
    const laptop = hosts.find(h => h.name === 'laptop')!
    expect(laptop.neighbours![0].direction).toBe('Left')
    expect(laptop.neighbours![0].name).toBe('desktop')
  })

  it('does not create neighbours for same-host adjacent blocks', () => {
    const items = [
      item('a', 'desktop', 0, 0),
      item('b', 'desktop', 1, 0),
    ]
    const hosts = deriveHostsFromLayout(items)
    expect(hosts).toHaveLength(1)
    expect(hosts[0].neighbours).toBeUndefined()
  })

  it('sets sourceScreen and destScreen when screenIds are present', () => {
    const items = [
      item('a', 'desktop', 0, 0, 'HDMI-1'),
      item('b', 'laptop', 1, 0, 'eDP-1'),
    ]
    const hosts = deriveHostsFromLayout(items)
    const desktop = hosts.find(h => h.name === 'desktop')!
    expect(desktop.neighbours![0].sourceScreen).toBe('HDMI-1')
    expect(desktop.neighbours![0].destScreen).toBe('eDP-1')
  })

  it('omits sourceScreen and destScreen when no screenIds', () => {
    const items = [
      item('a', 'desktop', 0, 0),
      item('b', 'laptop', 1, 0),
    ]
    const hosts = deriveHostsFromLayout(items)
    const desktop = hosts.find(h => h.name === 'desktop')!
    expect(desktop.neighbours![0].sourceScreen).toBeUndefined()
    expect(desktop.neighbours![0].destScreen).toBeUndefined()
  })

  it('creates down/up neighbours for vertical adjacency', () => {
    const items = [
      item('a', 'desktop', 0, 0),
      item('b', 'laptop', 0, 1),
    ]
    const hosts = deriveHostsFromLayout(items)
    const desktop = hosts.find(h => h.name === 'desktop')!
    expect(desktop.neighbours![0].direction).toBe('Down')
  })

  it('preserves deadCorners per host', () => {
    const items: LayoutItem[] = [{ ...item('a', 'desktop', 0, 0), deadCorners: 10 }]
    const hosts = deriveHostsFromLayout(items)
    expect(hosts[0].deadCorners).toBe(10)
  })

  it('handles 3 hosts in a row', () => {
    const items = [
      item('a', 'pc', 0, 0),
      item('b', 'mac', 1, 0),
      item('c', 'monitor', 2, 0),
    ]
    const hosts = deriveHostsFromLayout(items)
    const pc = hosts.find(h => h.name === 'pc')!
    const mac = hosts.find(h => h.name === 'mac')!
    const monitor = hosts.find(h => h.name === 'monitor')!

    expect(pc.neighbours).toHaveLength(1)
    expect(pc.neighbours![0].name).toBe('mac')

    expect(mac.neighbours).toHaveLength(2)
    expect(mac.neighbours!.map(n => n.name).sort()).toEqual(['monitor', 'pc'])

    expect(monitor.neighbours).toHaveLength(1)
    expect(monitor.neighbours![0].name).toBe('mac')
  })
})

describe('inferLayoutFromHosts', () => {
  it('returns empty array for empty hosts', () => {
    expect(inferLayoutFromHosts([])).toEqual([])
  })

  it('places a single host at a stable position', () => {
    const hosts: HostConfig[] = [{ name: 'desktop', neighbours: [] }]
    const layout = inferLayoutFromHosts(hosts)
    expect(layout).toHaveLength(1)
    expect(layout[0].hostName).toBe('desktop')
  })

  it('places right-neighbour host one column to the right', () => {
    const hosts: HostConfig[] = [
      { name: 'desktop', neighbours: [{ direction: 'Right', name: 'laptop' }] },
      { name: 'laptop', neighbours: [] },
    ]
    const layout = inferLayoutFromHosts(hosts)
    const desktop = layout.find(i => i.hostName === 'desktop')!
    const laptop = layout.find(i => i.hostName === 'laptop')!
    expect(laptop.col).toBe(desktop.col + 1)
    expect(laptop.row).toBe(desktop.row)
  })

  it('places down-neighbour host one row below', () => {
    const hosts: HostConfig[] = [
      { name: 'desktop', neighbours: [{ direction: 'Down', name: 'laptop' }] },
      { name: 'laptop', neighbours: [] },
    ]
    const layout = inferLayoutFromHosts(hosts)
    const desktop = layout.find(i => i.hostName === 'desktop')!
    const laptop = layout.find(i => i.hostName === 'laptop')!
    expect(laptop.row).toBe(desktop.row + 1)
    expect(laptop.col).toBe(desktop.col)
  })

  it('creates one block per unique host for simple configs', () => {
    const hosts: HostConfig[] = [
      { name: 'a', neighbours: [{ direction: 'Right', name: 'b' }] },
      { name: 'b', neighbours: [{ direction: 'Right', name: 'c' }] },
      { name: 'c', neighbours: [] },
    ]
    const layout = inferLayoutFromHosts(hosts)
    const hostNames = [...new Set(layout.map(i => i.hostName))]
    expect(hostNames.sort()).toEqual(['a', 'b', 'c'])
  })

  it('round-trips through deriveHostsFromLayout (basic)', () => {
    const hosts: HostConfig[] = [
      { name: 'desktop', neighbours: [{ direction: 'Right', name: 'laptop' }] },
      { name: 'laptop', neighbours: [{ direction: 'Left', name: 'desktop' }] },
    ]
    const layout = inferLayoutFromHosts(hosts)
    const derived = deriveHostsFromLayout(layout)
    const desktopDerived = derived.find(h => h.name === 'desktop')!
    expect(desktopDerived.neighbours!.some(n => n.name === 'laptop' && n.direction === 'Right')).toBe(true)
  })
})
