import { describe, it, expect, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { DropZone } from '../components/DropZone'

describe('DropZone', () => {
  it('renders Import Config button', () => {
    render(<DropZone onImport={vi.fn()} />)
    expect(screen.getByRole('button', { name: /import config/i })).toBeInTheDocument()
  })

  it('shows error message when onImport returns an error', async () => {
    const user = userEvent.setup()
    const onImport = vi.fn().mockReturnValue('invalid JSON')

    render(<DropZone onImport={onImport} />)

    const input = screen.getByLabelText(/import config file/i)
    const file = new File(['bad json'], 'hydra.conf', { type: 'application/json' })
    await user.upload(input, file)

    // wait for FileReader
    await vi.waitFor(() => {
      expect(screen.getByText('invalid JSON')).toBeInTheDocument()
    })
  })

  it('calls onImport with file contents', async () => {
    const user = userEvent.setup()
    const onImport = vi.fn().mockReturnValue(null)
    const json = '{"mode":"Master"}'

    render(<DropZone onImport={onImport} />)

    const input = screen.getByLabelText(/import config file/i)
    const file = new File([json], 'hydra.conf', { type: 'application/json' })
    await user.upload(input, file)

    await vi.waitFor(() => {
      expect(onImport).toHaveBeenCalledWith(json)
    })
  })
})
