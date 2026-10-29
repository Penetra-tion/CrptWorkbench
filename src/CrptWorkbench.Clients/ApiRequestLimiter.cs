using CrptWorkbench.Domain;

namespace CrptWorkbench.Clients;

public sealed class ApiRequestLimiter : IApiRequestLimiter
{
    public static ApiRequestLimiter Shared { get; } = new();
    private readonly SemaphoreSlim[] gates = Enum.GetValues<ApiConcurrencyService>().Select(_ => new SemaphoreSlim(4, 4)).ToArray();
    private readonly object _timingGate = new();
    private readonly long[] _nextStart = new long[Enum.GetValues<ApiConcurrencyService>().Length];
    private readonly long[] _blockedUntil = new long[Enum.GetValues<ApiConcurrencyService>().Length];

    public async ValueTask<IDisposable> AcquireAsync(ApiConcurrencyService service, CancellationToken cancellationToken = default)
    {
        var gate = gates[(int)service];
        await gate.WaitAsync(cancellationToken);
        try
        {
            while (true)
            {
                TimeSpan delay;
                lock (_timingGate)
                {
                    var now = Environment.TickCount64;
                    var due = Math.Max(_nextStart[(int)service], _blockedUntil[(int)service]);
                    if (due <= now)
                    {
                        // True API v733 common participant limit: 50 starts/second.
                        // One service-wide schedule also covers auth and background work.
                        _nextStart[(int)service] = now + (service == ApiConcurrencyService.TrueApi ? 20 : 0);
                        return new Slot(gate);
                    }
                    delay = TimeSpan.FromMilliseconds(Math.Min(due - now, 60_000));
                }
                await Task.Delay(delay, cancellationToken);
            }
        }
        catch { gate.Release(); throw; }
    }

    public void Defer(ApiConcurrencyService service, TimeSpan delay)
    {
        var milliseconds = Math.Max(0, Math.Min(delay.TotalMilliseconds, long.MaxValue / 2d));
        lock (_timingGate)
            _blockedUntil[(int)service] = Math.Max(_blockedUntil[(int)service], Environment.TickCount64 + (long)milliseconds);
    }

    private sealed class Slot(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? owned = gate;
        public void Dispose() => Interlocked.Exchange(ref owned, null)?.Release();
    }
}

// Hold the slot through streamed response consumption as well as buffered responses.
public sealed class ApiConcurrencyHandler(IApiRequestLimiter limiter, ApiConcurrencyService service) : DelegatingHandler
{
    public static readonly HttpRequestOptionsKey<Func<CancellationToken, Task>> BeforeDispatch = new("Workbench.BeforeDispatch" );
    public static readonly HttpRequestOptionsKey<Func<CancellationToken, Task<bool>>> CanDispatch = new("Workbench.CanDispatch");
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Options.TryGetValue(CanDispatch, out var canDispatch);
        request.Options.TryGetValue(BeforeDispatch, out var beforeDispatch);
        var slot = await ApiDispatchWait.AcquireAsync(limiter, service, canDispatch, beforeDispatch, cancellationToken);
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                limiter.Defer(service, ApiRateLimitDelay.ForRetry(response.Headers
                    .ToDictionary(x => x.Key, x => string.Join(", ", x.Value), StringComparer.OrdinalIgnoreCase), 0));
            response.Content = new LimitedContent(response.Content, slot);
            return response;
        }
        catch { slot.Dispose(); throw; }
    }

    private sealed class LimitedContent : HttpContent
    {
        private readonly HttpContent inner;
        private readonly IDisposable slot;
        public LimitedContent(HttpContent inner, IDisposable slot)
        {
            this.inner = inner;
            this.slot = slot;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            try { await inner.CopyToAsync(stream); }
            finally { slot.Dispose(); }
        }
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken)
        {
            try { await inner.CopyToAsync(stream, cancellationToken); }
            finally { slot.Dispose(); }
        }
        protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            try { return new LimitedStream(await inner.ReadAsStreamAsync(cancellationToken), slot); }
            catch { slot.Dispose(); throw; }
        }
        protected override bool TryComputeLength(out long length)
        { length = inner.Headers.ContentLength ?? 0; return inner.Headers.ContentLength.HasValue; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { inner.Dispose(); } finally { slot.Dispose(); } }
            base.Dispose(disposing);
        }
    }

    private sealed class LimitedStream(Stream inner, IDisposable slot) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            try { var read = inner.Read(buffer, offset, count); if (read == 0 && count > 0) slot.Dispose(); return read; }
            catch { slot.Dispose(); throw; }
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { var read = await inner.ReadAsync(buffer, cancellationToken); if (read == 0 && buffer.Length > 0) slot.Dispose(); return read; }
            catch { slot.Dispose(); throw; }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { inner.Dispose(); } finally { slot.Dispose(); } }
            base.Dispose(disposing);
        }
    }
}
