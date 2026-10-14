using System.Text.Encodings.Web;
using System.Text.Json;

namespace Grimmcair.Desktop.Services;

/// <summary>MCP text is JSON, never HTML. Keep Chinese readable and count the text actually sent.</summary>
internal static class AiKnowledgeJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
