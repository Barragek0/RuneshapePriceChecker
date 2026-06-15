using System.Globalization;
using System.Windows.Documents;
using RuneshapePriceChecker.App.Dashboard;
using Xunit;

namespace RuneshapePriceChecker.Tests.Dashboard;

public class MarkdownRendererTests
{
    [Fact]
    public void Render_EmptyString_ReturnsFlowDocument()
        => StaTestHelper.RunOnStaThread(() =>
        {
            var doc = MarkdownRenderer.Render("");
            Assert.NotNull(doc);
            Assert.IsType<FlowDocument>(doc);
        });

    [Fact]
    public void Render_PlainText_ReturnsDocumentWithParagraph()
        => StaTestHelper.RunOnStaThread(() =>
        {
            var doc = MarkdownRenderer.Render("Hello world");
            Assert.NotNull(doc);
            Assert.NotEmpty(doc.Blocks);
        });

    [Fact]
    public void Render_Heading_ParsesCorrectly()
        => StaTestHelper.RunOnStaThread(() =>
        {
            var doc = MarkdownRenderer.Render("## Heading Two");
            Assert.NotNull(doc);
            Assert.NotEmpty(doc.Blocks);
        });

    [Fact]
    public void Render_BoldText_ParsesCorrectly()
        => StaTestHelper.RunOnStaThread(() =>
        {
            var doc = MarkdownRenderer.Render("This is **bold** text");
            Assert.NotNull(doc);
            Assert.NotEmpty(doc.Blocks);
        });

    [Fact]
    public void Render_InlineCode_ParsesCorrectly()
        => StaTestHelper.RunOnStaThread(() =>
        {
            var doc = MarkdownRenderer.Render("Use `dotnet build` to compile");
            Assert.NotNull(doc);
            Assert.NotEmpty(doc.Blocks);
        });

    [Fact]
    public void Render_Blockquote_ParsesCorrectly()
        => StaTestHelper.RunOnStaThread(() =>
        {
            var doc = MarkdownRenderer.Render("> This is a quote");
            Assert.NotNull(doc);
            Assert.NotEmpty(doc.Blocks);
        });

    [Fact]
    public void Render_MixedContent_DoesNotThrow()
        => StaTestHelper.RunOnStaThread(() =>
        {
            var markdown = """
            ## Features

            - **Item 1**: Description with `code`
            - **Item 2**: Another description

            > Note: This is important
            """;

            var doc = MarkdownRenderer.Render(markdown);
            Assert.NotNull(doc);
            Assert.NotEmpty(doc.Blocks);
        });

    [Fact]
    public void Render_RealChangelog_DoesNotCrash()
        => StaTestHelper.RunOnStaThread(() =>
        {
            var changelog = """
            ## What's New in v1.0.0

            ### 🚀 Features

            - **Auto-update improvements** — More reliable update process
            - **Performance optimizations** — Reduced CPU and memory usage

            ### 🐛 Bug Fixes

            - Fixed overlay click-through issue with `WS_EX_TRANSPARENT`
            - Fixed settings validation for threshold values

            > This is a **major release** with significant changes.
            """;

            var doc = MarkdownRenderer.Render(changelog);
            Assert.NotNull(doc);
            Assert.NotEmpty(doc.Blocks);
        });

    [Fact]
    public void Render_VeryLongMarkdown_DoesNotCrash()
        => StaTestHelper.RunOnStaThread(() =>
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < 200; i++)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- Item {i}: Some description text here");

            var doc = MarkdownRenderer.Render(sb.ToString());
            Assert.NotNull(doc);
            Assert.NotEmpty(doc.Blocks);
        });
}
