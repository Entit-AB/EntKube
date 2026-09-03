using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;

namespace EntKube.Telemetry;

/// <summary>
/// The distinct values of an indexed label field — what fills the viewers' namespace, pod, container and
/// service dropdowns.
///
/// The obvious implementation is to search the scope filter and read a DocValue per hit, and that is what
/// this replaced. It costs one visit <b>per log line</b> to produce a list of a few dozen strings: on a
/// cluster writing a few thousand lines a second, an hour's window is millions of ordinal lookups before
/// the namespace picker can be drawn. That is why opening the log viewer took seconds before any of the
/// work the operator actually asked for had started.
///
/// Lucene already holds the answer. A segment's term dictionary for the field <i>is</i> the list of
/// distinct values, and walking it costs one step per distinct value. The one thing it cannot say is
/// whether a value still belongs to a document this caller may see — a management-plane segment holds
/// several clusters, and deleted documents leave their terms in the dictionary until a merge — so each
/// candidate is confirmed by asking for its <b>first</b> matching document and stopping there.
///
/// So the cost goes from "one lookup per document" to "one term walk, plus one short seek per distinct
/// value", and the result is identical to what the per-document scan produced.
/// </summary>
internal static class DistinctFieldValues
{
    /// <summary>
    /// Adds every value of <paramref name="field"/> carried by at least one document matching
    /// <paramref name="filter"/> to <paramref name="sink"/>. The sink is shared across index tiers, so a
    /// value already proven by an earlier call is not re-confirmed.
    /// </summary>
    public static void Collect(IndexSearcher searcher, Query filter, string field, ISet<string> sink)
    {
        IList<AtomicReaderContext> leaves = searcher.IndexReader.Leaves;

        // Every value present anywhere in the index, before filtering. Collected across all leaves first
        // so a value carried by many segments is confirmed once rather than once per segment.
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (AtomicReaderContext leaf in leaves)
        {
            Terms? terms = leaf.AtomicReader.GetTerms(field);
            if (terms is null) continue;

            TermsEnum values = terms.GetEnumerator();
            while (values.MoveNext())
            {
                string value = values.Term.Utf8ToString();
                if (value.Length > 0 && !sink.Contains(value)) candidates.Add(value);
            }
        }

        foreach (string value in candidates)
        {
            if (HasAnyMatch(searcher, leaves, filter, field, value)) sink.Add(value);
        }
    }

    /// <summary>
    /// True as soon as one document matches both the scope filter and this value.
    ///
    /// Driving the leaves directly rather than calling <c>searcher.Search</c> is the whole point: every
    /// collector Lucene ships visits all matching documents, because ranking needs them. Nothing here
    /// needs ranking or a count — only existence — so this stops at the first hit, in the first segment
    /// that has one.
    /// </summary>
    private static bool HasAnyMatch(
        IndexSearcher searcher, IList<AtomicReaderContext> leaves, Query filter, string field, string value)
    {
        var probe = new BooleanQuery
        {
            { filter, Occur.MUST },
            { new TermQuery(new Term(field, value)), Occur.MUST },
        };

        Weight weight = searcher.CreateNormalizedWeight(probe);
        foreach (AtomicReaderContext leaf in leaves)
        {
            // acceptDocs = LiveDocs, so a value left behind by deleted documents does not resurrect a
            // namespace that no longer has any logs.
            Scorer scorer = weight.GetScorer(leaf, leaf.AtomicReader.LiveDocs);
            if (scorer is not null && scorer.NextDoc() != DocIdSetIterator.NO_MORE_DOCS) return true;
        }

        return false;
    }
}
