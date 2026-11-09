using System;
using System.IO;
using libEDSsharp;
using Xunit;

namespace Tests
{
    public class DocumentationGenMarkupTests
    {
        static ODentry Entry(ushort index, string name, bool disabled, bool customerVisible)
        {
            var od = new ODentry
            {
                objecttype = ObjectType.VAR,
                parameter_name = name,
                accesstype = EDSsharp.AccessType.ro,
                datatype = DataType.UNSIGNED8,
                defaultvalue = "0",
                Index = index
            };
            od.prop.CO_disabled = disabled;
            od.prop.CO_customerVisible = customerVisible;
            return od;
        }

        static string Generate(bool customerOnly)
        {
            var eds = new EDSsharp();
            eds.ods.Add(0x1000, Entry(0x1000, "Comm", false, true));
            eds.ods.Add(0x2000, Entry(0x2000, "Mine", false, true));
            eds.ods.Add(0x6000, Entry(0x6000, "Disabled profile", true, true));
            eds.ods.Add(0x6001, Entry(0x6001, "Hidden profile", false, false));

            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".md");
            try
            {
                new DocumentationGenMarkup().genmddoc(path, eds, customerOnly);
                return File.ReadAllText(path);
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        [Fact]
        public void Customer_markdown_omits_chapter_heading_with_no_visible_entries()
        {
            string md = Generate(true);

            Assert.Contains("Communication Specific Parameters", md);
            Assert.Contains("Manufacturer Specific Parameters", md);
            Assert.DoesNotContain("Device Profile Specific Parameters", md);
            Assert.DoesNotContain("Hidden profile", md);
        }

        [Fact]
        public void Full_markdown_keeps_chapter_heading_when_an_entry_is_printed()
        {
            string md = Generate(false);

            Assert.Contains("Device Profile Specific Parameters", md);
            Assert.Contains("Hidden profile", md);
            Assert.DoesNotContain("Disabled profile", md);
        }
    }
}
