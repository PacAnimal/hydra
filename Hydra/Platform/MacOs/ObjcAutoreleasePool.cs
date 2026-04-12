using System.Runtime.InteropServices;

namespace Hydra.Platform.MacOs;

// equivalent to @autoreleasepool {} — drains autoreleased ObjC objects deterministically
// on threadpool threads that have no implicit pool.
internal readonly partial struct ObjcAutoreleasePool : IDisposable
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
    [LibraryImport("libobjc.dylib")]
    private static partial nint objc_autoreleasePoolPush();

    [LibraryImport("libobjc.dylib")]
    private static partial void objc_autoreleasePoolPop(nint context);
    // ReSharper restore InconsistentNaming
}
