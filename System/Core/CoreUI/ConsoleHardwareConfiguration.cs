using System.Text.Json;
using System.Text.Json.Serialization;

namespace Poseidon.Modules.CoreUI;

// H1: which of the two supported hardware configurations (m1-h-ui-extensibility.md) this deployment
// uses. Single: one window (chart + side panels + control pages). Dual: two windows (chart + side
// panels; control pages).
internal enum DisplayMode { Single, Dual }

internal sealed record ConsoleConfiguration(DisplayMode DisplayMode);

// H1: the hardware configuration stays with the module, found next to its own assembly, not supplied by
// the host — the same precedent as CoreRadar's per-channel hardware configuration (RadarHardwareConfiguration).
// Unlike Radar's "no file -> one implicit default channel", a missing, malformed, or unrecognized
// console.json is rejected outright (H1): which physical console this deployment is built for is not
// something to guess a default for.
internal static class ConsoleHardwareConfiguration
{
    public const string FileName = "console.json";

    private sealed record ConfigurationFile(
        [property: JsonPropertyName("displayMode")] string? DisplayMode);

    public static ConsoleConfiguration Load(string moduleDirectory)
    {
        var path = Path.Combine(moduleDirectory, FileName);
        if (!File.Exists(path))
            throw new InvalidDataException($"{FileName} is required (which physical console this is).");

        ConfigurationFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ConfigurationFile>(
                File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{FileName} is not valid JSON: {ex.Message}", ex);
        }

        // Case-sensitive: "single"/"dual" are rejected along with any other value, same as a typo.
        return file?.DisplayMode switch
        {
            "Single" => new ConsoleConfiguration(DisplayMode.Single),
            "Dual" => new ConsoleConfiguration(DisplayMode.Dual),
            _ => throw new InvalidDataException(
                $"{FileName}'s displayMode must be \"Single\" or \"Dual\".")
        };
    }
}
