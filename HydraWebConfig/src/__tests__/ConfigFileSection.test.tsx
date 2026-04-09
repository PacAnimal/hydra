import { describe, it, expect, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ConfigFileSection } from '../components/ConfigFileSection'
import type { HydraConfig } from '../types'

const cfg: HydraConfig = { mode: 'Master', name: 'test' }

describe('ConfigFileSection', () => {
  it('renders View and Download buttons', () => {
    render(<ConfigFileSection configs={[cfg]} multiConfig={false} />)
    expect(screen.getByRole('button', { name: /view/i })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /download/i })).toBeInTheDocument()
  })

  it('opens dialog on View click', async () => {
    const user = userEvent.setup()
    render(<ConfigFileSection configs={[cfg]} multiConfig={false} />)
    await user.click(screen.getByRole('button', { name: /view/i }))
    expect(HTMLDialogElement.prototype.showModal).toHaveBeenCalled()
  })

  it('displays JSON in the dialog', async () => {
    const user = userEvent.setup()
    const { container } = render(<ConfigFileSection configs={[cfg]} multiConfig={false} />)
    await user.click(screen.getByRole('button', { name: /view/i }))
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
    await user.click(screen.getByRole('button', { name: /view/i }))
    // dialog is hidden in jsdom (showModal is stubbed), query with { hidden: true }
    const copyBtn = screen.getByRole('button', { name: /copy to clipboard/i, hidden: true })
    await user.click(copyBtn)
    expect(writeText).toHaveBeenCalledWith(expect.stringContaining('"mode"'))
  })
})
