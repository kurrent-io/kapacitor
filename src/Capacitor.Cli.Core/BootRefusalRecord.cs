namespace Capacitor.Cli.Core;

/// <summary>
/// Contents of the boot-refusal marker a daemon leaves behind when it refuses to start: either the
/// server-expectation check or the consent-seed classification came back Refused.
/// <see cref="Expectation"/>/<see cref="Resolved"/> mirror the config's expected/resolved server URL
/// at write time regardless of which check fired — a consent-seed refusal still carries a non-null
/// <see cref="Expectation"/> whenever the operator configured one (and it was satisfied, which is why
/// the boot got far enough to reach the consent-seed check at all); both are null only when no
/// expectation was configured. <see cref="Pid"/>/<see cref="InstanceId"/>/<see cref="AttemptId"/> let
/// a caller correlate the marker with the exact boot attempt that wrote it.
///
/// <para><see cref="DaemonName"/> is the RAW configured name, not the on-disk id — compare it
/// through <see cref="DaemonStore.Sanitize"/> rather than verbatim.</para>
/// </summary>
public sealed record BootRefusalRecord(
        int            Schema,
        string         DaemonName,
        string         Token,
        string?        Expectation,
        string?        Resolved,
        int            Pid,
        string?        InstanceId,
        string?        AttemptId,
        DateTimeOffset Timestamp
    );
