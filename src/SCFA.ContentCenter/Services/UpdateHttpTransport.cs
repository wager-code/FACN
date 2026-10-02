using System.Net;
using System.Net.Http;

namespace SCFA.ContentCenter.Services;

// Keep redirect validation and a single headers/body deadline shared by both update endpoints.
internal static class UpdateHttpTransport
{
    private const int MaxRedirects = 5;

    internal static async Task<T> ReadAsync<T>(Uri initial, Func<Uri, HttpClient> createClient,
        Func<Uri, bool> allowRedirect, TimeSpan timeout,
        Func<HttpResponseMessage, CancellationToken, Task<T>> read, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            var current = initial;
            for (var redirects = 0; ; redirects++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                using var client = createClient(current);
                using var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                    HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (redirects >= MaxRedirects) throw new InvalidDataException("更新下载跳转次数超过安全上限");
                    var location = response.Headers.Location ?? throw new InvalidDataException("更新下载跳转缺少目标地址");
                    var target = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (target.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(target.UserInfo))
                        throw new InvalidDataException("更新跳转目标必须是无用户信息的 HTTPS 地址");
                    if (!allowRedirect(target)) throw new InvalidDataException("固定证书的更新地址不能跳转到其他主机");
                    current = target;
                    continue;
                }
                response.EnsureSuccessStatusCode();
                var result = await read(response, deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                return result;
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("更新请求超时，请稍后重试", ex);
        }
    }
}
