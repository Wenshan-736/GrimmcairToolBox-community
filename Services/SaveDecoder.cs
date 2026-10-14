using System.Text.Json.Nodes;

namespace Grimmcair.Desktop.Services;

/// <summary>Desktop adapter for the separately supplied read-only parser binary.</summary>
public sealed class SaveDecoder
{
    private readonly SaveParser parser;

    public SaveDecoder(string profilePath) => parser = new SaveParser(profilePath);
    public SaveDecoder() => parser = new SaveParser(ResourcePack.ReadBytes("save-profile.json"));

    public JsonObject Parse(string filePath, string? username = null) => parser.Parse(filePath, username);
    public JsonObject ParseGameExport(string filePath) => parser.ParseGameExport(filePath);
    public JsonObject ParseForImport(string filePath, string? fallbackUsername = null) => parser.ParseForImport(filePath, fallbackUsername);
    public JsonObject ParseBytesForImport(byte[] raw, string sourceName, string? fallbackUsername = null) =>
        parser.ParseBytesForImport(raw, sourceName, fallbackUsername);
}
