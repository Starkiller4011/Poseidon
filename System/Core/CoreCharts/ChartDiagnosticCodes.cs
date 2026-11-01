namespace Poseidon.Modules.Charts;

// Stable ChartDiagnostic.Code values, so callers and tests can tell outcomes apart without parsing messages.
public static class ChartDiagnosticCodes
{
    public const string CatalogueMissing = "catalogue-missing";
    public const string CatalogueMalformed = "catalogue-malformed";
    public const string CatalogueElementMissing = "catalogue-element-missing";
    public const string CatalogueEmpty = "catalogue-empty";
    public const string ExternalEntityRefused = "external-entity-refused";
    public const string PathOutsideRoot = "path-outside-root";
    public const string ProductUnsupported = "product-unsupported";
    public const string DatasetMalformed = "dataset-malformed";
    public const string DatasetMissing = "dataset-missing";
    public const string DdrDefinitionMissing = "ddr-definition-missing";
    public const string LimitExceeded = "limit-exceeded";
    public const string VersionUnsupported = "version-unsupported";
    public const string PreOperationalEdition = "pre-operational-edition";
    public const string UnresolvedCode = "unresolved-code";
    public const string UnresolvedReference = "unresolved-reference";
    public const string CatalogueMismatch = "catalogue-mismatch";
    public const string RecordCountMismatch = "record-count-mismatch";
    public const string SignatureInvalid = "signature-invalid";
    public const string SignatureUndecodable = "signature-undecodable";
    public const string Unsigned = "unsigned";
    public const string CertificateUnresolved = "certificate-unresolved";
    public const string UpdateOutOfSequence = "update-out-of-sequence";
    public const string UpdateEditionNewer = "update-edition-newer";
    public const string UpdateEditionOlder = "update-edition-older";
    public const string DatasetCancelled = "dataset-cancelled";
    public const string BaseNotInstalled = "base-not-installed";
    public const string StoreUnreadable = "store-unreadable";
    public const string SupportFileMissing = "support-file-missing";
    public const string SupportFileNotListed = "support-file-not-listed";
}
