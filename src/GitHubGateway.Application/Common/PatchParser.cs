using System;
using System.Collections.Generic;

namespace GitHubGateway.Application.Common
{
    /// <summary>
    /// Parsed representation of a unified git diff (one patch file may contain many entries).
    /// Only the minimal subset required to apply the patch is parsed:
    ///   - file path (--- / +++ lines, the b/ path wins)
    ///   - hunk lines that begin with ' ', '+' or '-'
    /// Deleted files (where new path is /dev/null) are represented with IsDeleted = true.
    /// </summary>
    public sealed class PatchEntry
    {
        public string OldPath { get; set; } = string.Empty;
        public string NewPath { get; set; } = string.Empty;
        public bool IsDeleted => string.IsNullOrEmpty(NewPath) || NewPath == "/dev/null";
        public bool IsNew => string.IsNullOrEmpty(OldPath) || OldPath == "/dev/null";
        public List<PatchHunk> Hunks { get; } = new();
    }

    public sealed class PatchHunk
    {
        public int OldStartLine { get; set; }
        public int OldLineCount { get; set; }
        public int NewStartLine { get; set; }
        public int NewLineCount { get; set; }
        public List<PatchLine> Lines { get; } = new();
    }

    public sealed class PatchLine
    {
        public PatchLineType Type { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    public enum PatchLineType
    {
        Context,    // ' '
        Addition,   // '+'
        Deletion,   // '-'
    }

    public static class PatchParser
    {
        /// <summary>
        /// Parses a unified git patch into structured <see cref="PatchEntry"/> objects.
        /// Lines that look like diff headers but cannot be parsed are skipped gracefully.
        /// </summary>
        public static IReadOnlyList<PatchEntry> Parse(string patchText)
        {
            if (string.IsNullOrWhiteSpace(patchText))
                return Array.Empty<PatchEntry>();

            var entries = new List<PatchEntry>();
            PatchEntry? current = null;
            PatchHunk? currentHunk = null;

            var lines = patchText.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                if (line.StartsWith("diff --git ", StringComparison.Ordinal))
                {
                    if (currentHunk != null && current != null) current.Hunks.Add(currentHunk);
                    currentHunk = null;
                    current = new PatchEntry();
                    entries.Add(current);
                    continue;
                }

                if (current == null)
                    continue;   // Skip preamble before first diff

                if (line.StartsWith("--- ", StringComparison.Ordinal))
                {
                    current.OldPath = NormalizePath(line.Substring(4));
                }
                else if (line.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    current.NewPath = NormalizePath(line.Substring(4));
                }
                else if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    if (currentHunk != null) current.Hunks.Add(currentHunk);
                    currentHunk = ParseHunkHeader(line);
                }
                else if (currentHunk != null && line.Length > 0)
                {
                    var t = line[0];
                    PatchLine pl = t switch
                    {
                        ' ' => new PatchLine { Type = PatchLineType.Context,    Text = line.Substring(1) },
                        '+' => new PatchLine { Type = PatchLineType.Addition,   Text = line.Substring(1) },
                        '-' => new PatchLine { Type = PatchLineType.Deletion,   Text = line.Substring(1) },
                        _   => new PatchLine { Type = PatchLineType.Context,    Text = line },
                    };
                    currentHunk.Lines.Add(pl);
                }
                else if (currentHunk != null && line.Length == 0)
                {
                    // A blank line is a context line with empty content.
                    currentHunk.Lines.Add(new PatchLine { Type = PatchLineType.Context, Text = string.Empty });
                }
            }

            if (currentHunk != null && current != null) current.Hunks.Add(currentHunk);
            return entries;
        }

        private static string NormalizePath(string raw)
        {
            raw = raw.Trim();
            if (raw == "/dev/null") return string.Empty;
            // Strip a/ or b/ prefix
            if (raw.StartsWith("a/", StringComparison.Ordinal)) raw = raw.Substring(2);
            else if (raw.StartsWith("b/", StringComparison.Ordinal)) raw = raw.Substring(2);
            // Some patches quote paths; strip surrounding quotes
            if (raw.Length >= 2 && raw.StartsWith('"') && raw.EndsWith('"'))
                raw = raw.Substring(1, raw.Length - 2);
            return raw;
        }

        private static PatchHunk ParseHunkHeader(string header)
        {
            // Format: @@ -l,s +l,s @@ <optional section>
            var hunk = new PatchHunk();
            var tokens = header.Split(' ');
            foreach (var tok in tokens)
            {
                if (tok.StartsWith("-", StringComparison.Ordinal) && tok.Length > 1)
                {
                    var parts = tok.Substring(1).Split(',');
                    hunk.OldStartLine = int.Parse(parts[0]);
                    hunk.OldLineCount = parts.Length > 1 ? int.Parse(parts[1]) : 1;
                }
                else if (tok.StartsWith("+", StringComparison.Ordinal) && tok.Length > 1)
                {
                    var parts = tok.Substring(1).Split(',');
                    hunk.NewStartLine = int.Parse(parts[0]);
                    hunk.NewLineCount = parts.Length > 1 ? int.Parse(parts[1]) : 1;
                }
            }
            return hunk;
        }

        /// <summary>
        /// Applies a parsed <paramref name="entry"/> to the <paramref name="original"/> file content
        /// and returns the new content. Throws if the patch does not cleanly apply.
        /// </summary>
        public static string ApplyTo(PatchEntry entry, string original)
        {
            if (entry.IsNew)
            {
                // For new files, just concatenate all addition lines.
                var sb = new System.Text.StringBuilder();
                foreach (var h in entry.Hunks)
                    foreach (var l in h.Lines)
                        if (l.Type == PatchLineType.Addition || l.Type == PatchLineType.Context)
                            sb.AppendLine(l.Text);
                return sb.ToString().TrimEnd('\n', '\r');
            }

            if (entry.IsDeleted)
                return string.Empty;

            var originalLines = original.Replace("\r\n", "\n").Split('\n');
            // Need to use a List<string> because we will insert / remove in place.
            var working = new List<string>(originalLines);
            int cursor = 0;   // 0-based offset from start of file (works with 1-based line numbers in patch)

            foreach (var hunk in entry.Hunks)
            {
                // Convert hunk.NewStartLine (1-based) into a 0-based index, but be lenient:
                // we look for the first context/deletion line that matches.
                int startPos = Math.Max(0, hunk.OldStartLine - 1);

                // Try to find the actual position by matching the first few context lines.
                int foundPos = FindAnchor(working, startPos, hunk.Lines);
                if (foundPos < 0)
                    throw new InvalidOperationException(
                        $"Hunk starting at line {hunk.OldStartLine} does not cleanly apply to file.");

                // Walk through hunk lines, mutating `working` in place.
                int w = foundPos;
                for (int i = 0; i < hunk.Lines.Count; i++)
                {
                    var pl = hunk.Lines[i];
                    switch (pl.Type)
                    {
                        case PatchLineType.Context:
                            if (w >= working.Count || working[w] != pl.Text)
                                throw new InvalidOperationException(
                                    $"Context mismatch at line {w + 1}: expected '{pl.Text}'.");
                            w++;
                            break;
                        case PatchLineType.Deletion:
                            if (w >= working.Count || working[w] != pl.Text)
                                throw new InvalidOperationException(
                                    $"Deletion mismatch at line {w + 1}: expected '{pl.Text}'.");
                            working.RemoveAt(w);
                            break;
                        case PatchLineType.Addition:
                            working.Insert(w, pl.Text);
                            w++;
                            break;
                    }
                }
                cursor = w;
            }

            return string.Join('\n', working);
        }

        private static int FindAnchor(List<string> working, int startPos, List<PatchLine> hunkLines)
        {
            // Collect the first context/deletion lines as the anchor signature.
            var anchor = new List<string>();
            foreach (var pl in hunkLines)
            {
                if (pl.Type == PatchLineType.Context || pl.Type == PatchLineType.Deletion)
                    anchor.Add(pl.Text);
                if (anchor.Count >= 3) break;
            }
            if (anchor.Count == 0)
                return Math.Min(startPos, working.Count);

            for (int p = startPos; p + anchor.Count <= working.Count; p++)
            {
                bool match = true;
                for (int k = 0; k < anchor.Count; k++)
                    if (working[p + k] != anchor[k]) { match = false; break; }
                if (match) return p;
            }
            return -1;
        }
    }
}
