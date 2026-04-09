import { describe, it, expect, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ConfigFileSection } from '../components/ConfigFileSection'
import type { HydraConfig } from '../types'

const cfg: HydraConfig = { mode: 'Master', name: 'test' }

describe('ConfigFileSection', () => {
  it('renders Copy and Download buttons', () => {
    render(<ConfigFileSection configs={[cfg]} multiConfig={false} />)
    expect(screen.getByRole('button', { name: /copy to clipboard/i })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /download/i })).toBeInTheDocument()
  })

  it('displays JSON', () => {
    const { container } = render(<ConfigFileSection configs={[cfg]} multiConfig={false} />)
    const pre = container.querySelector('pre')
    expect(pre?.textContent).toContain('"mode"')
  })

  it('copies JSON to clipboard on Copy button click', async () => {
    const user = userEvent.setup()
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'clipboard', {
      value: { writeText },
      writable: true,
      configurable: true,
    })

    render(<ConfigFileSection configs={[cfg]} multiConfig={false} />)
    await user.click(screen.getByRole('button', { name: /copy to clipboard/i }))
    expect(writeText).toHaveBeenCalledWith(expect.stringContaining('"mode"'))
  })
})
