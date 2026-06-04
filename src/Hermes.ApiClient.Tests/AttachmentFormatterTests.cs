using System;
using Hermes.ApiClient;
using Xunit;

namespace Hermes.ApiClient.Tests;

public sealed class AttachmentFormatterTests
{
    private static AttachmentReadResult File(string name, string path, long size = 1024)
        => AttachmentReadResult.File(path, name, size);

    private static AttachmentReadResult Img(string name, string path, long size = 12345)
        => AttachmentReadResult.Image(path, name, size);

    private static AttachmentReadResult Err(string name)
        => AttachmentReadResult.Error("/tmp/" + name, "Access denied");

    [Fact]
    public void BuildMessage_text_only_returns_text_unchanged()
    {
        Assert.Equal("hello", AttachmentFormatter.BuildMessage("hello", Array.Empty<AttachmentReadResult>()));
    }

    [Fact]
    public void BuildMessage_trims_trailing_whitespace_from_text()
    {
        Assert.Equal("hello", AttachmentFormatter.BuildMessage("hello   \n\n", Array.Empty<AttachmentReadResult>()));
    }

    [Fact]
    public void BuildMessage_returns_empty_when_text_and_attachments_empty()
    {
        Assert.Equal("", AttachmentFormatter.BuildMessage("", Array.Empty<AttachmentReadResult>()));
        Assert.Equal("", AttachmentFormatter.BuildMessage("   ", new[] { Err("e.txt") }));
    }

    [Fact]
    public void BuildMessage_image_with_prose_appends_path_on_its_own_line()
    {
        var r = AttachmentFormatter.BuildMessage("what's in this pic?",
            new[] { Img("foo.png", @"C:\Users\me\foo.png") });
        Assert.Equal("what's in this pic?\n\nC:\\Users\\me\\foo.png", r);
    }

    [Fact]
    public void BuildMessage_image_only_no_prose_uses_stand_in_then_path()
    {
        var r = AttachmentFormatter.BuildMessage(null,
            new[] { Img("foo.png", "/tmp/foo.png") });
        Assert.StartsWith("I'm attaching a file:", r);
        Assert.EndsWith("/tmp/foo.png", r);
    }

    [Fact]
    public void BuildMessage_file_with_prose_appends_path_not_contents()
    {
        var r = AttachmentFormatter.BuildMessage("summarize this",
            new[] { File("notes.md", @"C:\notes.md") });
        Assert.Equal("summarize this\n\nC:\\notes.md", r);
        // CRITICAL: we DO NOT inline the file contents. The user reported
        // a bug where attaching a JSON file pasted the entire body as the
        // message — this assertion guards against the regression.
        Assert.DoesNotContain("```", r);
        Assert.DoesNotContain("--- attached", r);
    }

    [Fact]
    public void BuildMessage_multiple_attachments_one_path_per_line()
    {
        var r = AttachmentFormatter.BuildMessage("look at these",
            new[] {
                File("a.md", "/a.md"),
                Img("b.png", "/b.png"),
                File("c.json", "/c.json"),
            });
        Assert.Equal("look at these\n\n/a.md\n/b.png\n/c.json", r);
    }

    [Fact]
    public void BuildMessage_skips_error_attachments()
    {
        var r = AttachmentFormatter.BuildMessage("hi",
            new[] { Err("broken.txt"), File("ok.md", "/ok.md") });
        Assert.DoesNotContain("broken.txt", r);
        Assert.Contains("/ok.md", r);
    }

    [Fact]
    public void BuildMessage_no_prose_multiple_attachments_pluralizes()
    {
        var r = AttachmentFormatter.BuildMessage(null,
            new[] { Img("a.png", "/a.png"), File("b.txt", "/b.txt") });
        Assert.StartsWith("I'm attaching 2 files:", r);
        Assert.Contains("/a.png", r);
        Assert.Contains("/b.txt", r);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1024L * 1024, "1 MB")]
    [InlineData(2L * 1024 * 1024 + 512 * 1024, "2.5 MB")]
    public void FormatSize_renders_human_readable(long bytes, string expected)
    {
        Assert.Equal(expected, AttachmentFormatter.FormatSize(bytes));
    }
}
