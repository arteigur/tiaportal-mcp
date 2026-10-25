using System.Linq;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Test
{
    /// <summary>
    /// Tests for the normalisation and line diff behind the Compare* tools. These tests do not
    /// connect to TIA Portal.
    /// </summary>
    [TestClass]
    public class Test9CompareText
    {
        private const string ExportA =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<Document>\r\n" +
            "  <Engineering version=\"V20\" />\r\n" +
            "  <DocumentInfo><Created>2026-08-12T13:54:18Z</Created></DocumentInfo>\r\n" +
            "  <SW.Blocks.GlobalDB ID=\"0\">\r\n" +
            "    <AttributeList><Name>db_A</Name><ModifiedDate>2026-01-01T00:00:00Z</ModifiedDate></AttributeList>\r\n" +
            "    <ObjectList><MultilingualText ID=\"1\" CompositionName=\"Comment\" /></ObjectList>\r\n" +
            "  </SW.Blocks.GlobalDB>\r\n" +
            "</Document>\r\n";

        private const string ExportB =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<Document>\n" +
            "    <Engineering version=\"V21\" />\n" +
            "    <DocumentInfo><Created>2026-10-25T22:39:37Z</Created></DocumentInfo>\n" +
            "    <SW.Blocks.GlobalDB ID=\"7\">\n" +
            "        <AttributeList>\n" +
            "            <Name>db_A</Name>\n" +
            "        </AttributeList>\n" +
            "        <ObjectList><MultilingualText ID=\"A\" CompositionName=\"Comment\" /></ObjectList>\n" +
            "    </SW.Blocks.GlobalDB>\n" +
            "</Document>\n";

        [TestMethod]
        public void NormalizeSimaticMl_IgnoresExportNoise()
        {
            // Arrange / Act
            var a = CompareText.NormalizeSimaticMl(ExportA);
            var b = CompareText.NormalizeSimaticMl(ExportB);

            // Assert
            Assert.AreEqual(a, b);
            Assert.IsFalse(a.Contains("DocumentInfo"));
            Assert.IsFalse(a.Contains("Engineering"));
            Assert.IsFalse(a.Contains("ID="));
            Assert.IsFalse(a.Contains("ModifiedDate"));
            Assert.IsTrue(a.StartsWith("<Document>"));
        }

        [TestMethod]
        public void NormalizeSimaticMl_KeepsRealChanges()
        {
            // Arrange
            var changed = ExportB.Replace("<Name>db_A</Name>", "<Name>db_B</Name>");

            // Act
            var diff = CompareText.Diff(CompareText.NormalizeSimaticMl(ExportA), CompareText.NormalizeSimaticMl(changed), "a", "b");

            // Assert
            Assert.IsFalse(diff.Identical);
            Assert.AreEqual(1, diff.AddedLines);
            Assert.AreEqual(1, diff.RemovedLines);
            StringAssert.Contains(diff.UnifiedDiff, "-      <Name>db_A</Name>");
            StringAssert.Contains(diff.UnifiedDiff, "+      <Name>db_B</Name>");
        }

        [TestMethod]
        public void NormalizeSourceText_IgnoresLineEndsAndTrailingBlanks()
        {
            // Arrange / Act
            var a = CompareText.NormalizeSourceText((char)0xFEFF + "A := 1;  \r\nB := 2;\r\n\r\n");
            var b = CompareText.NormalizeSourceText("A := 1;\nB := 2;");

            // Assert
            Assert.AreEqual(a, b);
        }

        [TestMethod]
        public void NormalizeSourceText_IgnoreWhitespace_DropsIndentation()
        {
            // Arrange / Act
            var a = CompareText.NormalizeSourceText("    IF  x THEN", ignoreWhitespace: true);
            var b = CompareText.NormalizeSourceText("\tIF x THEN", ignoreWhitespace: true);

            // Assert
            Assert.AreEqual(a, b);
        }

        [TestMethod]
        public void Diff_IdenticalTexts_HasNoHunks()
        {
            // Arrange / Act
            var diff = CompareText.Diff("a\nb\nc", "a\nb\nc", "left", "right");

            // Assert
            Assert.IsTrue(diff.Identical);
            Assert.AreEqual(0, diff.Hunks);
            Assert.AreEqual(string.Empty, diff.UnifiedDiff);
        }

        [TestMethod]
        public void Diff_ReplacedLine_IsOneHunkWithContext()
        {
            // Arrange
            var left = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"line {i}"));
            var right = left.Replace("line 10", "line ten");

            // Act
            var diff = CompareText.Diff(left, right, "left", "right", contextLines: 2);

            // Assert
            Assert.AreEqual(1, diff.Hunks);
            Assert.AreEqual(1, diff.AddedLines);
            Assert.AreEqual(1, diff.RemovedLines);
            Assert.AreEqual(
                "--- left\n+++ right\n@@ -8,5 +8,5 @@\n line 8\n line 9\n-line 10\n+line ten\n line 11\n line 12\n",
                diff.UnifiedDiff);
        }

        [TestMethod]
        public void Diff_AgainstEmptyText_RemovesEveryLine()
        {
            // Arrange / Act
            var diff = CompareText.Diff("a\nb", string.Empty, "left", "right");

            // Assert
            Assert.AreEqual(2, diff.RemovedLines);
            Assert.AreEqual(0, diff.AddedLines);
            StringAssert.Contains(diff.UnifiedDiff, "@@ -1,2 +0,0 @@");
        }

        [TestMethod]
        public void Diff_InsertionsAndDeletions_ReproduceTheRightSide()
        {
            // Arrange
            var left = new[] { "a", "b", "c", "d", "e", "f" };
            var right = new[] { "a", "x", "c", "d", "y", "z", "f" };

            // Act
            var script = CompareText.Compute(left, right);

            // Assert: the kept and inserted lines are the right side, the kept and deleted the left
            CollectionAssert.AreEqual(right, script.Where(l => l.Op != CompareText.DiffOp.Delete).Select(l => l.Text).ToArray());
            CollectionAssert.AreEqual(left, script.Where(l => l.Op != CompareText.DiffOp.Insert).Select(l => l.Text).ToArray());
        }

        [TestMethod]
        public void Truncate_CutsOnALineBoundary()
        {
            // Arrange / Act
            var (text, truncated) = CompareText.Truncate("12345\n67890\nabcde\n", 8);

            // Assert
            Assert.IsTrue(truncated);
            Assert.AreEqual("12345\n", text);
        }
    }
}
