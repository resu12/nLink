using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NLink.Core.Metrics;

public sealed class MetricsRegistry
{
    private readonly ConcurrentDictionary<MetricKey, CounterMetric> counters = new();
    private readonly ConcurrentDictionary<MetricKey, GaugeMetric> gauges = new();
    private readonly ConcurrentDictionary<MetricKey, HistogramMetric> histograms = new();
    private readonly double[] defaultHistogramBuckets;

    public MetricsRegistry(double[]? defaultHistogramBuckets = null)
    {
        this.defaultHistogramBuckets = NormalizeBuckets(defaultHistogramBuckets) ?? new[] { 1d, 5d, 10d, 25d, 50d, 100d, 250d, 500d, 1000d, 5000d };
    }

    public CounterHandle Counter(
        string name,
        string? transport = null,
        string? scenario = null,
        string? result = null,
        string? failureCategory = null,
        string? bridgeReuseMode = null)
    {
        var key = new MetricKey(name, MetricTags.Create(transport, scenario, result, failureCategory, bridgeReuseMode));
        var metric = counters.GetOrAdd(key, static k => new CounterMetric(k.Name, k.Tags));
        return new CounterHandle(metric);
    }

    public GaugeHandle Gauge(
        string name,
        string? transport = null,
        string? scenario = null,
        string? result = null,
        string? failureCategory = null,
        string? bridgeReuseMode = null)
    {
        var key = new MetricKey(name, MetricTags.Create(transport, scenario, result, failureCategory, bridgeReuseMode));
        var metric = gauges.GetOrAdd(key, static k => new GaugeMetric(k.Name, k.Tags));
        return new GaugeHandle(metric);
    }

    public HistogramHandle Histogram(
        string name,
        double[]? buckets = null,
        string? transport = null,
        string? scenario = null,
        string? result = null,
        string? failureCategory = null,
        string? bridgeReuseMode = null)
    {
        var key = new MetricKey(name, MetricTags.Create(transport, scenario, result, failureCategory, bridgeReuseMode));
        var bucketSet = NormalizeBuckets(buckets) ?? defaultHistogramBuckets;
        var metric = histograms.GetOrAdd(
            key,
            static (k, state) => new HistogramMetric(k.Name, k.Tags, state),
            bucketSet);
        return new HistogramHandle(metric);
    }

    public MetricsSnapshot Snapshot()
    {
        var counterDtos = counters.Values
            .Select(static m => m.Snapshot())
            .OrderBy(static m => m.Name, StringComparer.Ordinal)
            .ThenBy(static m => m.TagsKey, StringComparer.Ordinal)
            .ToArray();

        var gaugeDtos = gauges.Values
            .Select(static m => m.Snapshot())
            .OrderBy(static m => m.Name, StringComparer.Ordinal)
            .ThenBy(static m => m.TagsKey, StringComparer.Ordinal)
            .ToArray();

        var histogramDtos = histograms.Values
            .Select(static m => m.Snapshot())
            .OrderBy(static m => m.Name, StringComparer.Ordinal)
            .ThenBy(static m => m.TagsKey, StringComparer.Ordinal)
            .ToArray();

        return new MetricsSnapshot(counterDtos, gaugeDtos, histogramDtos);
    }

    public string ExportJson(bool indented = false)
    {
        var snapshot = Snapshot();
        return JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
        {
            WriteIndented = indented,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        });
    }

    private static double[]? NormalizeBuckets(double[]? buckets)
    {
        if (buckets is null || buckets.Length == 0)
        {
            return null;
        }

        return buckets
            .Where(static b => !double.IsNaN(b) && !double.IsInfinity(b))
            .Distinct()
            .OrderBy(static b => b)
            .ToArray();
    }

    public readonly struct CounterHandle
    {
        private readonly CounterMetric metric;

        internal CounterHandle(CounterMetric metric) => this.metric = metric;

        public void Inc(long by = 1) => metric.Inc(by);
    }

    public readonly struct GaugeHandle
    {
        private readonly GaugeMetric metric;

        internal GaugeHandle(GaugeMetric metric) => this.metric = metric;

        public void Set(double value) => metric.Set(value);
    }

    public readonly struct HistogramHandle
    {
        private readonly HistogramMetric metric;

        internal HistogramHandle(HistogramMetric metric) => this.metric = metric;

        public void Observe(double value) => metric.Observe(value);
    }

    private readonly record struct MetricKey(string Name, MetricTags Tags);

    internal readonly record struct MetricTags(
        string Transport,
        string Scenario,
        string Result,
        string FailureCategory,
        string BridgeReuseMode,
        string Key)
    {
        public static MetricTags Create(string? transport, string? scenario, string? result, string? failureCategory, string? bridgeReuseMode)
        {
            var normalizedTransport = Normalize(transport);
            var normalizedScenario = Normalize(scenario);
            var normalizedResult = Normalize(result);
            var normalizedFailureCategory = Normalize(failureCategory);
            var normalizedBridgeReuseMode = Normalize(bridgeReuseMode);
            var key = string.Create(
                normalizedTransport.Length + normalizedScenario.Length + normalizedResult.Length + normalizedFailureCategory.Length + normalizedBridgeReuseMode.Length + 4,
                (normalizedTransport, normalizedScenario, normalizedResult, normalizedFailureCategory, normalizedBridgeReuseMode),
                static (span, state) =>
                {
                    var index = 0;
                    state.normalizedTransport.AsSpan().CopyTo(span[index..]);
                    index += state.normalizedTransport.Length;
                    span[index++] = '|';
                    state.normalizedScenario.AsSpan().CopyTo(span[index..]);
                    index += state.normalizedScenario.Length;
                    span[index++] = '|';
                    state.normalizedResult.AsSpan().CopyTo(span[index..]);
                    index += state.normalizedResult.Length;
                    span[index++] = '|';
                    state.normalizedFailureCategory.AsSpan().CopyTo(span[index..]);
                    index += state.normalizedFailureCategory.Length;
                    span[index++] = '|';
                    state.normalizedBridgeReuseMode.AsSpan().CopyTo(span[index..]);
                });

            return new MetricTags(
                normalizedTransport,
                normalizedScenario,
                normalizedResult,
                normalizedFailureCategory,
                normalizedBridgeReuseMode,
                key);
        }

        public MetricTagsDto ToDto() => new(Transport, Scenario, Result, FailureCategory, BridgeReuseMode);

        private static string Normalize(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }
    }

    internal sealed class CounterMetric
    {
        private readonly string name;
        private readonly MetricTags tags;
        private long value;

        public CounterMetric(string name, MetricTags tags)
        {
            this.name = name;
            this.tags = tags;
        }

        public void Inc(long by)
        {
            if (by == 0)
            {
                return;
            }

            Interlocked.Add(ref value, by);
        }

        public CounterMetricSnapshot Snapshot()
        {
            return new CounterMetricSnapshot(name, tags.ToDto(), tags.Key, Interlocked.Read(ref value));
        }
    }

    internal sealed class GaugeMetric
    {
        private readonly string name;
        private readonly MetricTags tags;
        private double value;

        public GaugeMetric(string name, MetricTags tags)
        {
            this.name = name;
            this.tags = tags;
        }

        public void Set(double nextValue)
        {
            Interlocked.Exchange(ref value, nextValue);
        }

        public GaugeMetricSnapshot Snapshot()
        {
            return new GaugeMetricSnapshot(name, tags.ToDto(), tags.Key, Interlocked.CompareExchange(ref value, 0d, 0d));
        }
    }

    internal sealed class HistogramMetric
    {
        private readonly string name;
        private readonly MetricTags tags;
        private readonly double[] buckets;
        private readonly long[] bucketCounts;
        private long count;
        private double sum;
        private double min = double.PositiveInfinity;
        private double max = double.NegativeInfinity;

        public HistogramMetric(string name, MetricTags tags, double[] buckets)
        {
            this.name = name;
            this.tags = tags;
            this.buckets = buckets;
            bucketCounts = new long[buckets.Length + 1];
        }

        public void Observe(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return;
            }

            var bucketIndex = FindBucketIndex(value);
            Interlocked.Increment(ref bucketCounts[bucketIndex]);
            Interlocked.Increment(ref count);
            AddDouble(ref sum, value);
            UpdateMin(value);
            UpdateMax(value);
        }

        public HistogramMetricSnapshot Snapshot()
        {
            var countSnapshot = Interlocked.Read(ref count);
            var sumSnapshot = Interlocked.CompareExchange(ref sum, 0d, 0d);
            var minSnapshot = Interlocked.CompareExchange(ref min, 0d, 0d);
            var maxSnapshot = Interlocked.CompareExchange(ref max, 0d, 0d);
            var bucketSnapshots = new HistogramBucketSnapshot[bucketCounts.Length];

            for (var i = 0; i < bucketCounts.Length; i++)
            {
                var upperBound = i < buckets.Length ? buckets[i] : double.PositiveInfinity;
                bucketSnapshots[i] = new HistogramBucketSnapshot(
                    upperBound,
                    Interlocked.Read(ref bucketCounts[i]));
            }

            if (countSnapshot == 0)
            {
                minSnapshot = 0;
                maxSnapshot = 0;
            }

            return new HistogramMetricSnapshot(
                name,
                tags.ToDto(),
                tags.Key,
                countSnapshot,
                sumSnapshot,
                minSnapshot,
                maxSnapshot,
                bucketSnapshots);
        }

        private int FindBucketIndex(double value)
        {
            for (var i = 0; i < buckets.Length; i++)
            {
                if (value <= buckets[i])
                {
                    return i;
                }
            }

            return buckets.Length;
        }

        private void UpdateMin(double value)
        {
            while (true)
            {
                var current = Interlocked.CompareExchange(ref min, 0d, 0d);
                if (value >= current)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref min, value, current) == current)
                {
                    return;
                }
            }
        }

        private void UpdateMax(double value)
        {
            while (true)
            {
                var current = Interlocked.CompareExchange(ref max, 0d, 0d);
                if (value <= current)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref max, value, current) == current)
                {
                    return;
                }
            }
        }

        private static void AddDouble(ref double target, double addend)
        {
            while (true)
            {
                var current = Interlocked.CompareExchange(ref target, 0d, 0d);
                var next = current + addend;
                if (Interlocked.CompareExchange(ref target, next, current) == current)
                {
                    return;
                }
            }
        }
    }
}

public sealed record MetricsSnapshot(
    CounterMetricSnapshot[] Counters,
    GaugeMetricSnapshot[] Gauges,
    HistogramMetricSnapshot[] Histograms);

public sealed record MetricTagsDto(
    string Transport,
    string Scenario,
    string Result,
    string FailureCategory,
    string BridgeReuseMode);

public sealed record CounterMetricSnapshot(
    string Name,
    MetricTagsDto Tags,
    string TagsKey,
    long Value);

public sealed record GaugeMetricSnapshot(
    string Name,
    MetricTagsDto Tags,
    string TagsKey,
    double Value);

public sealed record HistogramMetricSnapshot(
    string Name,
    MetricTagsDto Tags,
    string TagsKey,
    long Count,
    double Sum,
    double Min,
    double Max,
    HistogramBucketSnapshot[] Buckets);

public sealed record HistogramBucketSnapshot(
    double UpperBound,
    long Count);
