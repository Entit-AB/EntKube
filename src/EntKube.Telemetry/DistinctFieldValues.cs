using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;

namespace EntKube.Telemetry;

/// <summary>
/// The distinct values of an indexed label field — what fills the viewers' namespace, pod, container and
/// service dropdowns.
///
/// <para>The values are read from the field's <b>SortedDocValues ordinals</b> over the documents the
/// caller's filter actually matches: one scorer walk per index leaf, one integer read per matched
/// document, and a term lookup only for the ordinals that turned up. Nothing is materialized per
/// document — no stored fields, no strings — so the walk costs about what advancing the postings costs,
/// and the term lookups cost one seek per value that will appear in the dropdown.</para>
///
/// <para>Two earlier shapes of this are worth naming, because both are natural and both are traps. Reading
/// a value <i>string</i> per hit costs a byte-copy and a UTF-8 decode per log line, which is seconds for an
/// hour of a busy cluster. Walking the field's term dictionary and confirming each candidate with a probe
/// query — which this replaced — costs one query per <i>distinct value in the whole index</i>, and the
/// index is not scoped to the caller: a tenant's segments hold every cluster's pods, and pod names churn
/// with every rollout, so "how many pods has this tenant ever run in the last hour of segments" is tens of
/// thousands. Measured on a 1M-line index with 32k distinct pod names, that probe loop took 10.4s to fill
/// the pod dropdown while the log search it sat in front of took 49ms. The ordinal scan below answers the
/// same question in milliseconds, and its cost is bounded by the caller's own filter rather than by
/// everything the index has ever seen.</para>
/// </summary>
internal static class DistinctFieldValues
{
    /// <summary>
    /// Adds every value of <paramref name="field"/> carried by at least one document matching
    /// <paramref name="filter"/> to <paramref name="sink"/>. The sink is shared across index tiers, so a
    /// tier is free to re-discover a value an earlier one already added.
    /// </summary>
    public static void Collect(IndexSearcher searcher, Query filter, string field, ISet<string> sink)
    {
        Weight weight = searcher.CreateNormalizedWeight(filter);

        foreach (AtomicReaderContext leaf in searcher.IndexReader.Leaves)
        {
            AtomicReader reader = leaf.AtomicReader;
            SortedDocValues? values = reader.GetSortedDocValues(field);
            if (values is null || values.ValueCount == 0)
            {
                // No columnar copy of the field in this segment. Nothing the engine writes today lands
                // here, but a leaf that predates the DocValue (or a field indexed without one) must still
                // answer rather than silently contribute nothing to the dropdown.
                CollectByTermWalk(searcher, weight, leaf, field, sink);
                continue;
            }

            // acceptDocs = LiveDocs, so values carried only by deleted documents don't resurrect a pod
            // that no longer has any logs.
            Scorer scorer = weight.GetScorer(leaf, reader.LiveDocs);
            if (scorer is null) continue;

            var seen = new FixedBitSet(values.ValueCount);
            int found = 0;
            for (int doc = scorer.NextDoc(); doc != DocIdSetIterator.NO_MORE_DOCS; doc = scorer.NextDoc())
            {
                int ord = values.GetOrd(doc);
                if (ord < 0 || seen.Get(ord)) continue;
                seen.Set(ord);
                // Every value this leaf holds is already accounted for, so the rest of the walk cannot
                // discover anything new. Worth the counter: on a single-cluster node the filter matches
                // most of the segment, and the distinct values run out in the first handful of documents.
                if (++found == values.ValueCount) break;
            }

            var scratch = new BytesRef();
            for (int ord = seen.NextSetBit(0); ord >= 0;
                 ord = ord + 1 < values.ValueCount ? seen.NextSetBit(ord + 1) : -1)
            {
                values.LookupOrd(ord, scratch);
                string value = scratch.Utf8ToString();
                if (value.Length > 0) sink.Add(value);
            }
        }
    }

    /// <summary>
    /// Fallback for a leaf with no DocValue on the field: walk its term dictionary and keep each term that
    /// some matching document carries. Existence only — it stops at the first hit rather than visiting
    /// every match the way a collector would.
    /// </summary>
    private static void CollectByTermWalk(
        IndexSearcher searcher, Weight filter, AtomicReaderContext leaf, string field, ISet<string> sink)
    {
        Terms? terms = leaf.AtomicReader.GetTerms(field);
        if (terms is null) return;

        TermsEnum values = terms.GetEnumerator();
        while (values.MoveNext())
        {
            string value = values.Term.Utf8ToString();
            if (value.Length == 0 || sink.Contains(value)) continue;

            var probe = new BooleanQuery
            {
                { filter.Query, Occur.MUST },
                { new TermQuery(new Term(field, value)), Occur.MUST },
            };
            Scorer scorer = searcher.CreateNormalizedWeight(probe)
                .GetScorer(leaf, leaf.AtomicReader.LiveDocs);
            if (scorer is not null && scorer.NextDoc() != DocIdSetIterator.NO_MORE_DOCS) sink.Add(value);
        }
    }
}
