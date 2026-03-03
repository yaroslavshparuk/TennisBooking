using System.Net;

namespace TennisBooking.Tests.Helpers;

/// <summary>
/// A mock handler that works with HttpClientHandler's CookieContainer
/// by simulating Set-Cookie headers from Skedda API responses.
/// </summary>
public class MockCookieHttpMessageHandler : DelegatingHandler
{
    private readonly Queue<(HttpStatusCode Status, string Content, Dictionary<string, string>? Cookies)> _responses = new();
    private readonly List<HttpRequestMessage> _requests = new();

    public IReadOnlyList<HttpRequestMessage> Requests => _requests;

    public void EnqueueResponse(HttpStatusCode statusCode, string content, Dictionary<string, string>? cookies = null)
    {
        _responses.Enqueue((statusCode, content, cookies));
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Add(request);

        if (_responses.Count == 0)
            throw new InvalidOperationException($"No more mock responses queued. Request: {request.Method} {request.RequestUri}");

        var (status, content, cookies) = _responses.Dequeue();

        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(content, System.Text.Encoding.UTF8, "text/html"),
            RequestMessage = request
        };

        if (cookies != null)
        {
            foreach (var cookie in cookies)
            {
                response.Headers.TryAddWithoutValidation("Set-Cookie", $"{cookie.Key}={cookie.Value}; Path=/");
            }
        }

        return Task.FromResult(response);
    }
}
