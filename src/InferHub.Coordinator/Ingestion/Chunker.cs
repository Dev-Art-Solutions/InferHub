using System.Text;
using System.Text.RegularExpressions;

namespace InferHub.Coordinator.Ingestion;

/// <summary>
/// Recursive splitter: paragraph → sentence → character. Text is first broken into *atoms* —
/// paragraphs, and, kept whole where they fit, fenced code blocks and Markdown tables — and the
/// atoms are then packed greedily into chunks. Overlap is atom-aligned, which is what keeps a
/// chunk from ever exceeding <see cref="IngestionOptions.MaxChars"/>: the tail of the previous
/// chunk is *re-used*, not appended on top of a full one.
/// </summary>
public sealed partial class Chunker(int maxChars, int overlapChars)
{
    private readonly int _maxChars = maxChars;
    private readonly int _overlapChars = Math.Min(overlapChars, Math.Max(0, maxChars - 1));

    public Chunker(IngestionOptions options) : this(options.MaxChars, options.OverlapChars) { }

    [GeneratedRegex(@"(?<=[.!?…])\s+")]
    private static partial Regex SentenceBoundaryRegex();

    /// <summary>Chunk a whole document. Indices run continuously across pages; each chunk keeps its page.</summary>
    public IReadOnlyList<DocumentChunk> Chunk(ExtractedDocument document)
    {
        var chunks = new List<DocumentChunk>();
        foreach (var page in document.Pages)
        {
            foreach (var text in ChunkText(page.Text))
            {
                chunks.Add(new DocumentChunk(chunks.Count, text, page.Page));
            }
        }
        return chunks;
    }

    /// <summary>Chunk one span of text. Empty or whitespace-only input yields no chunks.</summary>
    public IReadOnlyList<string> ChunkText(string text)
    {
        var chunks = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return chunks;

        var atoms = Atomise(text).SelectMany(SplitOversized).ToList();
        if (atoms.Count == 0) return chunks;

        var current = new List<string>();
        var currentLength = 0;

        foreach (var atom in atoms)
        {
            var cost = currentLength == 0 ? atom.Length : currentLength + 2 + atom.Length;
            if (cost > _maxChars && current.Count > 0)
            {
                chunks.Add(Join(current));

                var carried = OverlapTail(current, atom.Length);
                current.Clear();
                current.AddRange(carried);
                currentLength = current.Count == 0 ? 0 : Join(current).Length;
                cost = currentLength == 0 ? atom.Length : currentLength + 2 + atom.Length;
            }

            current.Add(atom);
            currentLength = cost;
        }

        if (current.Count > 0) chunks.Add(Join(current));
        return chunks;
    }

    private static string Join(List<string> atoms) => string.Join("\n\n", atoms);

    /// <summary>
    /// Trailing atoms of the emitted chunk, totalling at most <c>OverlapChars</c> — and only as
    /// many as still leave room for the atom that is about to start the new chunk. An overlap
    /// that crowded out the content it was meant to give context to would be worse than none.
    /// </summary>
    private List<string> OverlapTail(List<string> emitted, int nextAtomLength)
    {
        var carried = new List<string>();
        if (_overlapChars == 0) return carried;

        var budget = Math.Min(_overlapChars, _maxChars - nextAtomLength - 2);
        if (budget <= 0) return carried;

        var used = 0;
        for (var i = emitted.Count - 1; i >= 0; i--)
        {
            var atom = emitted[i];
            var cost = used == 0 ? atom.Length : used + 2 + atom.Length;
            if (cost > budget) break;
            carried.Insert(0, atom);
            used = cost;
        }
        return carried;
    }

    /// <summary>
    /// Split into paragraph-ish atoms, holding fenced code blocks and Markdown tables together —
    /// half a code fence retrieves as noise, and a table split from its header row loses the
    /// column names that made it answerable.
    /// </summary>
    internal static IEnumerable<string> Atomise(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var buffer = new StringBuilder();
        var inFence = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            var isFence = trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal);

            if (isFence)
            {
                if (inFence)
                {
                    buffer.AppendLine(line);
                    inFence = false;
                    var closed = buffer.ToString().Trim();
                    if (closed.Length > 0) yield return closed;
                    buffer.Clear();
                    continue;
                }

                // Opening a fence: flush whatever prose preceded it.
                var pending = buffer.ToString().Trim();
                if (pending.Length > 0) yield return pending;
                buffer.Clear();
                inFence = true;
                buffer.AppendLine(line);
                continue;
            }

            if (inFence)
            {
                buffer.AppendLine(line);
                continue;
            }

            // A blank line is a paragraph boundary — except inside a table, where the run of
            // pipe-prefixed lines is itself the unit and blank lines simply end it.
            if (line.Trim().Length == 0)
            {
                var paragraph = buffer.ToString().Trim();
                if (paragraph.Length > 0) yield return paragraph;
                buffer.Clear();
                continue;
            }

            buffer.AppendLine(line);
        }

        var last = buffer.ToString().Trim();
        if (last.Length > 0) yield return last;
    }

    /// <summary>An atom that is too big for a chunk: fall back to sentences, then to a hard window.</summary>
    private IEnumerable<string> SplitOversized(string atom)
    {
        if (atom.Length <= _maxChars)
        {
            yield return atom;
            yield break;
        }

        var buffer = new StringBuilder();
        foreach (var sentence in SentenceBoundaryRegex().Split(atom))
        {
            if (sentence.Length == 0) continue;

            if (sentence.Length > _maxChars)
            {
                if (buffer.Length > 0)
                {
                    yield return buffer.ToString().Trim();
                    buffer.Clear();
                }

                foreach (var window in HardSplit(sentence)) yield return window;
                continue;
            }

            if (buffer.Length + 1 + sentence.Length > _maxChars && buffer.Length > 0)
            {
                yield return buffer.ToString().Trim();
                buffer.Clear();
            }

            if (buffer.Length > 0) buffer.Append(' ');
            buffer.Append(sentence);
        }

        if (buffer.Length > 0) yield return buffer.ToString().Trim();
    }

    /// <summary>
    /// The floor: a single token longer than a chunk (a minified line, a base64 blob). Split on
    /// a fixed window. There is no meaningful boundary left to respect at this point.
    /// </summary>
    private IEnumerable<string> HardSplit(string text)
    {
        for (var offset = 0; offset < text.Length; offset += _maxChars)
        {
            yield return text.Substring(offset, Math.Min(_maxChars, text.Length - offset));
        }
    }
}
