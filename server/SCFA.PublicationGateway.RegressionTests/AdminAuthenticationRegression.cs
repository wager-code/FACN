using Microsoft.AspNetCore.Http;
using System.Net;
using System.Text;
using SCFA.PublicationGateway;

internal static class AdminAuthenticationRegression
{
    internal static async Task RunAsync(GatewayOptions options)
    {
        var failures = new List<string>();
        void Check(bool pass, string name)
        {
            Console.WriteLine((pass ? "PASS  " : "FAIL  ") + name);
            if (!pass) failures.Add(name);
        }
        DefaultHttpContext Context()
        {
            var context = new DefaultHttpContext();
            context.Request.Headers.Authorization = "Bearer fixture-token-not-real";
            return context;
        }
        foreach (var bodyOnly in new[] { false, true })
        {
            using var http = new HttpClient(bodyOnly ? new HangingCosBodyHandler(HttpStatusCode.OK) : new HangingCosHandler())
                { Timeout = Timeout.InfiniteTimeSpan };
            var auth = new AdminAuthenticator(options, http, TimeSpan.FromMilliseconds(50));
            using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var bounded = false;
            try { await auth.VerifyAsync(Context(), safety.Token); }
            catch (HttpRequestException ex) { bounded = ex.StatusCode == HttpStatusCode.GatewayTimeout && !safety.IsCancellationRequested; }
            catch (OperationCanceledException) { }
            Check(bounded, bodyOnly ? "管理员身份校验的正文停滞有独立总期限" : "管理员身份校验响应头停滞有独立总期限");
        }
        using (var http = new HttpClient(new HangingCosBodyHandler(HttpStatusCode.OK)) { Timeout = Timeout.InfiniteTimeSpan })
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30)))
        {
            var auth = new AdminAuthenticator(options, http, TimeSpan.FromSeconds(1));
            var canceled = false;
            try { await auth.VerifyAsync(Context(), cancellation.Token); }
            catch (OperationCanceledException) { canceled = cancellation.IsCancellationRequested; }
            Check(canceled, "管理员身份校验保留调用方取消语义");
        }
        foreach (var chunked in new[] { false, true })
        {
            var body = "{\"user\":{\"id\":\"fixture-user\",\"role_key\":\"admin\",\"status\":\"active\"},\"padding\":\"" + new string('x', 70000) + "\"}";
            using var http = new HttpClient(new JsonAuthHandler(body, chunked));
            var auth = new AdminAuthenticator(options, http, TimeSpan.FromSeconds(1));
            Check(await auth.VerifyAsync(Context(), CancellationToken.None) is null,
                chunked ? "管理员身份校验拒绝无Content-Length的超大正文" : "管理员身份校验拒绝声明超大的正文");
        }
        foreach (var (role, status, accepted) in new[] { ("admin", "active", true), ("super_admin", "active", true),
                     ("user", "active", false), ("admin", "disabled", false) })
        {
            var body = "{\"user\":{\"id\":\"fixture-user\",\"role_key\":\"" + role + "\",\"status\":\"" + status + "\"}}";
            using var http = new HttpClient(new JsonAuthHandler(body, true));
            var auth = new AdminAuthenticator(options, http, TimeSpan.FromSeconds(1));
            Check((await auth.VerifyAsync(Context(), CancellationToken.None) is not null) == accepted,
                "管理员身份校验保持角色和账号状态规则：" + role + "/" + status);
        }
        using (var http = new HttpClient(new JsonAuthHandler("not-json", true)))
        {
            var auth = new AdminAuthenticator(options, http, TimeSpan.FromSeconds(1));
            var rejected = false;
            try { rejected = await auth.VerifyAsync(Context(), CancellationToken.None) is null; }
            catch (System.Text.Json.JsonException) { }
            Check(rejected, "管理员身份校验对无效JSON拒绝授权");
        }
        if (failures.Count > 0) throw new InvalidOperationException("Admin auth regressions failed: " + string.Join("; ", failures));
    }

    private sealed class JsonAuthHandler(string body, bool chunked) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            HttpContent content = chunked ? new StreamContent(new ForwardOnlyStream(Encoding.UTF8.GetBytes(body))) : new StringContent(body);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
    private sealed class ForwardOnlyStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _input = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _input.ReadAsync(buffer, ct);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _input.Dispose(); base.Dispose(disposing); }
    }
}