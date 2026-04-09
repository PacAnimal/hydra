import { describe, it, expect, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ConfigFileSection } from '../components/ConfigFileSection'
import type { FormState } from '../types'

const state: FormState = { name: 'test', profiles: [{ profileName: 'Home', mode: 'Master' }], activeIndex: 0 }

describe('ConfigFileSection', () => {
  it('renders Copy and Download buttons', () => {
    render(<ConfigFileSection state={state} />)
    expect(screen.getByRole('button', { name: /copy to clipboard/i })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /download/i })).toBeInTheDocument()
  })

  it('displays JSON', () => {
    const { container } = render(<ConfigFileSection state={state} />)
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

    render(<ConfigFileSection state={state} />)
    await user.click(screen.getByRole('button', { name: /copy to clipboard/i }))
    expect(writeText).toHaveBeenCalledWith(expect.stringContaining('"mode"'))
  })
})
