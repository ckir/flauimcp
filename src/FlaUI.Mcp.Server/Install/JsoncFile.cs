using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlaUI.Mcp.Server.Install;

/// <summary>
/// Loads a JSON/JSONC file as a mutable <see cref="JsonObject"/>, tolerating comments and
/// trailing commas, and saves it atomically (temp + move) after a timestamped backup.
/// NOTE: comments are NOT preserved across a save (System.Text.Json reserializes) — the
/// pre-write backup `<file>.bak-<timestamp>` is the recovery path; all data keys ARE kept.
/// </summary>
public static class JsoncFile
{
    private static readonly JsonDocumentOptions DocOpts =
        new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static JsonObject Load(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        var node = JsonNode.Parse(text, nodeOptions: null, documentOptions: DocOpts);
        return node as JsonObject
            ?? throw new InvalidOperationException($"{path} is not a JSON object.");
    }

    public static void Save(string path, JsonObject obj)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(path))
            File.Copy(path, $"{path}.bak-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }
}
