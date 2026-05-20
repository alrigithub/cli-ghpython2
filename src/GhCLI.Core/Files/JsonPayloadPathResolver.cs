using System.Text.Json;
using System.Text.Json.Nodes;

namespace GhCLI.Core.Files;

public static class JsonPayloadPathResolver
{
    public static JsonElement ResolveFilePaths(JsonElement payload, string payloadFilePath)
    {
        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(payloadFilePath))
                            ?? Directory.GetCurrentDirectory();
        var node = JsonNode.Parse(payload.GetRawText());
        ResolveFilePathProperties(node, baseDirectory);

        using var doc = JsonDocument.Parse(node?.ToJsonString() ?? payload.GetRawText());
        return doc.RootElement.Clone();
    }

    private static void ResolveFilePathProperties(JsonNode? node, string baseDirectory)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToArray())
                {
                    if (IsFilePathProperty(property.Key) &&
                        property.Value is JsonValue value &&
                        value.TryGetValue<string>(out var path) &&
                        !string.IsNullOrWhiteSpace(path))
                    {
                        obj[property.Key] = ResolvePath(path, baseDirectory);
                        continue;
                    }

                    ResolveFilePathProperties(property.Value, baseDirectory);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    ResolveFilePathProperties(item, baseDirectory);
                }

                break;
        }
    }

    private static bool IsFilePathProperty(string name)
    {
        return string.Equals(name, "file_path", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "filePath", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolvePath(string path, string baseDirectory)
    {
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(baseDirectory, path));
    }
}
