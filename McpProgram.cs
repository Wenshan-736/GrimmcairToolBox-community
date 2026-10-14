using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grimmcair.Desktop.Services;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Grimmcair.Desktop;

internal static class McpProgram
{
    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            if (args is ["--help"])
            {
                Console.WriteLine("知识库 MCP：默认通过标准输入/输出提供只读服务。\n"
                    + "资料选择：--builtin-knowledge 或 --data-package <资料ZIP>；可加 --data-package-sha256 <SHA256> 固定资料身份。\n"
                    + "只读命令：--status 查看资料身份；--mcp-config 输出本机 MCP 配置；--export-skill <新Markdown文件> 导出本包游戏导读。\n"
                    + "交付核对：--verify-source-manifest <文档manifest.json> 核对文档与内嵌资料的来源字节。\n"
                    + "文档阅读：--render-docs-html <文档目录> 将该目录的 Markdown 转为新 HTML，不覆盖已有页面。\n"
                    + "导出不覆盖已有文件；这些命令不启动桌面、模型连接或 MCP 服务。");
                return 0;
            }
            DesktopInstallationSession.HoldForCurrentProcess();
            var remaining = KnowledgePackSelection.Initialize(args);
            if (remaining is ["--status"])
            {
                Console.WriteLine(JsonSerializer.Serialize(ResourcePack.Current));
                return 0;
            }
            if (remaining is ["--mcp-config"])
            {
                var executable = Path.Combine(AppContext.BaseDirectory, "Grimmcair.KnowledgeMcp.exe");
                if (!File.Exists(executable)) throw new FileNotFoundException("独立 MCP 程序不完整，请重新安装。", executable);
                var package = ResourcePack.Current;
                var parameters = package.PackagePath is { } packagePath
                    ? new[] { "--data-package", packagePath, "--data-package-sha256", package.PackageSha256 }
                    : new[] { "--builtin-knowledge", "--data-package-sha256", package.PackageSha256 };
                var configuration = new JsonObject { ["mcpServers"] = new JsonObject { ["grimmcair"] = new JsonObject
                { ["command"] = executable, ["args"] = new JsonArray(parameters.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()) } } };
                // Default encoder keeps copied JSON ASCII-only, including non-ASCII installation paths.
                Console.WriteLine(configuration.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if (remaining is ["--export-skill", var output])
            {
                var text = AiKnowledgeSkill.Load();
                var path = Path.GetFullPath(output);
                if (!Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("导读输出须为新的 .md 文件。");
                var bytes = new UTF8Encoding(false).GetBytes(text);
                // The caller supplies an existing output directory. Never replace an old guide or create personal state.
                await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await file.WriteAsync(bytes);
                Console.WriteLine(JsonSerializer.Serialize(new { output = path, characters = text.Length, bytes = bytes.Length,
                    knowledgeRevision = ResourcePack.Current.PackageSha256, mapVersion = ResourcePack.Current.MapVersion,
                    sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant() }));
                return 0;
            }
            if (remaining is ["--verify-source-manifest", var manifestPath])
            {
                if (new FileInfo(manifestPath).Length > 8 * 1024 * 1024) throw new InvalidDataException("文档清单过大。");
                var manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject
                    ?? throw new InvalidDataException("文档清单格式错误。");
                if (manifest["format"]?.GetValue<string>() != "grimmcair-reading-documents"
                    || manifest["mapVersion"]?.GetValue<string>() != ResourcePack.Current.MapVersion
                    || manifest["mapSha256"]?.GetValue<string>() != ResourcePack.Current.MapSha256
                    || manifest["scriptSha256"]?.GetValue<string>() != ResourcePack.Current.ScriptSha256
                    || manifest["sourceFiles"] is not JsonArray sourceFiles || sourceFiles.Count == 0)
                    throw new InvalidDataException("文档清单与当前知识库版本不符。");
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var file in sourceFiles)
                {
                    var name = file?["path"]?.GetValue<string>() ?? "";
                    var expected = file?["sha256"]?.GetValue<string>() ?? "";
                    if (name.Length == 0 || !names.Add(name) || !ResourcePack.Contains(name)
                        || expected.Length != 64 || !expected.All(char.IsAsciiHexDigit))
                        throw new InvalidDataException("来源清单含缺失、重复或无效资源。");
                    var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ResourcePack.ReadBytes(name))).ToLowerInvariant();
                    if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("文档来源与当前知识库字节不同：" + name);
                }
                foreach (var required in new[] { "catalog.json", "guide.json", "concepts.json", "equipment-guide.json" })
                    if (!names.Contains(required)) throw new InvalidDataException("来源清单缺少公共核心资料。");
                Console.WriteLine(JsonSerializer.Serialize(new { verified = names.Count, knowledgeRevision = ResourcePack.Current.PackageSha256 }));
                return 0;
            }
            if (remaining is ["--render-docs-html", var documentDirectory])
            {
                Console.WriteLine(JsonSerializer.Serialize(new { pages = RenderReadingHtml(documentDirectory) }));
                return 0;
            }
            if (remaining.Length != 0 && remaining is not ["--ai-knowledge-mcp"])
                throw new ArgumentException("参数无效。使用 --help 查看资料选择、MCP 配置和导读导出方式。");
            if (KnowledgePackSelection.StartupWarning is { } warning) Console.Error.WriteLine(warning);
            await AiKnowledgeMcp.RunServerAsync();
            return 0;
        }
        catch (Exception error)
        {
            // The MCP transport uses stdout exclusively. No desktop login, archive watcher,
            // credential store or real UserData is initialized by this entry point.
            Console.Error.WriteLine("知识库 MCP 未能启动：" + error.Message);
            return 1;
        }
    }

    private static int RenderReadingHtml(string directory)
    {
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("阅读文档目录不存在。");
        for (var parent = new DirectoryInfo(root); parent is not null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("阅读文档不能位于链接目录。");
        var files = Directory.EnumerateFiles(root, "*.md", new EnumerationOptions
        { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Take(10001).ToArray();
        if (files.Length is 0 or > 10000 || files.Any(file => new FileInfo(file).Length > 2 * 1024 * 1024))
            throw new InvalidDataException("阅读文档数量或单页大小超限。");
        if (files.Any(file => File.Exists(Path.ChangeExtension(file, ".html"))))
            throw new IOException("HTML 页面已经存在，请使用新导出的文档目录。");
        var builder = new MarkdownPipelineBuilder { MaximumNestingDepth = 64 }.UsePipeTables().UseCjkFriendlyEmphasis().DisableHtml();
        // Explicit links are checked below. Bare URI/email autolinks remain text, not implicit navigations.
        builder.InlineParsers.TryRemove<Markdig.Parsers.Inlines.AutolinkInlineParser>();
        var pipeline = builder.Build();
        foreach (var source in files)
        {
            var document = Markdown.Parse(File.ReadAllText(source), pipeline);
            foreach (var link in document.Descendants<LinkInline>())
            {
                var safe = ReadingLink(link.Url ?? "", source, root, link.IsImage);
                link.GetDynamicUrl = null;
                if (safe is null) { link.Url = "#"; link.IsImage = false; }
                else link.Url = safe;
            }
            var html = "<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\">"
                + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
                + "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src 'self' file:; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\">"
                + "<title>妙妙工具箱 · 公开知识库</title><style>"
                + "body{max-width:1100px;margin:30px auto;padding:0 24px 60px;background:#fbf5e8;color:#483425;font:16px/1.8 'Microsoft YaHei',sans-serif;overflow-wrap:anywhere}"
                + "a{color:#895524}h1,h2,h3{line-height:1.4}h1{border-bottom:2px solid #c8a979;padding-bottom:14px}h2{margin-top:34px}"
                + "table{border-collapse:collapse;display:block;max-width:100%;overflow:auto}th,td{border:1px solid #ceb993;padding:8px 12px;vertical-align:top}th{background:#efdfbf}"
                + "pre{overflow:auto;padding:14px;background:#f1e5cf}code{font-family:Consolas,monospace}img{max-width:100%;height:auto}blockquote{border-left:3px solid #c8a979;margin-left:0;padding-left:18px;color:#6d5844}"
                + "</style><body><main>" + Markdown.ToHtml(document, pipeline) + "</main></body></html>";
            using var output = new FileStream(Path.ChangeExtension(source, ".html"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(new UTF8Encoding(false).GetBytes(html));
        }
        return files.Length;
    }

    private static string? ReadingLink(string url, string source, string root, bool image)
    {
        if (url.Any(char.IsControl) || url.Contains('\\') || url.StartsWith("//", StringComparison.Ordinal)) return null;
        if (url.StartsWith('#')) return image ? null : url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var remote))
            return !image && (remote.Scheme is "https" or "http") && string.IsNullOrEmpty(remote.UserInfo) ? remote.AbsoluteUri : null;
        var fragmentIndex = url.IndexOf('#');
        var local = fragmentIndex < 0 ? url : url[..fragmentIndex];
        var fragment = fragmentIndex < 0 ? "" : url[fragmentIndex..];
        if (local.Length == 0 || local.Contains('?') || local.Contains(':')) return null;
        try
        {
            var decoded = Uri.UnescapeDataString(local);
            if (Path.IsPathRooted(decoded) || decoded.Any(char.IsControl) || decoded.Contains('\\') || decoded.Contains(':')) return null;
            var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, decoded));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) return null;
            for (var item = new FileInfo(target) as FileSystemInfo; item is not null && !item.FullName.Equals(root, StringComparison.OrdinalIgnoreCase);
                item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            var extension = Path.GetExtension(target).ToLowerInvariant();
            if (image) return (extension is ".png" or ".jpg" or ".jpeg")
                && target.StartsWith(Path.Combine(root, "images") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? local + fragment : null;
            return extension == ".md" ? local[..^3] + ".html" + fragment : null;
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        { return null; }
    }
}
