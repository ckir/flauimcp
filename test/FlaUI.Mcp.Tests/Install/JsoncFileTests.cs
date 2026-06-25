using System.Text.Json.Nodes;
using FlaUI.Mcp.Server.Install;
using Xunit;

namespace FlaUI.Mcp.Tests.Install;

public class JsoncFileTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"flaui-jsonc-{Guid.NewGuid():N}.json");

    [Fact]
    public void Load_missing_file_returns_empty_object()
    {
        var obj = JsoncFile.Load(TempFile());
        Assert.Empty(obj);
    }

    [Fact]
    public void Load_tolerates_comments_and_trailing_commas()
    {
        var path = TempFile();
        File.WriteAllText(path, "{\n  // a comment\n  \"a\": 1,\n  \"b\": 2,\n}");
        try
        {
            var obj = JsoncFile.Load(path);
            Assert.Equal(1, (int)obj["a"]!);
            Assert.Equal(2, (int)obj["b"]!);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_preserves_unrelated_keys_and_writes_a_backup()
    {
        var path = TempFile();
        File.WriteAllText(path, "{ \"keep\": \"me\" }");
        try
        {
            var obj = JsoncFile.Load(path);
            obj["added"] = "new";
            JsoncFile.Save(path, obj);

            var reloaded = JsoncFile.Load(path);
            Assert.Equal("me", (string)reloaded["keep"]!);
            Assert.Equal("new", (string)reloaded["added"]!);
            Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(path)!,
                Path.GetFileName(path) + ".bak-*"));
        }
        finally
        {
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
                File.Delete(f);
        }
    }
}
