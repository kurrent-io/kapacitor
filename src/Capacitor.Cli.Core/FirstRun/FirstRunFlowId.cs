using System.Buffers.Text;
using System.Security.Cryptography;

namespace Capacitor.Cli.Core.FirstRun;

/// <summary>
/// The id naming one first-run flow. Generated here because the CLI creates the flow before the
/// browser opens, so nothing else is in a position to mint it.
///
/// <para><b>The server cannot check entropy, only length.</b> Its floor is 22 characters, which is
/// base64url of 16 bytes — chosen so that a CSPRNG ≥128-bit id is the only thing that fits. Anything
/// weaker generated here would be accepted, so the guarantee is this file's alone.</para>
/// </summary>
public static class FirstRunFlowId {
    /// <summary>128 bits. The id travels in a URL and is the only thing an attacker would have to
    /// guess, so it is sized to make guessing hopeless rather than merely unlikely.</summary>
    const int Bytes = 16;

    /// <summary>A fresh flow id: 128 CSPRNG bits, base64url, 22 characters.</summary>
    public static string New() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(Bytes));
}
