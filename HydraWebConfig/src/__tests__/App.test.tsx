import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import App from '../App'

describe('App', () => {
  beforeEach(() => {
    vi.spyOn(window, 'confirm').mockReturnValue(true)
  })

  it('renders without crashing', () => {
    render(<App />)
    expect(screen.getByText('Hydra')).toBeInTheDocument()
  })

  it('shows Master and Slave toggle buttons', () => {
    render(<App />)
    expect(screen.getByRole('button', { name: /master/i })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /slave/i })).toBeInTheDocument()
  })

  it('shows canvas add button in visual mode', () => {
    render(<App />)
    expect(screen.getByText('+ Add Screen')).toBeInTheDocument()
  })

  it('hides hosts section in Slave mode', async () => {
    const user = userEvent.setup()
    render(<App />)
    await user.click(screen.getByRole('button', { name: /slave/i }))
    expect(screen.queryByText('+ Add Host')).not.toBeInTheDocument()
    expect(screen.queryByText('+ Add Screen')).not.toBeInTheDocument()
  })

  it('can add a screen block in visual mode', async () => {
    const user = userEvent.setup()
    render(<App />)
    await user.click(screen.getByText('+ Add Screen'))
    // add form should appear
    expect(screen.getByPlaceholderText(/e.g. desktop/i)).toBeInTheDocument()
  })

  it('resets to initial state', async () => {
    const user = userEvent.setup()
    render(<App />)
    await user.click(screen.getByRole('button', { name: '+' }))
    expect(screen.getByText('Profile 2')).toBeInTheDocument()
    await user.click(screen.getByText('Reset'))
    expect(screen.queryByText('Profile 2')).not.toBeInTheDocument()
  })

  it('always shows profile tabs', () => {
    render(<App />)
    expect(screen.getByText('Profile 1')).toBeInTheDocument()
  })
})
