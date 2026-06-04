using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Hermes.ApiClient;
using Xunit;

namespace Hermes.ApiClient.Tests;

public sealed class AttachmentReaderTests : IDisposable
{
    private readonly string _scratch;

    public AttachmentReaderTests()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "hermes-attach-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    private string Write(string name, string content)
    {
        var p = Path.Combine(_scratch, name);
        File.WriteAllText(p, content, new UTF8Encoding(false));
        return p;
    }

    private string WriteBytes(string name, byte[] bytes)
    {
        var p = Path.Combine(_scratch, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    [Fact]
    public async Task ReadAsync_text_file_returns_file_kind_with_path_and_size()
    {
        var path = Write("hello.txt", "hello world\nline 2\n");
        var r = await AttachmentReader.ReadAsync(path);
        Assert.Equal(AttachmentKind.File, r.Kind);
        Assert.True(r.IsSuccess);
        Assert.Equal("hello.txt", r.FileName);
        Assert.Equal(path, r.Path);
        Assert.True(r.Size > 0);
        Assert.Null(r.ErrorMessage);
    }

    [Fact]
    public async Task ReadAsync_arbitrary_binary_returns_file_kind_not_error()
    {
        // No magic-byte image header -> File kind. We no longer reject
        // "binary" files; the agent decides what to do with the path.
        var path = WriteBytes("data.bin", new byte[] { 0x89, 0x50, 0x00, 0x4E, 0x47, 0x01, 0x02 });
        var r = await AttachmentReader.ReadAsync(path);
        Assert.Equal(AttachmentKind.File, r.Kind);
        Assert.True(r.IsSuccess);
    }

    [Fact]
    public async Task ReadAsync_missing_file_returns_error()
    {
        var path = Path.Combine(_scratch, "does-not-exist.txt");
        var r = await AttachmentReader.ReadAsync(path);
        Assert.Equal(AttachmentKind.Error, r.Kind);
        Assert.False(r.IsSuccess);
        Assert.Contains("not found", r.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_empty_path_returns_error()
    {
        var r = await AttachmentReader.ReadAsync("");
        Assert.Equal(AttachmentKind.Error, r.Kind);
    }

    [Fact]
    public void IsKnownImage_recognises_png_jpeg_gif_webp_bmp()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01 };
        var jpg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        var gif87 = Encoding.ASCII.GetBytes("GIF87a...");
        var gif89 = Encoding.ASCII.GetBytes("GIF89a...");
        var webp = new byte[12] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 };
        var bmp = new byte[] { 0x42, 0x4D, 0, 0, 0, 0 };

        Assert.True(AttachmentReader.IsKnownImage(png));
        Assert.True(AttachmentReader.IsKnownImage(jpg));
        Assert.True(AttachmentReader.IsKnownImage(gif87));
        Assert.True(AttachmentReader.IsKnownImage(gif89));
        Assert.True(AttachmentReader.IsKnownImage(webp));
        Assert.True(AttachmentReader.IsKnownImage(bmp));

        Assert.False(AttachmentReader.IsKnownImage(Encoding.UTF8.GetBytes("hello world")));
        Assert.False(AttachmentReader.IsKnownImage(new byte[] { 0x00, 0x00, 0x00, 0x00 }));
    }

    [Fact]
    public async Task ReadAsync_png_returns_image_kind()
    {
        var path = WriteBytes("screenshot.png",
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xDE, 0xAD, 0xBE, 0xEF });
        var r = await AttachmentReader.ReadAsync(path);
        Assert.Equal(AttachmentKind.Image, r.Kind);
        Assert.True(r.IsImage);
        Assert.True(r.IsSuccess);
        Assert.False(r.IsFile);
    }

    [Fact]
    public async Task ReadAsync_jpeg_returns_image_kind()
    {
        var path = WriteBytes("photo.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 });
        var r = await AttachmentReader.ReadAsync(path);
        Assert.Equal(AttachmentKind.Image, r.Kind);
    }

    [Fact]
    public async Task ReadAsync_tiny_file_under_magic_window_still_classifies()
    {
        // A 4-byte file is shorter than our sniff window but should still
        // read cleanly and come back as File (no image magic matched).
        var path = WriteBytes("tiny.dat", new byte[] { 0x01, 0x02, 0x03, 0x04 });
        var r = await AttachmentReader.ReadAsync(path);
        Assert.Equal(AttachmentKind.File, r.Kind);
        Assert.Equal(4, r.Size);
    }
}
