using System.Text;

namespace AutoJev.Local;

// The application owns the file handle and closes it before renaming.
// External copy/truncate rotation is unsafe with buffered FileStream offsets.
internal sealed class RollingLogWriter : TextWriter
{
    private readonly string path;
    private readonly long maxBytes;
    private readonly int retained;
    private readonly object sync = new();
    private StreamWriter writer;
    public override Encoding Encoding => Encoding.UTF8;

    public RollingLogWriter(string path, long maxBytes = 20 * 1024 * 1024, int retained = 7)
    {
        this.path = path;
        this.maxBytes = maxBytes;
        this.retained = retained;
        writer = Open();
    }

    private StreamWriter Open() => new(path, append: true, new UTF8Encoding(false)) { AutoFlush = true };

    private void Rotate()
    {
        if (writer.BaseStream.Length < maxBytes) return;
        writer.Dispose();
        File.Delete(path + "." + retained);
        for (var i = retained - 1; i >= 1; i--)
            if (File.Exists(path + "." + i)) File.Move(path + "." + i, path + "." + (i + 1));
        File.Move(path, path + ".1");
        writer = Open();
    }

    public override void Write(char value) { lock (sync) { Rotate(); writer.Write(value); } }
    public override void Write(string? value) { lock (sync) { Rotate(); writer.Write(value); } }
    public override void WriteLine(string? value) { lock (sync) { Rotate(); writer.WriteLine(value); } }
    public override void Flush() { lock (sync) writer.Flush(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { lock (sync) writer.Dispose(); }
        base.Dispose(disposing);
    }
}
