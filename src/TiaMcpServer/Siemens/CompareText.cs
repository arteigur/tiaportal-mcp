using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// Normalisation and line diff for the Compare* tools.
    ///
    /// Callers: Portal.Compare.cs (CompareBlockToFile, CompareBlocksToFolder). Affected API: none
    /// existing - new type. Pure text handling, no Openness and no file I/O, so it can be tested
    /// without TIA Portal.
    ///
    /// SimaticML exports always differ in a few places that say nothing about the block: the
    /// export timestamp and installed products in &lt;DocumentInfo&gt;, the &lt;Engineering
    /// version&gt;, the per-export 'ID' attributes and the formatting. <see cref="NormalizeSimaticMl"/>
    /// removes exactly those, so a diff shows only what changed in the block itself.
    /// </summary>
    public static class CompareText
    {
        /// <summary>
        /// Block attributes that are timestamps. They appear in an export only with the
        /// 'ReadOnly' export option, so a file exported with it would otherwise always differ.
        /// </summary>
        public static readonly IReadOnlyCollection<string> IgnoredXmlAttributeElements = new[]
        {
            "CodeModifiedDate", "CompileDate", "CreationDate", "DownloadDate", "InterfaceModifiedDate",
            "ModifiedDate", "ParameterModified", "StructureModified"
        };

        /// <summary>
        /// Canonical form of a SimaticML document: the XML declaration, &lt;Engineering&gt;,
        /// &lt;DocumentInfo&gt;, every 'ID' attribute and the timestamp attributes of the block are
        /// removed, and the rest is re-indented with two spaces and '\n' line ends. Attribute
        /// order, namespaces, text and the order of elements are kept.
        /// </summary>
        public static string NormalizeSimaticMl(string xml)
        {
            var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);

            document.Declaration = null;

            var root = document.Root;
            if (root != null)
            {
                root.Elements().Where(e => e.Name.LocalName == "Engineering" || e.Name.LocalName == "DocumentInfo").Remove();

                foreach (var element in root.DescendantsAndSelf())
                {
                    element.Attributes().Where(a => a.Name.LocalName == "ID" && a.Name.Namespace == XNamespace.None).Remove();
                }

                root.Descendants()
                    .Where(e => e.Parent?.Name.LocalName == "AttributeList" && IgnoredXmlAttributeElements.Contains(e.Name.LocalName))
                    .ToList()
                    .ForEach(e => e.Remove());

                // Whitespace between elements is formatting; whitespace inside a text-only element is content.
                document.DescendantNodes()
                    .OfType<XText>()
                    .Where(t => string.IsNullOrWhiteSpace(t.Value) && (t.Parent == null || t.Parent.Elements().Any()))
                    .ToList()
                    .ForEach(t => t.Remove());
            }

            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                NewLineChars = "\n",
                NewLineHandling = NewLineHandling.Replace,
                OmitXmlDeclaration = true,
                Encoding = new UTF8Encoding(false)
            };

            using (var writer = new StringWriter())
            {
                using (var xmlWriter = XmlWriter.Create(writer, settings))
                {
                    document.Save(xmlWriter);
                }

                return writer.ToString();
            }
        }

        /// <summary>
        /// Canonical form of a source text (.scl, .db, .awl, .s7dcl, .s7res): '\n' line ends, no
        /// trailing whitespace and no trailing empty lines. With <paramref name="ignoreWhitespace"/>
        /// indentation is dropped and every run of blanks becomes one space as well.
        /// </summary>
        public static string NormalizeSourceText(string text, bool ignoreWhitespace = false)
        {
            var lines = SplitLines(text)
                .Select(l => ignoreWhitespace ? Regex.Replace(l.Trim(), @"[ \t]+", " ") : l.TrimEnd())
                .ToList();

            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            return string.Join("\n", lines);
        }

        public static List<string> SplitLines(string text)
        {
            return (text ?? string.Empty)
                .TrimStart((char)0xFEFF)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n')
                .ToList();
        }

        #region diff

        public enum DiffOp
        {
            Equal,
            Delete,
            Insert
        }

        public struct DiffLine
        {
            public DiffOp Op;
            public string Text;

            public DiffLine(DiffOp op, string text)
            {
                Op = op;
                Text = text;
            }
        }

        /// <summary>The outcome of one comparison.</summary>
        public sealed class DiffResult
        {
            public bool Identical { get; set; }

            /// <summary>Lines only in the left text (shown with '-').</summary>
            public int RemovedLines { get; set; }

            /// <summary>Lines only in the right text (shown with '+').</summary>
            public int AddedLines { get; set; }

            public int Hunks { get; set; }

            /// <summary>Unified diff, empty when identical.</summary>
            public string UnifiedDiff { get; set; } = string.Empty;
        }

        /// <summary>
        /// Line diff of two texts in unified format (Myers' algorithm on the lines left after the
        /// common prefix and suffix). Lines starting with '-' are only in <paramref name="left"/>,
        /// lines with '+' only in <paramref name="right"/>.
        /// </summary>
        public static DiffResult Diff(string left, string right, string leftLabel, string rightLabel, int contextLines = 3)
        {
            // An empty text has no lines, not one empty line.
            var a = string.IsNullOrEmpty(left) ? new List<string>() : SplitLines(left);
            var b = string.IsNullOrEmpty(right) ? new List<string>() : SplitLines(right);

            var script = Compute(a, b);

            var result = new DiffResult
            {
                RemovedLines = script.Count(l => l.Op == DiffOp.Delete),
                AddedLines = script.Count(l => l.Op == DiffOp.Insert)
            };

            result.Identical = result.RemovedLines == 0 && result.AddedLines == 0;

            if (!result.Identical)
            {
                var (text, hunks) = Format(script, leftLabel, rightLabel, Math.Max(0, contextLines));

                result.UnifiedDiff = text;
                result.Hunks = hunks;
            }

            return result;
        }

        /// <summary>Edit script turning <paramref name="a"/> into <paramref name="b"/>.</summary>
        public static List<DiffLine> Compute(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            var prefix = 0;
            while (prefix < a.Count && prefix < b.Count && a[prefix] == b[prefix])
            {
                prefix++;
            }

            var suffix = 0;
            while (suffix < a.Count - prefix && suffix < b.Count - prefix && a[a.Count - 1 - suffix] == b[b.Count - 1 - suffix])
            {
                suffix++;
            }

            var result = new List<DiffLine>(a.Count + b.Count);

            for (var i = 0; i < prefix; i++)
            {
                result.Add(new DiffLine(DiffOp.Equal, a[i]));
            }

            var middleA = a.Skip(prefix).Take(a.Count - prefix - suffix).ToList();
            var middleB = b.Skip(prefix).Take(b.Count - prefix - suffix).ToList();

            result.AddRange(Myers(middleA, middleB));

            for (var i = a.Count - suffix; i < a.Count; i++)
            {
                result.Add(new DiffLine(DiffOp.Equal, a[i]));
            }

            return result;
        }

        // Beyond this many edits the trace would grow too large; the remaining middle part is
        // then reported as a whole block removed and added, which is still a correct diff.
        private const int MaxEditDistance = 4000;

        private static List<DiffLine> Myers(List<string> a, List<string> b)
        {
            int n = a.Count, m = b.Count;

            if (n == 0 || m == 0)
            {
                return a.Select(l => new DiffLine(DiffOp.Delete, l))
                    .Concat(b.Select(l => new DiffLine(DiffOp.Insert, l)))
                    .ToList();
            }

            var max = n + m;
            var offset = max + 1;
            var v = new int[2 * max + 3];
            var trace = new List<int[]>();

            for (var d = 0; d <= max; d++)
            {
                if (d > MaxEditDistance)
                {
                    return a.Select(l => new DiffLine(DiffOp.Delete, l))
                        .Concat(b.Select(l => new DiffLine(DiffOp.Insert, l)))
                        .ToList();
                }

                // Snapshot of v[k] for k in [-(d+1), d+1] before step d.
                var snapshot = new int[2 * d + 3];
                Array.Copy(v, offset - d - 1, snapshot, 0, snapshot.Length);
                trace.Add(snapshot);

                for (var k = -d; k <= d; k += 2)
                {
                    int x;
                    if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]))
                    {
                        x = v[offset + k + 1];
                    }
                    else
                    {
                        x = v[offset + k - 1] + 1;
                    }

                    var y = x - k;

                    while (x < n && y < m && a[x] == b[y])
                    {
                        x++;
                        y++;
                    }

                    v[offset + k] = x;

                    if (x >= n && y >= m)
                    {
                        return Backtrack(a, b, trace);
                    }
                }
            }

            throw new InvalidOperationException("Diff did not converge.");
        }

        private static List<DiffLine> Backtrack(List<string> a, List<string> b, List<int[]> trace)
        {
            var reversed = new List<DiffLine>();
            int x = a.Count, y = b.Count;

            for (var d = trace.Count - 1; d >= 0; d--)
            {
                var snapshot = trace[d];
                int Get(int k) => snapshot[k + d + 1];

                var k0 = x - y;
                int prevK;
                if (k0 == -d || (k0 != d && Get(k0 - 1) < Get(k0 + 1)))
                {
                    prevK = k0 + 1;
                }
                else
                {
                    prevK = k0 - 1;
                }

                var prevX = d == 0 ? 0 : Get(prevK);
                var prevY = prevX - prevK;

                while (x > prevX && y > prevY)
                {
                    reversed.Add(new DiffLine(DiffOp.Equal, a[x - 1]));
                    x--;
                    y--;
                }

                if (d > 0)
                {
                    if (x == prevX)
                    {
                        reversed.Add(new DiffLine(DiffOp.Insert, b[y - 1]));
                    }
                    else
                    {
                        reversed.Add(new DiffLine(DiffOp.Delete, a[x - 1]));
                    }
                }

                x = prevX;
                y = prevY;
            }

            reversed.Reverse();

            return reversed;
        }

        private static (string Text, int Hunks) Format(List<DiffLine> script, string leftLabel, string rightLabel, int context)
        {
            var builder = new StringBuilder();
            builder.Append("--- ").Append(leftLabel).Append('\n');
            builder.Append("+++ ").Append(rightLabel).Append('\n');

            // Line numbers (1-based) of each script entry on both sides.
            var leftLine = new int[script.Count];
            var rightLine = new int[script.Count];
            int l = 1, r = 1;
            for (var i = 0; i < script.Count; i++)
            {
                leftLine[i] = l;
                rightLine[i] = r;

                if (script[i].Op != DiffOp.Insert)
                {
                    l++;
                }

                if (script[i].Op != DiffOp.Delete)
                {
                    r++;
                }
            }

            var changes = Enumerable.Range(0, script.Count).Where(i => script[i].Op != DiffOp.Equal).ToList();
            var hunks = 0;
            var c = 0;

            while (c < changes.Count)
            {
                var start = Math.Max(0, changes[c] - context);
                var last = changes[c];

                // Merge changes whose context would touch or overlap.
                while (c + 1 < changes.Count && changes[c + 1] - last <= 2 * context + 1)
                {
                    c++;
                    last = changes[c];
                }

                var end = Math.Min(script.Count - 1, last + context);

                var leftCount = 0;
                var rightCount = 0;
                for (var i = start; i <= end; i++)
                {
                    if (script[i].Op != DiffOp.Insert)
                    {
                        leftCount++;
                    }

                    if (script[i].Op != DiffOp.Delete)
                    {
                        rightCount++;
                    }
                }

                var leftStart = leftCount == 0 ? leftLine[start] - 1 : leftLine[start];
                var rightStart = rightCount == 0 ? rightLine[start] - 1 : rightLine[start];

                builder.Append($"@@ -{leftStart},{leftCount} +{rightStart},{rightCount} @@\n");

                for (var i = start; i <= end; i++)
                {
                    var prefix = script[i].Op == DiffOp.Equal ? ' ' : script[i].Op == DiffOp.Delete ? '-' : '+';
                    builder.Append(prefix).Append(script[i].Text).Append('\n');
                }

                hunks++;
                c++;
            }

            return (builder.ToString(), hunks);
        }

        /// <summary>Cuts a text after at most <paramref name="maxChars"/> characters, on a line boundary.</summary>
        public static (string Text, bool Truncated) Truncate(string text, int maxChars)
        {
            if (maxChars <= 0 || text.Length <= maxChars)
            {
                return (text, false);
            }

            var cut = text.LastIndexOf('\n', Math.Min(maxChars, text.Length - 1));

            return (text.Substring(0, cut > 0 ? cut + 1 : maxChars), true);
        }

        #endregion
    }
}
