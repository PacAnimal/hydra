using System.Runtime.InteropServices;
using Hydra.Platform.Linux;

namespace Tests.Platform;

[TestFixture]
public class LinuxInteropLayoutTests
{
    [Test]
    public void XiDeviceEvent_RootCoordinateOffsetsMatchLp64Abi()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Marshal.OffsetOf<XIDeviceEvent>(nameof(XIDeviceEvent.RootX)).ToInt32(), Is.EqualTo(88));
            Assert.That(Marshal.OffsetOf<XIDeviceEvent>(nameof(XIDeviceEvent.RootY)).ToInt32(), Is.EqualTo(96));
        }
    }
}
