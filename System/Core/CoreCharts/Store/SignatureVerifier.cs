using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Poseidon.Core.Modules;
using Poseidon.Modules.Charts.ExchangeSets;

namespace Poseidon.Modules.Charts.Store;

// Rejected is true only for a decodable signature that fails verification (C36).
internal sealed record SignatureCheck(bool Rejected, ChartDiagnostic? Diagnostic);

// Verifies a file's ECDSA signature (S-100 Part 15) against the certificate its own catalogue carries.
// No certificate chain or trust is evaluated (Q7): this proves the bytes are what the producer signed.
internal sealed class SignatureVerifier(IReadOnlyDictionary<string, string> certificates) : IDisposable
{
    private readonly Dictionary<string, ECDsa?> keys = new(StringComparer.Ordinal);

    public void Dispose()
    {
        foreach (var key in keys.Values)
            key?.Dispose();
    }

    public SignatureCheck Check(DatasetSignature? signature, byte[] data, string file)
    {
        if (signature is null)
            return Warning(ChartDiagnosticCodes.Unsigned, $"{file} has no digital signature in the catalogue.", file);

        byte[] value;
        try
        {
            value = Convert.FromBase64String(signature.Value);
            var reader = new AsnReader(value, AsnEncodingRules.DER);
            var sequence = reader.ReadSequence();
            sequence.ReadIntegerBytes();
            sequence.ReadIntegerBytes();
            sequence.ThrowIfNotEmpty();
            reader.ThrowIfNotEmpty();
        }
        catch (Exception e) when (e is FormatException or AsnContentException)
        {
            return Warning(ChartDiagnosticCodes.SignatureUndecodable,
                $"The signature of {file} is not a decodable ECDSA signature; the file is treated as unsigned.", file);
        }

        // A signature with no certificate reference can only mean the catalogue's single certificate.
        var reference = signature.CertificateRef ?? (certificates.Count == 1 ? certificates.Keys.Single() : null);
        if (reference is null || !certificates.ContainsKey(reference))
            return Warning(ChartDiagnosticCodes.CertificateUnresolved,
                $"The signature of {file} names certificate '{signature.CertificateRef}', which the catalogue does not contain; it cannot be verified.", file);
        if (Key(reference) is not { } key)
            return Warning(ChartDiagnosticCodes.CertificateUnresolved,
                $"Certificate '{reference}' for {file} is not a readable ECDSA certificate; the signature cannot be verified.", file);

        // S-100 2.0.0 data is signed with SHA-384 and 1.1.0 data with SHA-256, but producers are not
        // consistent (the sub-group's CorruptData set uses SHA-256 with 2.0.0 data), so either is accepted.
        if (key.VerifyData(data, value, HashAlgorithmName.SHA384, DSASignatureFormat.Rfc3279DerSequence)
            || key.VerifyData(data, value, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            return new SignatureCheck(false, null);
        return new SignatureCheck(true, new ChartDiagnostic(ChartDiagnosticSeverity.Error, ChartDiagnosticCodes.SignatureInvalid,
            $"{file} does not match its signature: it was altered or damaged after signing, and is rejected as corrupt.", file));
    }

    private ECDsa? Key(string reference)
    {
        if (keys.TryGetValue(reference, out var key))
            return key;
        try
        {
            var der = Convert.FromBase64String(string.Concat(certificates[reference].Where(c => !char.IsWhiteSpace(c))));
            using var certificate = X509CertificateLoader.LoadCertificate(der);
            key = certificate.GetECDsaPublicKey();
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            key = null;
        }
        return keys[reference] = key;
    }

    private static SignatureCheck Warning(string code, string message, string file)
        => new(false, new ChartDiagnostic(ChartDiagnosticSeverity.Warning, code, message, file));
}
