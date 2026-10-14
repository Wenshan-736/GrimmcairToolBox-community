using System.Text.Json.Nodes;

namespace Grimmcair.Desktop.Services;

public sealed record AiToolDefinition(string Name, string Description, JsonObject Parameters);
