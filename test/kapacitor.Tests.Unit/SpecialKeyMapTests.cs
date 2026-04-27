using kapacitor.Daemon.Services;

namespace kapacitor.Tests.Unit;

public sealed class SpecialKeyMapTests {
    [Test]
    public async Task Escape_returns_ESC() {
        var bytes = SpecialKeyMap.ToBytes("Escape");
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0x1b });
    }

    [Test]
    public async Task Tab_returns_HT() {
        var bytes = SpecialKeyMap.ToBytes("Tab");
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0x09 });
    }

    [Test]
    public async Task Enter_returns_CR() {
        var bytes = SpecialKeyMap.ToBytes("Enter");
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0x0d });
    }

    [Test]
    public async Task CtrlC_returns_ETX() {
        var bytes = SpecialKeyMap.ToBytes("CtrlC");
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0x03 });
    }

    [Test]
    public async Task ArrowUp_returns_CSI_A() {
        var bytes = SpecialKeyMap.ToBytes("ArrowUp");
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0x1b, 0x5b, 0x41 });
    }

    [Test]
    public async Task ArrowDown_returns_CSI_B() {
        var bytes = SpecialKeyMap.ToBytes("ArrowDown");
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0x1b, 0x5b, 0x42 });
    }

    [Test]
    public async Task ShiftTab_returns_CSI_Z() {
        var bytes = SpecialKeyMap.ToBytes("ShiftTab");
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 0x1b, 0x5b, 0x5a });
    }

    [Test]
    public async Task Unknown_key_returns_empty_array() {
        var bytes = SpecialKeyMap.ToBytes("BogusKey");
        await Assert.That(bytes.Length).IsEqualTo(0);
    }
}
