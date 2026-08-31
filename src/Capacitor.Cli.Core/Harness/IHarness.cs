namespace Capacitor.Cli.Core.Harness;

/// <summary>
/// One harness as this process sees it: its identity, and what it can answer about the machine.
/// The registry hands these out, so a consumer that treats every vendor alike never names one.
///
/// <para>A vendor's layout is NOT here. It stays that vendor's own type, exposed by its module, so
/// the code that reads a vendor's files takes the paths it needs and everything else takes this.
/// </para>
/// </summary>
public interface IHarness {
    HarnessId      Id      { get; }
    string         Label   { get; }
    HarnessSignals Signals { get; }

    string VendorId => Id.VendorId;
}

/// <summary>
/// What a vendor implements. Identity is declared as statics — it is a fact about the type, not
/// about an instance — and bridged onto <see cref="IHarness"/> here so the registry can iterate
/// without knowing the concrete type. <c>new</c> is required: without it the static and the
/// inherited instance member collide.
///
/// <para><typeparamref name="TSelf"/> is the implementing type. Nothing stops a copy-paste from
/// naming another vendor's type there, and the resulting mislabel is invisible at the call site —
/// <c>HarnessRegistryTests</c> pins that every entry reports its own identity.</para>
/// </summary>
public interface IHarness<TSelf> : IHarness where TSelf : IHarness<TSelf> {
    new abstract static HarnessId Id    { get; }
    new abstract static string    Label { get; }

    HarnessId IHarness.Id    => TSelf.Id;
    string    IHarness.Label => TSelf.Label;
}
