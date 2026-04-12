using System.Runtime.InteropServices;

namespace Hydra.Platform.MacOs;

// equivalent to @autoreleasepool {} — drains autoreleased ObjC objects deterministically
// on threadpool threads that have no implicit pool.
internal readonly struct ObjcAutoreleasePool : IDisposable
{
    private readonly nint _context;

    public ObjcAutoreleasePool()
    {
        _context = objc_autoreleasePoolPush();
    }

    public void Dispose()
    {
        if (_context != nint.Zero)
            objc_autoreleasePoolPop(_context);
    }

    // ReSharper disable InconsistentNaming
    [DllImport("libobjc.dylib")]
    private static extern nint objc_autoreleasePoolPush();

    [DllImport("libobjc.dylib")]
    private static extern void objc_autoreleasePoolPop(nint context);
    // ReSharper restore InconsistentNaming
}
