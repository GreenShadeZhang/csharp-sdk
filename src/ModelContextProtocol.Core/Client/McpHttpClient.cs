using ModelContextProtocol.Protocol;
using System.Diagnostics;

#if NET
using System.Net.Http.Json;
#else
using System.Text;
using System.Text.Json;
#endif

namespace ModelContextProtocol.Client;

internal class McpHttpClient(HttpClient httpClient, bool omitContentTypeCharset = false)
{
    internal virtual async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, JsonRpcMessage? message, CancellationToken cancellationToken)
    {
        Debug.Assert(request.Content is null, "The request body should only be supplied as a JsonRpcMessage");
        Debug.Assert(message is null || request.Method == HttpMethod.Post, "All messages should be sent in POST requests.");

        using var content = CreatePostBodyContent(message);
        request.Content = content;
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    private HttpContent? CreatePostBodyContent(JsonRpcMessage? message)
    {
        if (message is null)
        {
            return null;
        }

#if NET
        var content = JsonContent.Create(message, McpJsonUtilities.JsonContext.Default.JsonRpcMessage);
        if (omitContentTypeCharset && content.Headers.ContentType is not null)
        {
            // Remove charset parameter to support servers that reject "application/json; charset=utf-8"
            content.Headers.ContentType.CharSet = null;
        }
        return content;
#else
        var json = JsonSerializer.Serialize(message, McpJsonUtilities.JsonContext.Default.JsonRpcMessage);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        if (omitContentTypeCharset && content.Headers.ContentType is not null)
        {
            // Remove charset parameter to support servers that reject "application/json; charset=utf-8"
            content.Headers.ContentType.CharSet = null;
        }
        return content;
#endif
    }
}
