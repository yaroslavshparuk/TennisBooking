namespace TennisBooking.Tests.Helpers;

public class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    private readonly List<HttpRequestMessage> _requests = new();

    public IReadOnlyList<HttpRequestMessage> Requests => _requests;

    public void EnqueueResponse(HttpResponseMessage response)
    {
        _responses.Enqueue(response);
    }

    public void EnqueueResponse(HttpStatusCode statusCode, string content = "", string mediaType = "application/json")
    {
        _responses.Enqueue(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, System.Text.Encoding.UTF8, mediaType)
        });
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Add(request);

        if (_responses.Count == 0)
            throw new InvalidOperationException($"No more mock responses queued. Request: {request.Method} {request.RequestUri}");

        return Task.FromResult(_responses.Dequeue());
    }
}
