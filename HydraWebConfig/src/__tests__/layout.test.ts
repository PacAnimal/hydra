import { describe, it, expect } from 'vitest'
import { deriveHostsFromLayout, inferLayoutFromHosts, DEFAULT_W, DEFAULT_H } from '../utils/layout'
import type { LayoutItem, HostConfig } from '../types'

// helpers
function item(id: string, hostName: string, x: number, y: number, w = DEFAULT_W, h = DEFAULT_H, screenId?: string): LayoutItem {
  return { id, hostName, x, y, w, h, screenId }
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

  it('creates a right neighbour for horizontally adjacent blocks', () => {
    const items = [
      item('a', 'desktop', 0, 0),
      item('b', 'laptop', DEFAULT_W, 0),
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
      item('b', 'laptop', DEFAULT_W, 0),
    ]
    const hosts = deriveHostsFromLayout(items)
    const laptop = hosts.find(h => h.name === 'laptop')!
    expect(laptop.neighbours![0].direction).toBe('Left')
    expect(laptop.neighbours![0].name).toBe('desktop')
  })

  it('does not create neighbours for same-host adjacent blocks', () => {
    const items = [
      item('a', 'desktop', 0, 0),
      item('b', 'desktop', DEFAULT_W, 0),
    ]
    const hosts = deriveHostsFromLayout(items)
    expect(hosts).toHaveLength(1)
    expect(hosts[0].neighbours).toBeUndefined()
  })

  it('sets sourceScreen and destScreen when screenIds are present', () => {
    const items = [
      item('a', 'desktop', 0, 0, DEFAULT_W, DEFAULT_H, 'HDMI-1'),
      item('b', 'laptop', DEFAULT_W, 0, DEFAULT_W, DEFAULT_H, 'eDP-1'),
    ]
    const hosts = deriveHostsFromLayout(items)
    const desktop = hosts.find(h => h.name === 'desktop')!
    expect(desktop.neighbours![0].sourceScreen).toBe('HDMI-1')
    expect(desktop.neighbours![0].destScreen).toBe('eDP-1')
  })

  it('omits sourceScreen and destScreen when no screenIds', () => {
    const items = [
      item('a', 'desktop', 0, 0),
      item('b', 'laptop', DEFAULT_W, 0),
    ]
    const hosts = deriveHostsFromLayout(items)
    const desktop = hosts.find(h => h.name === 'desktop')!
    expect(desktop.neighbours![0].sourceScreen).toBeUndefined()
    expect(desktop.neighbours![0].destScreen).toBeUndefined()
  })

  it('creates down/up neighbours for vertical adjacency', () => {
    const items = [
      item('a', 'desktop', 0, 0),
      item('b', 'laptop', 0, DEFAULT_H),
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
      item('b', 'mac', DEFAULT_W, 0),
      item('c', 'monitor', DEFAULT_W * 2, 0),
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

  it('computes sourceStart when right neighbour is vertically offset', () => {
    // laptop starts halfway down desktop — cursor arriving at laptop lands at its top
    const items = [
      item('a', 'desktop', 0, 0, DEFAULT_W, DEFAULT_H),
      item('b', 'laptop', DEFAULT_W, DEFAULT_H / 2, DEFAULT_W, DEFAULT_H),
    ]
    const hosts = deriveHostsFromLayout(items)
    const desktop = hosts.find(h => h.name === 'desktop')!
    const n = desktop.neighbours![0]
    expect(n.direction).toBe('Right')
    expect(n.sourceStart).toBe(50)      // desktop active edge starts 50% down
    expect(n.sourceEnd).toBeUndefined() // reaches to 100% (default)
    expect(n.destStart).toBeUndefined() // laptop top aligns with overlap top (default 0)
    expect(n.destEnd).toBe(50)          // overlap covers only bottom 50% of laptop
  })

  it('computes both source and dest ranges for partial overlaps', () => {
    // desktop starts at y=540 (offset 50%), laptop starts at y=0
    // overlap: y=540–1080, which is the bottom half of desktop and top half of laptop
    const items = [
      item('a', 'desktop', 0, DEFAULT_H / 2, DEFAULT_W, DEFAULT_H),
      item('b', 'laptop', DEFAULT_W, 0, DEFAULT_W, DEFAULT_H),
    ]
    const hosts = deriveHostsFromLayout(items)
    const desktop = hosts.find(h => h.name === 'desktop')!
    const n = desktop.neighbours![0]
    expect(n.sourceStart).toBeUndefined() // overlap starts at desktop's top edge (default 0)
    expect(n.sourceEnd).toBe(50)           // overlap ends at 50% of desktop
    expect(n.destStart).toBe(50)           // overlap starts 50% down laptop
    expect(n.destEnd).toBeUndefined()      // reaches 100% of laptop (default)
  })

  it('omits all range fields when blocks are perfectly aligned', () => {
    const items = [
      item('a', 'desktop', 0, 0, DEFAULT_W, DEFAULT_H),
      item('b', 'laptop', DEFAULT_W, 0, DEFAULT_W, DEFAULT_H),
    ]
    const hosts = deriveHostsFromLayout(items)
    const desktop = hosts.find(h => h.name === 'desktop')!
    const n = desktop.neighbours![0]
    expect(n.sourceStart).toBeUndefined()
    expect(n.sourceEnd).toBeUndefined()
    expect(n.destStart).toBeUndefined()
    expect(n.destEnd).toBeUndefined()
  })
})

describe('inferLayoutFromHosts', () => {
  it('returns empty array for empty hosts', () => {
    expect(inferLayoutFromHosts([])).toEqual([])
  })

  it('places a single host with default dimensions', () => {
    const hosts: HostConfig[] = [{ name: 'desktop', neighbours: [] }]
    const layout = inferLayoutFromHosts(hosts)
    expect(layout).toHaveLength(1)
    expect(layout[0].hostName).toBe('desktop')
    expect(layout[0].w).toBe(DEFAULT_W)
    expect(layout[0].h).toBe(DEFAULT_H)
  })

  it('places right-neighbour host one screen-width to the right', () => {
    const hosts: HostConfig[] = [
      { name: 'desktop', neighbours: [{ direction: 'Right', name: 'laptop' }] },
      { name: 'laptop', neighbours: [] },
    ]
    const layout = inferLayoutFromHosts(hosts)
    const desktop = layout.find(i => i.hostName === 'desktop')!
    const laptop = layout.find(i => i.hostName === 'laptop')!
    expect(laptop.x).toBe(desktop.x + DEFAULT_W)
    expect(laptop.y).toBe(desktop.y)
  })

  it('places down-neighbour host one screen-height below', () => {
    const hosts: HostConfig[] = [
      { name: 'desktop', neighbours: [{ direction: 'Down', name: 'laptop' }] },
      { name: 'laptop', neighbours: [] },
    ]
    const layout = inferLayoutFromHosts(hosts)
    const desktop = layout.find(i => i.hostName === 'desktop')!
    const laptop = layout.find(i => i.hostName === 'laptop')!
    expect(laptop.y).toBe(desktop.y + DEFAULT_H)
    expect(laptop.x).toBe(desktop.x)
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

  it('applies sourceStart offset when inferring neighbour position', () => {
    // laptop is a right neighbour starting at sourceStart=50 (halfway down desktop)
    const hosts: HostConfig[] = [
      { name: 'desktop', neighbours: [{ direction: 'Right', name: 'laptop', sourceStart: 50 }] },
      { name: 'laptop', neighbours: [] },
    ]
    const layout = inferLayoutFromHosts(hosts)
    const desktop = layout.find(i => i.hostName === 'desktop')!
    const laptop = layout.find(i => i.hostName === 'laptop')!
    // laptop.y should be offset by 50% of desktop.h
    expect(laptop.y).toBe(desktop.y + DEFAULT_H * 0.5)
  })
})
