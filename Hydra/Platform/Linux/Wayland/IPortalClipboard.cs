namespace Hydra.Platform.Linux.Wayland;

// what PortalClipboardSync uses of the portal's clipboard, so a test can stand in for the portal
internal interface IPortalClipboard
{
    // the selection this session advertised is wanted as mimeType; answer with Write (or SelectionWriteDone) on serial
    event Func<string, uint, Task>? SelectionTransfer;

    // the clipboard changed: its MIME types, and whether this session owns it
    event Action<IReadOnlyList<string>, bool>? SelectionOwnerChanged;

    Task SetSelection(IReadOnlyList<string> mimeTypes);

    Task SelectionWriteDone(uint serial, bool success);

    // the selection as mimeType, read to EOF; null when it runs past maxBytes
    Task<byte[]?> Read(string mimeType, int maxBytes, TimeSpan timeout, CancellationToken cancel);

    // writes data into the transfer's pipe, then reports whether it all went
    Task Write(uint serial, byte[] data);
}
