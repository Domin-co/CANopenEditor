using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using libEDSsharp;

namespace ODEditor
{
    public partial class Preferences : Form
    {
        public Preferences()
        {
            InitializeComponent();

            comboBox_exporter.DataSource = Enum.GetValues(typeof(ExporterFactory.Exporter));

            comboBox_exporter.SelectedItem = (ExporterFactory.Exporter)Properties.Settings.Default.ExporterType;

            UInt32 mask = Properties.Settings.Default.WarningMask;

            if ((mask & 0x01) == 0x01)
                checkBox_genericwarning.Checked = true;
            if ((mask & 0x02) == 0x02)
                checkBox_renamewarning.Checked = true;
            if ((mask & 0x04) == 0x04)
                checkBox_buildwarning.Checked = true;
            if ((mask & 0x08) == 0x08)
                checkBox_stringwarning.Checked = true;
            if ((mask & 0x10) == 0x10)
                checkBox_structwarning.Checked = true;

            textBox_wordtemplate.Text = Properties.Settings.Default.WordTemplatePath;
            textBox_pandoc.Text = Properties.Settings.Default.PandocPath;
            textBox_wordstart.Text = Properties.Settings.Default.WordStartHeading;
        }

        /// <summary>
        /// Push the saved Word export settings into the exporter. Empty values fall back to
        /// the exporter's environment variable / default lookup.
        /// </summary>
        internal static void ApplyWordSettings()
        {
            DocumentationGenWord.TemplatePath = NullIfEmpty(Properties.Settings.Default.WordTemplatePath);
            DocumentationGenWord.PandocPath = NullIfEmpty(Properties.Settings.Default.PandocPath);
            DocumentationGenWord.StartAtHeading = NullIfEmpty(Properties.Settings.Default.WordStartHeading);
        }

        private static string NullIfEmpty(string s)
        {
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }

        private void button_browsetemplate_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Filter = "Word templates (*.dotx;*.docx)|*.dotx;*.docx|All files (*.*)|*.*";
                if (ofd.ShowDialog() == DialogResult.OK)
                    textBox_wordtemplate.Text = ofd.FileName;
            }
        }

        private void button_browsepandoc_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Filter = "pandoc (pandoc.exe)|pandoc.exe|All files (*.*)|*.*";
                if (ofd.ShowDialog() == DialogResult.OK)
                    textBox_pandoc.Text = ofd.FileName;
            }
        }

        private void button_close_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void button_save_Click(object sender, EventArgs e)
        {

            ExporterFactory.Exporter exporter;
            Enum.TryParse<ExporterFactory.Exporter>(comboBox_exporter.SelectedValue.ToString(), out exporter);
            Properties.Settings.Default.ExporterType = (int)exporter;

            UInt32 mask = 0xFFE0;

            if (checkBox_genericwarning.Checked)
                mask |= 0x01;
            if (checkBox_renamewarning.Checked)
                mask |= 0x02;
            if (checkBox_buildwarning.Checked)
                mask |= 0x04;
            if (checkBox_stringwarning.Checked)
                mask |= 0x08;
            if (checkBox_structwarning.Checked)
                mask |= 0x10;

            Properties.Settings.Default.WarningMask = mask;

            Warnings.warning_mask = mask;

            Properties.Settings.Default.WordTemplatePath = textBox_wordtemplate.Text.Trim();
            Properties.Settings.Default.PandocPath = textBox_pandoc.Text.Trim();
            Properties.Settings.Default.WordStartHeading = textBox_wordstart.Text.Trim();
            ApplyWordSettings();

            Properties.Settings.Default.Save();
            this.Close();
        }

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {

        }
    }
}
