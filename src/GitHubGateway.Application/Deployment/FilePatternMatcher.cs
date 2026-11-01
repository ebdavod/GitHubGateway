using System;
using System.Collections.Generic;

namespace GitHubGateway.Application.Deployment
{
    /// <summary>
    /// Simple glob-style pattern matcher for file path filtering.
    /// Supports:
    ///   * — match any number of characters
    ///   ? — match exactly one character
    ///   / — path separator (matches on either / or \)
    ///   ** — match any number of path segments
    /// </summary>
    public static class FilePatternMatcher
    {
        /// <summary>
        /// Returns true if the given relative path matches any of the patterns.
        /// Patterns are matched against the path with / as separator.
        /// </summary>
        public static bool MatchesAny(string relativePath, IEnumerable<string>? patterns)
        {
            if (patterns == null) return false;
            var normalized = relativePath.Replace('\\', '/').TrimStart('/');
            foreach (var p in patterns)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                var pattern = p.Replace('\\', '/').TrimStart('/');
                if (GlobMatch(pattern, normalized)) return true;
            }
            return false;
        }

        /// <summary>
        /// Simple recursive glob matcher supporting * and ? and **.
        /// </summary>
        private static bool GlobMatch(string pattern, string text)
        {
            int p = 0, t = 0;
            int starP = -1, starT = -1;
            int doubleStarP = -1, doubleStarT = -1;

            while (t < text.Length)
            {
                if (p < pattern.Length)
                {
                    // Double-star: match any number of segments (including /)
                    if (p + 2 < pattern.Length && pattern[p] == '*' && pattern[p + 1] == '*' && (pattern[p + 2] == '/' || p + 2 == pattern.Length - 1 || p + 2 == pattern.Length))
                    {
                        // consume ** and optional /
                        if (p + 2 < pattern.Length && pattern[p + 2] == '/')
                            p += 3;
                        else
                            p += 2;
                        doubleStarP = p;
                        doubleStarT = t;
                        continue;
                    }
                    if (pattern[p] == '*')
                    {
                        // Single star: match any character except /
                        starP = p;
                        starT = t;
                        p++;
                        continue;
                    }
                    if (pattern[p] == '?' && text[t] != '/')
                    {
                        p++;
                        t++;
                        continue;
                    }
                    if (pattern[p] == text[t])
                    {
                        p++;
                        t++;
                        continue;
                    }
                }
                // Mismatch — try to backtrack single-star
                if (starP != -1 && starT < text.Length && text[starT] != '/')
                {
                    p = starP + 1;
                    starT++;
                    t = starT;
                    continue;
                }
                // Try double-star
                if (doubleStarP != -1)
                {
                    p = doubleStarP;
                    while (doubleStarT < text.Length)
                    {
                        doubleStarT++;
                        if (doubleStarT < text.Length && (text[doubleStarT] == '/' || doubleStarT == text.Length))
                        {
                            t = doubleStarT;
                            break;
                        }
                    }
                    if (doubleStarT >= text.Length)
                    {
                        t = doubleStarT;
                    }
                    continue;
                }
                return false;
            }
            // Skip trailing * and ** in pattern
            while (p < pattern.Length && pattern[p] == '*') p++;
            return p == pattern.Length;
        }
    }
}
