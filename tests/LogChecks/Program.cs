using AutoJev.Local;

var root = Directory.GetCurrentDirectory();
if (!File.Exists(Path.Combine(root, ".gitignore"))) throw new Exception("Run from project root.");
var dir = Path.Combine(root, "tmp", "log-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
try
{
    var path = Path.Combine(dir, "test.log");
    using (var writer = new RollingLogWriter(path, maxBytes: 128, retained: 3))
    {
        Parallel.For(0, 200, i => writer.WriteLine($"line-{i:D3}-abcdefghijklmno"));
        writer.WriteLine("final-marker");
    }
    var files = Directory.GetFiles(dir);
    if (files.Length != 4) throw new Exception("Retention failure.");
    foreach (var file in files)
    {
        var bytes = File.ReadAllBytes(file);
        if (bytes.Length > 170 || bytes.Contains((byte)0)) throw new Exception("Sparse or oversized log.");
    }
    if (!File.ReadAllText(path).Contains("final-marker")) throw new Exception("Latest write lost.");
    Console.WriteLine("PASS: concurrent writes, rotation, retention and no NUL-filled gaps.");
}
finally { Directory.Delete(dir, recursive: true); }
