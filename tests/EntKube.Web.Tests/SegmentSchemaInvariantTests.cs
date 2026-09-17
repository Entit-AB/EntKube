using EntKube.Telemetry;
using EntKube.Web.Services;
using FluentAssertions;
using Lucene.Net.Analysis.Core;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Store;
using Lucene.Net.Util;

namespace EntKube.Web.Tests;

/// <summary>
/// Pins the one thing every telemetry schema owes the engine: a <c>ts</c> field written as BOTH an indexed
/// <see cref="Int64Field"/> and a <see cref="NumericDocValuesField"/>.
///
/// <para>This was stated only in prose, on <c>ActiveSegmentIndex.TsField</c>, and the trace-summary schema
/// did not honour it for as long as that index existed. Nothing queried there sorts on <c>ts</c>, so the
/// missing DocValue looked like an economy. But <c>ActiveSegmentIndex</c> recovers an unsealed index's time
/// bounds at open for EVERY signal, and reading them through a Lucene Sort falls back — silently — to the
/// legacy FieldCache when the columnar copy is absent, uninverting the whole term dictionary into one packed
/// array sized by the doc count. In production that asked for a contiguous block of hundreds of megabytes
/// and threw OutOfMemoryException from inside a segment manager's constructor, at 1.2 GB of a 2 GB
/// container. The registry cached that failure, the seal service never saw the manager, so its index never
/// rolled and every restart recovered a larger one — the node could only get worse.</para>
///
/// <para>The engine no longer uses the FieldCache path at all (see the recovery test below), so a future
/// omission degrades instead of exploding. This test is what keeps it from being an omission at all.</para>
/// </summary>
public class SegmentSchemaInvariantTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Cluster = Guid.NewGuid();
    private static readonly DateTime When = new(2026, 9, 17, 10, 0, 0, DateTimeKind.Utc);

    public static TheoryData<string, Document> EverySignalDocument() => new()
    {
        {
            "logs (LogSegmentSchema)",
            LogSegmentSchema.ToDocument(Tenant, Cluster, new LogIngestRecord(
                When, "default", "api-0", "api", 9, "hello", null, null))
        },
        {
            "spans (SpanSegmentSchema)",
            SpanSegmentSchema.ToDocument(Tenant, Cluster, new SpanIngestRecord(
                When, "trace-1", "span-1", null, "GET /", "api", 2, 12.5, 1, "default", "api-0", null))
        },
        {
            "traces (TraceSummarySchema)",
            TraceSummarySchema.ToDocument(Tenant, Cluster, new TraceSummaryPartial(
                "trace-1", "default", TelemetryTime.ToEpochMillis(When), TelemetryTime.ToEpochMillis(When) + 12,
                SpanCount: 3, ErrorCount: 0, Services: ["api"], EarliestService: "api", EarliestName: "GET /",
                RootService: "api", RootName: "GET /", RootTs: TelemetryTime.ToEpochMillis(When),
                PartialId: "p1"))
        },
        {
            "rum page view (RumSegmentSchema)",
            RumSegmentSchema.ToPageViewDoc(Tenant, Cluster, new RumPageViewRecord(
                When, "s1", "v1", "/", null, 100, 10, 50, 0.01, 20, 30, "Firefox", "Linux", "desktop"))
        },
        {
            "rum error (RumSegmentSchema)",
            RumSegmentSchema.ToErrorDoc(Tenant, Cluster, new RumErrorRecord(
                When, "s1", "v1", "/", "boom", null, null))
        },
        {
            "rum resource (RumSegmentSchema)",
            RumSegmentSchema.ToResourceDoc(Tenant, Cluster, new RumResourceRecord(
                When, "s1", "v1", "/", "app.js", "script", 12.0, 200, null))
        },
    };

    [Theory]
    [MemberData(nameof(EverySignalDocument))]
    public void EverySignalSchemaWritesTsAsIndexedAndDocValues(string signal, Document doc)
    {
        IIndexableField[] ts = [.. doc.Fields.Where(f => f.Name == "ts")];

        ts.Should().NotBeEmpty($"{signal} must write a 'ts' field — the engine's roll and retention triggers read it");

        ts.Should().Contain(f => f.IndexableFieldType.IsIndexed,
            $"{signal} must index 'ts' so NumericRangeQuery can bound a window");

        ts.Should().Contain(f => f.IndexableFieldType.DocValueType == DocValuesType.NUMERIC,
            $"{signal} must write NumericDocValuesField(ts) — without it ActiveSegmentIndex cannot recover "
            + "its unsealed time bounds from a columnar read, and the fallback allocates an array sized by "
            + "the whole doc count");
    }

    /// <summary>
    /// A leaf with no <c>ts</c> DocValue must cost a warning and unknown bounds — never an uninversion.
    ///
    /// The index here is deliberately built the way the broken schema built it (indexed + stored, no
    /// columnar copy). Opening it must succeed and report the doc count; only the time bounds are lost.
    /// Against the old Sort-based recovery this same index drove Lucene into
    /// <c>FieldCacheImpl.Uninvert.DoUninvert</c>, which is what ran the node out of memory.
    /// </summary>
    [Fact]
    public void RecoveringAnIndexWithNoTsDocValues_OpensWithUnknownBoundsInsteadOfUninverting()
    {
        using var dir = new RAMDirectory();
        var analyzer = new KeywordAnalyzer();

        using (var writer = new IndexWriter(dir, new IndexWriterConfig(LuceneVersion.LUCENE_48, analyzer)))
        {
            for (int i = 0; i < 500; i++)
            {
                writer.AddDocument(new Document
                {
                    // No NumericDocValuesField — exactly what TraceSummarySchema used to write.
                    new Int64Field("ts", TelemetryTime.ToEpochMillis(When.AddSeconds(i)), Field.Store.YES),
                    new StringField("tenant_id", Tenant.ToString("N"), Field.Store.NO),
                });
            }
            writer.Commit();
        }

        using var index = new ActiveSegmentIndex(dir, analyzer, ownsDirectory: false);

        index.DocCount.Should().Be(500, "the doc count comes from the writer, not from the missing DocValue");
        index.MinTs.Should().BeNull("bounds are unknowable without a columnar ts, and guessing is worse");
        index.MaxTs.Should().BeNull();
    }

    /// <summary>The same index WITH the DocValue recovers its real bounds — the case that must keep working.</summary>
    [Fact]
    public void RecoveringAnIndexWithTsDocValues_RestoresTheRealBounds()
    {
        using var dir = new RAMDirectory();
        var analyzer = new KeywordAnalyzer();

        using (var writer = new IndexWriter(dir, new IndexWriterConfig(LuceneVersion.LUCENE_48, analyzer)))
        {
            for (int i = 0; i < 500; i++)
            {
                long ms = TelemetryTime.ToEpochMillis(When.AddSeconds(i));
                writer.AddDocument(new Document
                {
                    new Int64Field("ts", ms, Field.Store.YES),
                    new NumericDocValuesField("ts", ms),
                });
            }
            writer.Commit();
        }

        using var index = new ActiveSegmentIndex(dir, analyzer, ownsDirectory: false);

        index.DocCount.Should().Be(500);
        index.MinTs.Should().Be(When);
        index.MaxTs.Should().Be(When.AddSeconds(499));
    }

    /// <summary>
    /// A manager whose construction fails must not be remembered as failed.
    ///
    /// <see cref="Lazy{T}"/> caches its factory's exception for good, so the registry used to answer every
    /// later call for that tenant with the same error and — because <c>ActiveManagers</c> filters on
    /// <c>IsValueCreated</c> — hide the tenant from the seal service entirely. Its index then grew unsealed
    /// forever, which is the one thing that makes the next open more likely to fail than the last.
    /// </summary>
    [Fact]
    public void ARegistryForgetsAFailedCreateSoTheNextCallRetries()
    {
        int attempts = 0;
        var registry = new SegmentManagerRegistry<SegmentManagerBase>(_ =>
        {
            attempts++;
            throw new OutOfMemoryException("the index was too large to open");
        });

        Guid tenant = Guid.NewGuid();

        registry.Invoking(r => r.For(tenant)).Should().Throw<OutOfMemoryException>();
        registry.Invoking(r => r.For(tenant)).Should().Throw<OutOfMemoryException>();
        registry.Invoking(r => r.For(tenant)).Should().Throw<OutOfMemoryException>();

        attempts.Should().Be(3, "each call must try again rather than replay a cached exception");
        registry.ActiveManagers.Should().BeEmpty("nothing was ever successfully created");
    }
}
