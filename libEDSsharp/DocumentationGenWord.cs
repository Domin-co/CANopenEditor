/*
    This file is part of libEDSsharp.

    libEDSsharp is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    libEDSsharp is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with libEDSsharp.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace libEDSsharp
{
    /// <summary>
    /// Word (.docx) documentation generator. Renders the markdown documentation with pandoc,
    /// using an optional reference document for styles, then restyles the tables.
    /// </summary>
    public class DocumentationGenWord : IFileExporter
    {
        /// <summary>
        /// Path to pandoc. When null the EDSSHARP_PANDOC and PYPANDOC_PANDOC environment variables,
        /// the per-user Windows install location and finally PATH are tried in that order.
        /// </summary>
        public static string PandocPath { get; set; }

        /// <summary>
        /// Word template (.dotx/.docx) passed to pandoc as --reference-doc. When null the
        /// EDSSHARP_WORD_TEMPLATE environment variable is used; if that is unset pandoc's default styles apply.
        /// </summary>
        public static string TemplatePath { get; set; }

        /// <summary>
        /// If set, markdown before the first occurrence of this text is discarded, for example
        /// "# 0x2025 - BuildVersion". When null the EDSSHARP_WORD_START environment variable is used.
        /// </summary>
        public static string StartAtHeading { get; set; }

        /// <summary>
        /// Lowest object dictionary index included in the document. Everything before the first
        /// index at or above this value (cover details, PDO mapping, lower chapters) is dropped.
        /// 0 keeps everything. Defaults to 0x2000, the manufacturer specific range.
        /// </summary>
        public static int FirstIndex { get; set; } = 0x2000;

        /// <summary>
        /// Fetches all the different fileexporter types the class supports
        /// </summary>
        /// <returns>List of the different exporters the class supports</returns>
        public ExporterDescriptor[] GetExporters()
        {
            return new ExporterDescriptor[]
            {
                new ExporterDescriptor("Documentation Word", new string[] { ".docx" }, ExporterDescriptor.ExporterFlags.Documentation, delegate (string filepath, List<EDSsharp> edss)
                {
                    var e = new DocumentationGenWord();
                    e.genworddoc(filepath, edss[0]);
                })
            };
        }

        /// <summary>
        /// Generate Word documentation
        /// </summary>
        /// <param name="filepath">where the .docx should be created</param>
        /// <param name="eds">data to generate the documentation from</param>
        /// <param name="customerOnly">if true, only objects marked "Customer Visible" are included</param>
        /// <param name="author">document author; defaults to the file's last modifier or the current user</param>
        public void genworddoc(string filepath, EDSsharp eds, bool customerOnly = false, string author = null)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "edssharp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string rawMd = Path.Combine(tempDir, "raw.md");
                string pandocMd = Path.Combine(tempDir, "document.md");

                new DocumentationGenMarkup().genmddoc(rawMd, eds, customerOnly);

                string md = File.ReadAllText(rawMd, Encoding.UTF8);
                md = PrepareMarkdown(md, ResolveAuthor(eds, author), DateTime.Today, StartAtHeading ?? Environment.GetEnvironmentVariable("EDSSHARP_WORD_START"), FirstIndex);
                File.WriteAllText(pandocMd, md, new UTF8Encoding(false));

                RunPandoc(pandocMd, filepath);
                FormatTables(filepath);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>
        /// Trim, re-level headings and prepend the pandoc title block.
        /// </summary>
        /// <param name="md">markdown from <see cref="DocumentationGenMarkup"/></param>
        /// <param name="author">author for the title block</param>
        /// <param name="date">date for the title block</param>
        /// <param name="startAtHeading">discard everything before this text, if found; null or empty to keep everything</param>
        /// <returns>markdown ready for pandoc</returns>
        /// <param name="firstIndex">lowest object dictionary index to include; everything before the first index at or above it is dropped, 0 keeps everything</param>
        public static string PrepareMarkdown(string md, string author, DateTime date, string startAtHeading = null, int firstIndex = 0)
        {
            if (!string.IsNullOrEmpty(startAtHeading))
            {
                int pos = md.IndexOf(startAtHeading, StringComparison.Ordinal);
                if (pos != -1)
                {
                    // "# 0x2025" also matches inside "### 0x2025"; keep the whole heading marker.
                    while (pos > 0 && md[pos - 1] == '#')
                        pos--;
                    md = md.Substring(pos);
                }
            }
            // The title block already names the document, so drop the generated top heading.
            md = Regex.Replace(md, @"^\s*CANopen device documentation[ \t]*\r?\n=+[ \t]*\r?\n", "");
            // Drop the generated cover block (product name, description, file details table and
            // "generated by" line) so the document starts at Device Information.
            Match deviceInfo = Regex.Match(md, @"^Device Information[ \t]*\r?\n-{3,}", RegexOptions.Multiline);
            if (deviceInfo.Success && deviceInfo.Index > 0 && md.Substring(0, deviceInfo.Index).Contains("automatically generated by"))
                md = md.Substring(deviceInfo.Index);
            // pandoc builds a real table of contents with --toc; the "[TOC]" marker is not understood by it.
            md = Regex.Replace(md, @"^\[TOC\][ \t]*\r?\n", "", RegexOptions.Multiline);
            if (firstIndex > 0)
                md = CutBeforeIndex(md, firstIndex);
            md = RemoveEmptyChapters(md);
            md = PromoteHeadings(md);
            // The three title block lines must be consecutive for pandoc to treat them as title, author and date.
            return "% Object Dictionary\n% Author: " + author + "\n% Date: " + date.ToString("yyyy-MM-dd") + "\n\n" + md;
        }

        static readonly Regex ChapterHeading = new Regex(@"^[^\r\n]+ Specific Parameters[ \t]*\r?\n-{3,}[ \t]*\r?\n", RegexOptions.Multiline);
        static readonly Regex IndexHeading = new Regex(@"^#{1,3} 0x[0-9A-Fa-f]{4}\b", RegexOptions.Multiline);

        /// <summary>
        /// Remove chapter headings (for example Device Profile Specific Parameters) that have no
        /// index entries beneath them. The generator writes the heading as soon as it reaches the range,
        /// even when every entry in it is disabled or hidden.
        /// </summary>
        static string RemoveEmptyChapters(string md)
        {
            MatchCollection chapters = ChapterHeading.Matches(md);
            if (chapters.Count == 0)
                return md;

            var sb = new StringBuilder(md.Substring(0, chapters[0].Index));
            for (int i = 0; i < chapters.Count; i++)
            {
                int start = chapters[i].Index;
                int end = i + 1 < chapters.Count ? chapters[i + 1].Index : md.Length;
                string section = md.Substring(start, end - start);
                if (IndexHeading.IsMatch(section))
                    sb.Append(section);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Make index entries level 1 ("### 0x..." becomes "# 0x...") while chapter headings, which the
        /// generator underlines with "---", stay level 2.
        /// </summary>
        static string PromoteHeadings(string md)
        {
            return Regex.Replace(md, @"^### 0x", "# 0x", RegexOptions.Multiline);
        }

        /// <summary>
        /// Keep only the text from the first "# 0xNNNN" index heading at or above firstIndex onwards,
        /// together with a chapter heading (e.g. Manufacturer Specific Parameters) directly above it.
        /// If there is no such index the result is empty.
        /// </summary>
        static string CutBeforeIndex(string md, int firstIndex)
        {
            foreach (Match m in Regex.Matches(md, @"^#{1,3} 0x([0-9A-Fa-f]{4})\b", RegexOptions.Multiline))
            {
                if (Convert.ToInt32(m.Groups[1].Value, 16) < firstIndex)
                    continue;

                int start = m.Index;
                Match chapter = Regex.Match(md.Substring(0, start), @"[^\r\n]+ Specific Parameters[ \t]*\r?\n-{3,}[ \t]*\r?\n\s*\z");
                if (chapter.Success)
                    start = chapter.Index;
                return md.Substring(start);
            }
            return "";
        }

        static string ResolveAuthor(EDSsharp eds, string author)
        {
            if (!string.IsNullOrWhiteSpace(author))
                return author;
            if (!string.IsNullOrWhiteSpace(eds.fi.ModifiedBy))
                return eds.fi.ModifiedBy;
            if (!string.IsNullOrWhiteSpace(eds.fi.CreatedBy))
                return eds.fi.CreatedBy;
            // Split CamelCase user names, "JamesScott" -> "James Scott"
            return string.Join(" ", Regex.Matches(Environment.UserName, "[A-Z][^A-Z]*").Cast<Match>().Select(m => m.Value));
        }

        static string ResolvePandoc()
        {
            string[] candidates =
            {
                PandocPath,
                Environment.GetEnvironmentVariable("EDSSHARP_PANDOC"),
                Environment.GetEnvironmentVariable("PYPANDOC_PANDOC"),
                Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "Pandoc", "pandoc.exe")
            };
            foreach (string c in candidates)
            {
                if (!string.IsNullOrEmpty(c) && File.Exists(c))
                    return c;
            }
            return "pandoc";
        }

        static void RunPandoc(string markdownFile, string outputFile)
        {
            string template = TemplatePath ?? Environment.GetEnvironmentVariable("EDSSHARP_WORD_TEMPLATE");
            var args = new StringBuilder();
            args.Append("-f markdown -t docx --toc --toc-depth=2 ");
            if (!string.IsNullOrEmpty(template))
            {
                if (!File.Exists(template))
                    throw new FileNotFoundException("Word template not found", template);
                args.Append("--reference-doc=\"" + template + "\" ");
            }
            args.Append("-o \"" + outputFile + "\" \"" + markdownFile + "\"");

            var psi = new ProcessStartInfo(ResolvePandoc(), args.ToString())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            Process p;
            try
            {
                p = Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new InvalidOperationException("Could not start pandoc. Install it from https://pandoc.org or set DocumentationGenWord.PandocPath / EDSSHARP_PANDOC.", ex);
            }
            using (p)
            {
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                    throw new InvalidOperationException("pandoc failed (exit code " + p.ExitCode + "): " + err);
            }
        }

        /// <summary>
        /// Restyle every table in a .docx: autofit to contents, centred cells, banded rows,
        /// rule lines under the header and at the bottom, indented to match body text.
        /// </summary>
        /// <param name="docxPath">document to modify in place</param>
        public static void FormatTables(string docxPath)
        {
            const string shade = "F2F2F2";

            using (var doc = WordprocessingDocument.Open(docxPath, true))
            {
                Body body = doc.MainDocumentPart.Document.Body;
                string indent = BodyIndentDxa(doc);

                foreach (Table table in body.Descendants<Table>())
                {
                    List<TableRow> rows = table.Elements<TableRow>().ToList();
                    if (rows.Count == 0)
                        continue;

                    SetAutofit(table);

                    // A first row that is one merged cell is a note row; the header is the next row.
                    int headerIdx = rows[0].Elements<TableCell>().Select(c => c.InnerText).Distinct().Count() == 1 ? 1 : 0;
                    if (headerIdx >= rows.Count)
                        headerIdx = rows.Count - 1;

                    for (int r = 0; r < rows.Count; r++)
                    {
                        bool isNote = r < headerIdx;
                        bool isHeader = r == headerIdx;
                        int dataRow = r - headerIdx - 1;

                        foreach (TableCell cell in rows[r].Elements<TableCell>())
                        {
                            foreach (Paragraph para in cell.Elements<Paragraph>())
                            {
                                ParagraphProperties pPr = para.ParagraphProperties ?? para.PrependChild(new ParagraphProperties());
                                pPr.Indentation = new Indentation { Left = "0", Right = "0" };
                                pPr.Justification = new Justification { Val = JustificationValues.Center };

                                foreach (Run run in para.Elements<Run>())
                                {
                                    RunProperties rPr = run.RunProperties ?? run.PrependChild(new RunProperties());
                                    if (isNote)
                                        rPr.Italic = new Italic();
                                    else if (isHeader)
                                        rPr.Bold = new Bold { Val = false };
                                }
                            }

                            if (!isNote && !isHeader && dataRow % 2 == 1)
                            {
                                GetCellProps(cell).Shading = new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = shade };
                            }
                        }
                    }

                    SetBorders(table, rows, headerIdx);

                    TableProperties tblPr = table.GetFirstChild<TableProperties>();
                    tblPr.TableIndentation = new TableIndentation { Width = int.Parse(indent), Type = TableWidthUnitValues.Dxa };
                }

                PushTitleBelowHeaderArt(doc);
                PutContentsOnOwnPage(doc.MainDocumentPart, body);
                if (PageBreakPerIndex)
                    BreakBeforeEachIndex(body);

                doc.MainDocumentPart.Document.Save();
            }
        }

        /// <summary>
        /// Start every object dictionary index (a level 1 heading beginning "0x") on a new page.
        /// </summary>
        public static bool PageBreakPerIndex { get; set; } = true;

        static string StyleOf(Paragraph p)
        {
            return p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        }

        /// <summary>
        /// Put the table of contents on its own page: a break before its heading, and a break
        /// before whatever follows it.
        /// </summary>
        static void PutContentsOnOwnPage(MainDocumentPart main, Body body)
        {
            SdtBlock toc = body.Elements<SdtBlock>()
                .FirstOrDefault(b => b.Descendants<Paragraph>().Any(p => StyleOf(p) == "TOCHeading"));
            if (toc == null)
                return;

            Paragraph heading = toc.Descendants<Paragraph>().First(p => StyleOf(p) == "TOCHeading");
            SetPageBreakBefore(heading);
            DefineContentsHeadingStyle(main);

            // The Title style carries outline level 1, which the contents field would list. Opt it out.
            Paragraph title = body.Elements<Paragraph>().FirstOrDefault(p => StyleOf(p) == "Title");
            if (title != null)
            {
                ParagraphProperties tPr = title.ParagraphProperties ?? title.PrependChild(new ParagraphProperties());
                tPr.OutlineLevel = new OutlineLevel { Val = 9 };
            }

            OpenXmlElement next = toc.ElementsAfter().FirstOrDefault(e => e is Paragraph || e is Table);
            if (next is Paragraph)
                SetPageBreakBefore((Paragraph)next);
            else if (next != null)
                next.InsertBeforeSelf(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
        }

        /// <summary>
        /// pandoc marks the contents heading with the TOCHeading style but the template does not define it,
        /// so it renders as body text. Define it as Heading 1 in appearance, with outline level body text so
        /// the contents list does not list itself.
        /// </summary>
        static void DefineContentsHeadingStyle(MainDocumentPart main)
        {
            Styles styles = main.StyleDefinitionsPart?.Styles;
            if (styles == null || styles.Elements<Style>().Any(st => st.StyleId != null && st.StyleId.Value == "TOCHeading"))
                return;

            var style = new Style(
                new StyleName { Val = "TOC Heading" },
                new BasedOn { Val = "Heading1" },
                new NextParagraphStyle { Val = "Normal" },
                new UIPriority { Val = 39 },
                new UnhideWhenUsed(),
                new PrimaryStyle(),
                new StyleParagraphProperties(new OutlineLevel { Val = 9 }))
            {
                Type = StyleValues.Paragraph,
                StyleId = "TOCHeading"
            };
            styles.Append(style);
            styles.Save();
        }

        static void SetPageBreakBefore(Paragraph p)
        {
            ParagraphProperties pPr = p.ParagraphProperties ?? p.PrependChild(new ParagraphProperties());
            pPr.PageBreakBefore = new PageBreakBefore();
        }

        /// <summary>
        /// Set page-break-before on each index heading. If a chapter heading (level 2) sits directly
        /// above the index, the break goes on that heading instead so it is not left alone on the previous page.
        /// </summary>
        static void BreakBeforeEachIndex(Body body)
        {
            foreach (Paragraph p in body.Elements<Paragraph>().ToList())
            {
                if (StyleOf(p) != "Heading1" || !p.InnerText.TrimStart().StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    continue;

                Paragraph target = p;
                Paragraph prev = p.PreviousSibling<Paragraph>();
                if (prev != null && StyleOf(prev) == "Heading2")
                    target = prev;

                ParagraphProperties pPr = target.ParagraphProperties ?? target.PrependChild(new ParagraphProperties());
                pPr.PageBreakBefore = new PageBreakBefore();
            }
        }

        const long EmuPerTwip = 635;
        const int TitleGapDxa = 284; // about 0.5 cm

        /// <summary>
        /// Templates may place a logo in the first-page header as a floating picture. Those do not
        /// push body text down, so the title would overlap them. Find the lowest such picture and
        /// add enough space before the Title paragraph to clear it.
        /// </summary>
        static void PushTitleBelowHeaderArt(WordprocessingDocument doc)
        {
            MainDocumentPart main = doc.MainDocumentPart;
            Body body = main.Document.Body;

            Paragraph title = body.Elements<Paragraph>()
                .FirstOrDefault(p => p.ParagraphProperties?.ParagraphStyleId?.Val?.Value == "Title");
            if (title == null)
                return;

            SectionProperties sect = body.Descendants<SectionProperties>().FirstOrDefault(s => s.GetFirstChild<TitlePage>() != null);
            HeaderReference href = sect?.Elements<HeaderReference>().FirstOrDefault(h => h.Type != null && h.Type.Value == HeaderFooterValues.First);
            if (href == null)
                return;
            HeaderPart headerPart = main.GetPartById(href.Id.Value) as HeaderPart;
            if (headerPart == null)
                return;

            PageMargin margin = sect.GetFirstChild<PageMargin>();
            PageSize size = sect.GetFirstChild<PageSize>();
            if (margin == null || size == null || margin.Top == null || size.Height == null)
                return;

            long topMargin = margin.Top.Value;
            long headerDistance = margin.Header != null ? (long)margin.Header.Value : 0;
            long pageHeight = (long)size.Height.Value;

            long lowest = 0;
            foreach (var anchor in headerPart.Header.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.Anchor>())
            {
                var v = anchor.VerticalPosition;
                if (v == null || v.RelativeFrom == null || v.PositionOffset == null || anchor.Extent == null)
                    continue;

                long offset;
                if (!long.TryParse(v.PositionOffset.Text, out offset))
                    continue;

                long origin;
                if (v.RelativeFrom.Value == DocumentFormat.OpenXml.Drawing.Wordprocessing.VerticalRelativePositionValues.Page)
                    origin = 0;
                else if (v.RelativeFrom.Value == DocumentFormat.OpenXml.Drawing.Wordprocessing.VerticalRelativePositionValues.Margin)
                    origin = topMargin;
                else if (v.RelativeFrom.Value == DocumentFormat.OpenXml.Drawing.Wordprocessing.VerticalRelativePositionValues.Paragraph)
                    origin = headerDistance;
                else
                    continue;

                long bottom = origin + (offset + anchor.Extent.Cy.Value) / EmuPerTwip;

                // Ignore page-sized backgrounds and anything that is not in the upper half of the page.
                if (bottom > 0 && bottom < pageHeight / 2 && bottom > lowest)
                    lowest = bottom;
            }

            if (lowest == 0)
                return;

            long before = lowest + TitleGapDxa - topMargin;
            if (before <= 0)
                return;

            ParagraphProperties pPr = title.ParagraphProperties ?? title.PrependChild(new ParagraphProperties());
            SpacingBetweenLines spacing = pPr.SpacingBetweenLines ?? (pPr.SpacingBetweenLines = new SpacingBetweenLines());
            spacing.Before = before.ToString();
        }

        static TableCellProperties GetCellProps(TableCell cell)
        {
            return cell.TableCellProperties ?? cell.PrependChild(new TableCellProperties());
        }

        static void SetAutofit(Table table)
        {
            TableProperties tblPr = table.GetFirstChild<TableProperties>();
            if (tblPr == null)
                tblPr = table.PrependChild(new TableProperties());

            tblPr.TableLayout = new TableLayout { Type = TableLayoutValues.Autofit };
            tblPr.TableWidth = new TableWidth { Type = TableWidthUnitValues.Auto, Width = "0" };

            TableGrid grid = table.GetFirstChild<TableGrid>();
            if (grid != null)
            {
                foreach (GridColumn col in grid.Elements<GridColumn>())
                    col.Width = "0";
            }

            foreach (TableCell cell in table.Descendants<TableCell>())
                GetCellProps(cell).TableCellWidth = new TableCellWidth { Type = TableWidthUnitValues.Auto, Width = "0" };
        }

        static void SetBorders(Table table, List<TableRow> rows, int headerIdx)
        {
            TableProperties tblPr = table.GetFirstChild<TableProperties>();
            tblPr.TableBorders = new TableBorders(
                new TopBorder { Val = BorderValues.Nil },
                new LeftBorder { Val = BorderValues.Nil },
                new BottomBorder { Val = BorderValues.Nil },
                new RightBorder { Val = BorderValues.Nil },
                new InsideHorizontalBorder { Val = BorderValues.Nil },
                new InsideVerticalBorder { Val = BorderValues.Nil });

            bool headerIsLast = headerIdx == rows.Count - 1;
            foreach (TableCell cell in rows[headerIdx].Elements<TableCell>())
            {
                GetCellProps(cell).TableCellBorders = new TableCellBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 8, Color = "000000" },
                    new BottomBorder { Val = BorderValues.Single, Size = 8, Color = "000000" });
            }
            if (!headerIsLast)
            {
                foreach (TableCell cell in rows[rows.Count - 1].Elements<TableCell>())
                {
                    GetCellProps(cell).TableCellBorders = new TableCellBorders(
                        new BottomBorder { Val = BorderValues.Single, Size = 8, Color = "000000" });
                }
            }
        }

        /// <summary>
        /// Left indent of body text in dxa: first non-empty body paragraph with an explicit indent,
        /// else the Normal style's indent, else one inch.
        /// </summary>
        static string BodyIndentDxa(WordprocessingDocument doc)
        {
            const string fallback = "1440";

            foreach (Paragraph p in doc.MainDocumentPart.Document.Body.Elements<Paragraph>())
            {
                string left = p.ParagraphProperties?.Indentation?.Left?.Value;
                if (!string.IsNullOrWhiteSpace(p.InnerText) && left != null)
                    return left;
            }

            Styles styles = doc.MainDocumentPart.StyleDefinitionsPart?.Styles;
            Style normal = styles?.Elements<Style>().FirstOrDefault(s => s.StyleId != null && s.StyleId.Value == "Normal");
            string normalLeft = normal?.StyleParagraphProperties?.Indentation?.Left?.Value;
            return normalLeft ?? fallback;
        }
    }
}
