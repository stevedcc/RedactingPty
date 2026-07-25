using System.Text;
using RedactingPty;
using Xunit;

namespace RedactingPty.Tests;

/// <summary>
/// Tests for the FallbackPty drain plumbing (byte decode → StreamRedactor → re-encode)
/// using in-memory streams, without spawning a subprocess. Exercised via
/// InternalsVisibleTo since Drain is an implementation detail of FallbackPty.
/// </summary>
public class FallbackPtyTests
{
    private static string Drain(string input, IReadOnlyList<StreamReplacement> replacements)
    {
        var source = new MemoryStream(Encoding.UTF8.GetBytes(input));
        var dest = new MemoryStream();
        FallbackPty.Drain(source, dest, replacements, Encoding.UTF8, writeLock: null);
        return Encoding.UTF8.GetString(dest.ToArray());
    }

    [Fact]
    public void Drain_ReplacesSecretInOutput()
    {
        var result = Drain("password is hunter2 ok",
            [new StreamReplacement("hunter2", "[REDACTED: pw]")]);
        Assert.Equal("password is [REDACTED: pw] ok", result);
    }

    [Fact]
    public void Drain_SecretStraddlingReadBufferBoundary_Replaced()
    {
        // Drain reads in 4096-byte chunks; place the secret across the 4096 boundary.
        var input = new string('x', 4090) + "hunter2" + " end";
        var result = Drain(input, [new StreamReplacement("hunter2", "[REDACTED: pw]")]);
        Assert.DoesNotContain("hunter2", result);
        Assert.Contains("[REDACTED: pw] end", result);
        Assert.StartsWith(new string('x', 4090), result);
    }

    [Fact]
    public void Drain_NoReplacements_OutputUnchanged()
    {
        var result = Drain("plain output, nothing sensitive", []);
        Assert.Equal("plain output, nothing sensitive", result);
    }

    [Fact]
    public void Drain_MultiByteUtf8AcrossBufferBoundary_Preserved()
    {
        // A multi-byte UTF-8 character spanning the 4096-byte read boundary must survive
        // the decode/re-encode round trip (Decoder carries partial sequences between reads).
        var input = new string('x', 4095) + "é after-boundary hunter2";
        var result = Drain(input, [new StreamReplacement("hunter2", "[REDACTED: pw]")]);
        Assert.Contains("é after-boundary [REDACTED: pw]", result);
    }

    [Fact]
    public async Task Drain_WithSharedLock_ConcurrentWritesDontCorrupt()
    {
        // Simulates the redirected-stdout scenario: two Drain calls share the
        // same destination stream and lock. Verifies no torn writes and both
        // secrets are redacted.
        var sharedLock = new object();
        var dest = new MemoryStream();
        var replacements = new List<StreamReplacement>
        {
            new StreamReplacement("hunter2", "[REDACTED: pw]"),
        };

        var source1 = new MemoryStream(Encoding.UTF8.GetBytes(
            "stdout: password is hunter2 here\n"));
        var source2 = new MemoryStream(Encoding.UTF8.GetBytes(
            "stderr: token is hunter2 there\n"));

        var t1 = Task.Run(() => FallbackPty.Drain(source1, dest, replacements, Encoding.UTF8, sharedLock));
        var t2 = Task.Run(() => FallbackPty.Drain(source2, dest, replacements, Encoding.UTF8, sharedLock));
        await Task.WhenAll(t1, t2);

        var output = Encoding.UTF8.GetString(dest.ToArray());
        Assert.DoesNotContain("hunter2", output);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(output, @"\[REDACTED: pw\]").Count);
    }

    [Fact]
    public void Drain_EmptyInput_ProducesNoOutput()
    {
        var source = new MemoryStream(Array.Empty<byte>());
        var dest   = new MemoryStream();
        FallbackPty.Drain(source, dest, [], Encoding.UTF8, writeLock: null);
        Assert.Equal(0, dest.Length);
    }

    [Fact]
    public void Drain_SecretEndingExactlyAtBufferBoundary_Replaced()
    {
        // Place the secret so its last byte lands exactly on byte 4096 (0-indexed),
        // i.e. the secret ends right at the read-buffer boundary.
        var secret = "hunter2"; // 7 UTF-8 bytes
        var input  = new string('x', 4096 - secret.Length) + secret + " tail";
        var result = DrainString(input, [new StreamReplacement(secret, "[REDACTED: pw]")]);
        Assert.DoesNotContain(secret, result);
        Assert.Contains("[REDACTED: pw] tail", result);
    }

    // Private helper used by the new tests above (mirrors the existing Drain helper in this class).
    private static string DrainString(string input, IReadOnlyList<StreamReplacement> replacements)
    {
        var source = new MemoryStream(Encoding.UTF8.GetBytes(input));
        var dest   = new MemoryStream();
        FallbackPty.Drain(source, dest, replacements, Encoding.UTF8, writeLock: null);
        return Encoding.UTF8.GetString(dest.ToArray());
    }
}
