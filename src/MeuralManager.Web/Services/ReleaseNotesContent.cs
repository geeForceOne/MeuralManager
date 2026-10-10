using Markdig;

namespace MeuralManager.Web.Services;

// The release notes page's content: the repository's CHANGELOG.md, embedded into the app at build
// time (see the .csproj) so a running instance shows the notes up to its own version, with no
// network access. The same file's sections are what a GitHub release's body is taken from, so the
// two can't drift apart. Rendered once; raw HTML in the file is escaped, not passed through.
public static class ReleaseNotesContent
{
    private static readonly Lazy<string> RenderedHtml = new(Render);

    public static string Html => RenderedHtml.Value;

    private static string Render()
    {
        using var stream = typeof(ReleaseNotesContent).Assembly.GetManifestResourceStream("CHANGELOG.md");
        if (stream is null)
            return "<p>No release notes are included in this build.</p>";

        using var reader = new StreamReader(stream);
        var markdown = reader.ReadToEnd();
        // The file's own "# Release notes" title would repeat the page's heading.
        if (markdown.StartsWith("# ", StringComparison.Ordinal))
            markdown = markdown[(markdown.IndexOf('\n') + 1)..];

        var pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();
        return Markdown.ToHtml(markdown, pipeline);
    }
}
