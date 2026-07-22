using System.IO.Compression;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;
using Environment = EntKube.Web.Data.Environment;

namespace EntKube.Web.Tests;

/// <summary>
/// zstd segment sealing (<see cref="SegmentArchive"/>): proves that sealing a realistic log corpus with
/// zstd produces a materially smaller at-rest archive than the old Deflate zip of the same Lucene segment,
/// AND that the zstd-tar archive round-trips — download → unpack → reopen → full-text search + stored-field
/// reconstruction. Also pins the back-compat path: a legacy Deflate <c>.zip</c> segment still unpacks.
/// </summary>
public sealed class SegmentCodecTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly TestDbContextFactory _factory;
    private readonly ClusterTenantResolver _resolver;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _clusterId = Guid.NewGuid();
    private readonly List<SegmentManagerRegistry<LogSegmentManager>> _registries = [];
    private readonly List<string> _tempDirs = [];
    private readonly ITestOutputHelper _out;
    private string _dataPath = "";

    public SegmentCodecTests(ITestOutputHelper output)
    {
        _out = output;
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        Guid envId = Guid.NewGuid();
        _context.Tenants.Add(new Tenant { Id = _tenantId, Name = "Acme", Slug = "acme" });
        _context.Environments.Add(new Environment { Id = envId, TenantId = _tenantId, Name = "prod" });
        _context.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = _clusterId, TenantId = _tenantId, EnvironmentId = envId,
            Name = "c1", ApiServerUrl = "https://k8s.example.com",
        });
        _context.SaveChanges();

        _factory = new TestDbContextFactory(_connection);
        _resolver = new ClusterTenantResolver(_factory);
    }

    private (LogSegmentManager mgr, SegmentManagerRegistry<LogSegmentManager> reg) NewManager()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), "codectest-" + Guid.NewGuid().ToString("N"));
        _tempDirs.Add(_dataPath);
        string blobsDir = Path.Combine(_dataPath, "blobs");
        Directory.CreateDirectory(blobsDir);
        var options = new SegmentEngineOptions { DataPath = _dataPath };
        var store = new LocalSegmentBlobStore(blobsDir);
        var reg = new SegmentManagerRegistry<LogSegmentManager>(tid =>
            new LogSegmentManager(tid, _factory, store, options, NullLogger<LogSegmentManager>.Instance));
        _registries.Add(reg);
        return (reg.For(_tenantId), reg);
    }

    // A realistic, compressible log corpus: structured lines + a JSON attributes blob — the payload that
    // dominates real segments. Deterministic so a rerun indexes identical bytes.
    private static List<LogIngestRecord> Corpus(int n)
    {
        string[] pods = ["api", "worker", "frontend"];
        var records = new List<LogIngestRecord>(n);
        DateTime t0 = new(2026, 7, 8, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < n; i++)
        {
            string pod = pods[i % 3];
            bool err = i % 25 == 0;
            short sev = (short)(err ? 4 : 2);
            string body =
                $"2026-07-08T10:{i % 60:D2}:{i % 60:D2}Z {(err ? "ERROR" : "INFO")} [{pod}] " +
                $"request_id={i:D8} GET /api/v1/tenants/{i % 1000}/resources status={(err ? 503 : 200)} " +
                $"duration_ms={i % 500} upstream connect reason connection failure to db-{i % 7}";
            string attrs =
                $"{{\"http.status_code\":{(err ? 503 : 200)},\"http.method\":\"GET\"," +
                $"\"pod\":\"{pod}-{i % 9:D3}\",\"k8s.namespace\":\"team-alpha\",\"service\":\"{pod}\"}}";
            records.Add(new LogIngestRecord(t0.AddMilliseconds(i * 10), "team-alpha", $"{pod}-{i % 9:D3}", "app",
                sev, body, err ? $"trace-{i}" : null, attrs));
        }
        return records;
    }

    [Fact]
    public async Task ZstdSeal_IsSmallerThanDeflateZip_AndReadsBack()
    {
        const int n = 20_000;
        (LogSegmentManager mgr, SegmentManagerRegistry<LogSegmentManager> reg) = NewManager();
        mgr.WriteLogs(_tenantId, _clusterId, Corpus(n));
        TelemetrySegment? seg = await mgr.RollAndSealAsync();
        seg.Should().NotBeNull();
        long zstdSize = seg!.SizeBytes;

        // The seal adopts the unpacked segment dir into the local cache at a deterministic path. Re-pack it
        // as a Deflate zip (the old seal format) for an apples-to-apples size comparison on real Lucene bytes.
        string cacheDir = Path.Combine(_dataPath, _tenantId.ToString("N"), "cache", "logs", seg.Id.ToString("N"));
        Directory.Exists(cacheDir).Should().BeTrue();
        string zipPath = Path.Combine(_dataPath, "compare.zip");
        ZipFile.CreateFromDirectory(cacheDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        long deflateSize = new FileInfo(zipPath).Length;

        _out.WriteLine($"zstd seal    = {zstdSize,9:N0} bytes ({zstdSize / (double)n:F1} B/doc)");
        _out.WriteLine($"Deflate zip  = {deflateSize,9:N0} bytes ({deflateSize / (double)n:F1} B/doc)");
        _out.WriteLine($"reduction    = {100.0 * (deflateSize - zstdSize) / deflateSize:F1}%");

        // The whole point: the zstd archive that lands on object storage is smaller than the old zip.
        zstdSize.Should().BeLessThan(deflateSize);

        // Round-trip: the logs live only in the sealed segment now, so a query must download + unpack +
        // reopen it and reconstruct the stored body from the zstd-tar archive.
        var svc = new SegmentLogService(
            new LogTierRegistries(reg, null, new SegmentEngineOptions()), _resolver, NullLogger<SegmentLogService>.Instance);
        var hits = await svc.QueryAsync(
            _clusterId, new LogQueryFilter { Namespaces = ["team-alpha"], Text = "upstream" },
            new DateTime(2026, 7, 8, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 7, 9, 0, 0, 0, DateTimeKind.Utc),
            limit: 5);
        hits.IsSuccess.Should().BeTrue();
        List<LokiLogEntry> entries = hits.Data!.SelectMany(s => s.Entries).ToList();
        entries.Should().NotBeEmpty();
        entries[0].Line.Should().Contain("upstream connect reason connection failure");
    }

    [Fact]
    public async Task SegmentArchive_RoundTrips_AndBeatsDeflate_OnCompressibleContent()
    {
        // A stand-in for a segment directory: several files of repetitive, log-shaped text.
        string src = Path.Combine(Path.GetTempPath(), "arc-src-" + Guid.NewGuid().ToString("N"));
        _tempDirs.Add(src);
        Directory.CreateDirectory(src);
        for (int f = 0; f < 4; f++)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 5000; i++)
                sb.AppendLine($"2026-07-08 INFO [api] request_id={i:D8} GET /api/v1/resources/{i % 100} status=200 ok");
            await File.WriteAllTextAsync(Path.Combine(src, $"_{f}.fdt"), sb.ToString());
        }

        string zst = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + SegmentArchive.Extension);
        string zip = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
        await SegmentArchive.PackAsync(src, zst, zstdLevel: 19);
        ZipFile.CreateFromDirectory(src, zip, CompressionLevel.Optimal, includeBaseDirectory: false);

        new FileInfo(zst).Length.Should().BeLessThan(new FileInfo(zip).Length);

        // zstd-tar unpacks to identical file content...
        string outZ = Path.Combine(Path.GetTempPath(), "arc-out-" + Guid.NewGuid().ToString("N"));
        _tempDirs.Add(outZ);
        await SegmentArchive.UnpackAsync(zst, outZ);
        (await File.ReadAllTextAsync(Path.Combine(outZ, "_0.fdt")))
            .Should().Be(await File.ReadAllTextAsync(Path.Combine(src, "_0.fdt")));

        // ...and the back-compat path: a legacy Deflate .zip unpacks through the same API (format sniffed).
        string outLegacy = Path.Combine(Path.GetTempPath(), "arc-legacy-" + Guid.NewGuid().ToString("N"));
        _tempDirs.Add(outLegacy);
        await SegmentArchive.UnpackAsync(zip, outLegacy);
        (await File.ReadAllTextAsync(Path.Combine(outLegacy, "_1.fdt")))
            .Should().Be(await File.ReadAllTextAsync(Path.Combine(src, "_1.fdt")));

        File.Delete(zst); File.Delete(zip);
    }

    public void Dispose()
    {
        foreach (SegmentManagerRegistry<LogSegmentManager> r in _registries) r.Dispose();
        _context.Dispose();
        _connection.Dispose();
        foreach (string d in _tempDirs)
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { /* temp */ }
    }
}
