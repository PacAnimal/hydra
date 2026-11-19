namespace Hydra.Platform.Linux;

// the kernel's keyboards read without a grab, so the compositor still gets every key: raw evdev key codes as they come
internal interface IEvdevKeySource : IDisposable
{
    // evdev KEY_* code and whether it went down; raised on the source's own thread
    event Action<uint, bool>? Key;

    // opens every keyboard the user can read and follows hotplug; false, with nothing opened, when the user cannot read
    // the keyboards: the input directory is unreadable, or no keyboard opened and some node was refused for permission.
    // True with no keyboard yet (waiting for one) only when nothing was refused
    bool Start();
}
