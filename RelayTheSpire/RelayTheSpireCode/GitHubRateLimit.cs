using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RelayTheSpire;

/// <summary>
/// Remembers GitHub's rate-limit headers from the most recent response. Static because every transport
/// shares one token, and so one budget. The room browser checks <see cref="IsLow"/> before fetching, so
/// browsing can never use up the requests the player needs to take and pass turns.
/// </summary>
public static class GitHubRateLimit
{
    /// <summary>Browsing stops when fewer than this many requests are left in the hour.</summary>
    public const int Reserve = 300;

    private static readonly object Gate = new();
    private static int  _remaining = -1; // -1 = not seen yet
    private static long _resetAt;

    public static void Update(HttpResponseMessage response)
    {
        int?  remaining = TryHeader(response, "X-RateLimit-Remaining") is { } r && int.TryParse(r, out var ri) ? ri : null;
        long? reset     = TryHeader(response, "X-RateLimit-Reset")     is { } t && long.TryParse(t, out var ti) ? ti : null;

        lock (Gate)
        {
            if (remaining is { } a) _remaining = a;
            if (reset is { } b)     _resetAt   = b;
        }
    }

    private static string? TryHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>True when the hourly budget is nearly spent (and has not reset yet).</summary>
    public static bool IsLow
    {
        get { lock (Gate) return _remaining >= 0 && _remaining < Reserve && RelayManifest.NowUnix() < _resetAt; }
    }

    public static bool IsExhausted
    {
        get { lock (Gate) return _remaining == 0 && RelayManifest.NowUnix() < _resetAt; }
    }

    private static string Reset()
    {
        long minutes;
        lock (Gate) minutes = (_resetAt - RelayManifest.NowUnix() + 59) / 60;
        return minutes > 0 ? $"It resets in ~{minutes} min." : "Try again in a few minutes.";
    }

    public static string DescribeLow()
    {
        int left;
        lock (Gate) left = _remaining;
        return $"Only {left} GitHub requests are left this hour, so the room browser is paused to keep " +
               $"your runs working. {Reset()}";
    }

    public static string DescribeExhausted() => $"GitHub's rate limit was reached. {Reset()}";
}

/// <summary>Reads the rate-limit headers off every response the transport receives.</summary>
public sealed class RateLimitHandler : DelegatingHandler
{
    public RateLimitHandler() : base(new HttpClientHandler()) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        GitHubRateLimit.Update(response);
        return response;
    }
}

/// <summary>GitHub said no because the hourly (or secondary) rate limit was hit. The message is player-ready.</summary>
public class RelayRateLimitException : RelayTransportException
{
    public RelayRateLimitException(string message) : base(message) { }
}
