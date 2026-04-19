using Hydra.Platform.Linux;

namespace Tests.Keyboard;

[TestFixture]
public class XorgKeyResolverTests
{
    [TestCase(0xFF95UL, 0xFF50UL)]  // KP_Home → Home
    [TestCase(0xFF96UL, 0xFF51UL)]  // KP_Left → Left
    [TestCase(0xFF97UL, 0xFF52UL)]  // KP_Up → Up
    [TestCase(0xFF98UL, 0xFF53UL)]  // KP_Right → Right
    [TestCase(0xFF99UL, 0xFF54UL)]  // KP_Down → Down
    [TestCase(0xFF9AUL, 0xFF55UL)]  // KP_Prior → PageUp
    [TestCase(0xFF9BUL, 0xFF56UL)]  // KP_Next → PageDown
    [TestCase(0xFF9CUL, 0xFF57UL)]  // KP_End → End
    [TestCase(0xFF9EUL, 0xFF63UL)]  // KP_Insert → Insert
    [TestCase(0xFF9FUL, 0xFFFFUL)]  // KP_Delete → Delete
    public void MapKpNavToStandard_MapsAllNavigationKeys(ulong kpKeysym, ulong expected)
    {
        Assert.That(XorgKeyResolver.MapKpNavToStandard(kpKeysym), Is.EqualTo(expected));
    }

    [Test]
    public void MapKpNavToStandard_KpBegin_PassesThrough()
    {
        // KP_Begin (center 5) has no standard navigation equivalent
        Assert.That(XorgKeyResolver.MapKpNavToStandard(0xFF9DUL), Is.EqualTo(0xFF9DUL));
    }

    [Test]
    public void MapKpNavToStandard_NonKpKey_PassesThrough()
    {
        Assert.That(XorgKeyResolver.MapKpNavToStandard(0xFF51UL), Is.EqualTo(0xFF51UL));
    }

    [TestCase(0xFF9EUL, '0')]  // KP_Insert → 0
    [TestCase(0xFF9CUL, '1')]  // KP_End → 1
    [TestCase(0xFF99UL, '2')]  // KP_Down → 2
    [TestCase(0xFF9BUL, '3')]  // KP_Next → 3
    [TestCase(0xFF96UL, '4')]  // KP_Left → 4
    [TestCase(0xFF9DUL, '5')]  // KP_Begin → 5
    [TestCase(0xFF98UL, '6')]  // KP_Right → 6
    [TestCase(0xFF95UL, '7')]  // KP_Home → 7
    [TestCase(0xFF97UL, '8')]  // KP_Up → 8
    [TestCase(0xFF9AUL, '9')]  // KP_Prior → 9
    [TestCase(0xFF9FUL, (ulong)'.')]  // KP_Delete → .
    public void KpNavToChar_MapsAllPositions(ulong kpKeysym, ulong expectedChar)
    {
        Assert.That(XorgKeyResolver.KpNavToChar(kpKeysym), Is.EqualTo(expectedChar));
    }

    [TestCase(0xFFB0UL, 0xFF9EUL)]  // KP_0 → KP_Insert
    [TestCase(0xFFB1UL, 0xFF9CUL)]  // KP_1 → KP_End
    [TestCase(0xFFB2UL, 0xFF99UL)]  // KP_2 → KP_Down
    [TestCase(0xFFB3UL, 0xFF9BUL)]  // KP_3 → KP_Next
    [TestCase(0xFFB4UL, 0xFF96UL)]  // KP_4 → KP_Left
    [TestCase(0xFFB5UL, 0xFF9DUL)]  // KP_5 → KP_Begin
    [TestCase(0xFFB6UL, 0xFF98UL)]  // KP_6 → KP_Right
    [TestCase(0xFFB7UL, 0xFF95UL)]  // KP_7 → KP_Home
    [TestCase(0xFFB8UL, 0xFF97UL)]  // KP_8 → KP_Up
    [TestCase(0xFFB9UL, 0xFF9AUL)]  // KP_9 → KP_Prior
    [TestCase(0xFFAEUL, 0xFF9FUL)]  // KP_Decimal → KP_Delete
    public void KpNumericToNav_MapsAllDigits(ulong kpNumeric, ulong expectedNav)
    {
        Assert.That(XorgKeyResolver.KpNumericToNav(kpNumeric), Is.EqualTo(expectedNav));
    }

    [Test]
    public void KpNumericToNav_FollowedByMapKpNavToStandard_ProducesStandardNav()
    {
        // round-trip: KP_7 → KP_Home → Home (Left arrow block)
        var nav = XorgKeyResolver.KpNumericToNav(0xFFB7UL);
        var standard = XorgKeyResolver.MapKpNavToStandard(nav);
        Assert.That(standard, Is.EqualTo(0xFF50UL)); // Home
    }
}
