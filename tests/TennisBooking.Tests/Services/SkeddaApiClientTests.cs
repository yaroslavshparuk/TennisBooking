using FluentAssertions;
using TennisBooking.Services;

namespace TennisBooking.Tests.Services;

public class SkeddaApiClientTests
{
    [Fact]
    public void GetRequestVerificationToken_ValidHtml_ReturnsToken()
    {
        var html = """
            <html>
            <body>
                <input name="__RequestVerificationToken" type="hidden" value="my-csrf-token-abc123" />
            </body>
            </html>
            """;

        var token = SkeddaApiClient.GetRequestVerificationToken(html);

        token.Should().Be("my-csrf-token-abc123");
    }

    [Fact]
    public void GetRequestVerificationToken_SelfClosingTag_ReturnsToken()
    {
        var html = """<input name="__RequestVerificationToken" type="hidden" value="token-456"/>""";

        var token = SkeddaApiClient.GetRequestVerificationToken(html);

        token.Should().Be("token-456");
    }

    [Fact]
    public void GetRequestVerificationToken_NoToken_ThrowsInvalidOperationException()
    {
        var html = "<html><body>No token here</body></html>";

        var act = () => SkeddaApiClient.GetRequestVerificationToken(html);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*__RequestVerificationToken*not found*");
    }

    [Fact]
    public void GetRequestVerificationToken_EmptyHtml_ThrowsInvalidOperationException()
    {
        var act = () => SkeddaApiClient.GetRequestVerificationToken(string.Empty);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void GetRequestVerificationToken_CaseInsensitive_ReturnsToken()
    {
        var html = """<INPUT NAME="__RequestVerificationToken" TYPE="hidden" VALUE="case-insensitive-token" />""";

        var token = SkeddaApiClient.GetRequestVerificationToken(html);

        token.Should().Be("case-insensitive-token");
    }
}
