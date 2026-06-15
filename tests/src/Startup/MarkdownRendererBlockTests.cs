using RuneshapePriceChecker.App.Dashboard;
using Xunit;

namespace RuneshapePriceChecker.Tests.Dashboard;

public class MarkdownRendererBlockTests
{
    [Fact]
    public void Render_Heading_ProducesNonEmptyBlocks()
    {
        StaTestHelper.RunOnStaThread(() =>
        {
            var doc = MarkdownRenderer.Render("## Title");
            Assert.NotEmpty(doc.Blocks);
        });
    }

    [Fact]
    public void Render_BulletList_ProducesBlocks()
    {
        StaTestHelper.RunOnStaThread(() =>
        {
            var doc = MarkdownRenderer.Render("- A\n- B\n- C");
            Assert.NotEmpty(doc.Blocks);
        });
    }

    [Fact]
    public void Render_CodeBlock_ProducesBlocks()
    {
        StaTestHelper.RunOnStaThread(() =>
        {
            var doc = MarkdownRenderer.Render("```\ncode\n```");
            Assert.NotEmpty(doc.Blocks);
        });
    }
}