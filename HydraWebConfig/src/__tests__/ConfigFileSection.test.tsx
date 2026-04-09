import { describe, it, expect, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ConfigFileSection } from '../components/ConfigFileSection'
import type { FormState } from '../types'

const state: FormState = { name: 'test', profiles: [{ profileName: 'Home', mode: 'Master' }], activeIndex: 0 }
const noop = () => {}

describe('ConfigFileSection', () => {
  it('renders Copy and Download buttons', () => {
    render(<ConfigFileSection state={state} isValid={true} onScrollToErrors={noop} />)
    expect(screen.getByRole('button', { name: /copy to clipboard/i })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /download/i })).toBeInTheDocument()
  })

  it('displays JSON', () => {
    const { container } = render(<ConfigFileSection state={state} isValid={true} onScrollToErrors={noop} />)
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

    render(<ConfigFileSection state={state} isValid={true} onScrollToErrors={noop} />)
    await user.click(screen.getByRole('button', { name: /copy to clipboard/i }))
    expect(writeText).toHaveBeenCalledWith(expect.stringContaining('"mode"'))
  })

  it('disables Copy and Download when invalid', () => {
    render(<ConfigFileSection state={state} isValid={false} onScrollToErrors={noop} />)
    expect(screen.getByRole('button', { name: /copy to clipboard/i })).toBeDisabled()
    expect(screen.getByRole('button', { name: /download/i })).toBeDisabled()
  })

  it('shows INCOMPLETE when invalid', () => {
    render(<ConfigFileSection state={state} isValid={false} onScrollToErrors={noop} />)
    expect(screen.getByText('(INCOMPLETE!)')).toBeInTheDocument()
  })

  it('hides INCOMPLETE when valid', () => {
    render(<ConfigFileSection state={state} isValid={true} onScrollToErrors={noop} />)
    expect(screen.queryByText('(INCOMPLETE!)')).not.toBeInTheDocument()
  })

  it('calls onScrollToErrors when INCOMPLETE is clicked', async () => {
    const user = userEvent.setup()
    const onScrollToErrors = vi.fn()
    render(<ConfigFileSection state={state} isValid={false} onScrollToErrors={onScrollToErrors} />)
    await user.click(screen.getByText('(INCOMPLETE!)'))
    expect(onScrollToErrors).toHaveBeenCalledOnce()
  })
})
