using System;
using System.Collections.Concurrent;

namespace FoldEngine
{
    /// <summary>
    /// 每玩家令牌桶：容量 5，每秒补充 5 个令牌。
    /// 服务器控制台（SenderId 为空）不限制。
    /// </summary>
    internal static class RateLimiter
    {
        private const int Capacity = 5;
        private const double RefillPerSecond = 5.0;
        private const int MaxBuckets = 2048;

        private static readonly ConcurrentDictionary<string, Bucket> Buckets =
            new ConcurrentDictionary<string, Bucket>(StringComparer.Ordinal);

        public static bool TryAcquire(string playerId)
        {
            if (string.IsNullOrEmpty(playerId))
                return true;

            if (Buckets.Count > MaxBuckets)
                Prune();

            Bucket bucket = Buckets.GetOrAdd(playerId, _ => new Bucket());
            return bucket.TryAcquire();
        }

        private static void Prune()
        {
            long now = NowMs();
            foreach (var pair in Buckets)
            {
                if (now - pair.Value.LastSeenMs > 60_000)
                    Buckets.TryRemove(pair.Key, out _);
            }
        }

        private static long NowMs()
        {
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        private sealed class Bucket
        {
            private readonly object _gate = new object();
            private double _tokens = Capacity;
            private long _lastRefillMs = NowMs();

            public long LastSeenMs { get; private set; } = NowMs();

            public bool TryAcquire()
            {
                lock (_gate)
                {
                    long now = NowMs();
                    LastSeenMs = now;

                    double elapsedSeconds = (now - _lastRefillMs) / 1000.0;
                    _tokens = Math.Min(Capacity, _tokens + elapsedSeconds * RefillPerSecond);
                    _lastRefillMs = now;

                    if (_tokens < 1.0)
                        return false;

                    _tokens -= 1.0;
                    return true;
                }
            }
        }
    }
}
