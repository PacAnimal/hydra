import { describe, it, expect } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import App from '../App'

describe('App', () => {
  it('renders without crashing', () => {
    render(<App />)
    expect(screen.getByText('Hydra')).toBeInTheDocument()
  })

  it('shows Master and Slave toggle buttons', () => {
    render(<App />)
    expect(screen.getByRole('button', { name: /master/i })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /slave/i })).toBeInTheDocument()
  })

  it('hides hosts section in Slave mode', async () => {
    const user = userEvent.setup()
    render(<App />)
    await user.click(screen.getByRole('button', { name: /slave/i }))
    expect(screen.queryByText('+ Add Host')).not.toBeInTheDocument()
  })

  it('shows hosts section in Master mode', async () => {
    render(<App />)
    expect(screen.getByText('+ Add Host')).toBeInTheDocument()
  })

  it('can add and see a host', async () => {
    const user = userEvent.setup()
    render(<App />)
    await user.click(screen.getByText('+ Add Host'))
    expect(screen.getByText('Host 1')).toBeInTheDocument()
  })

  it('resets to initial state', async () => {
    const user = userEvent.setup()
    render(<App />)
    await user.click(screen.getByText('+ Add Host'))
    await user.click(screen.getByText('Reset'))
    expect(screen.queryByText('Host 1')).not.toBeInTheDocument()
  })

  it('always shows profile tabs', () => {
    render(<App />)
    expect(screen.getByText('Profile 1')).toBeInTheDocument()
  })
})
