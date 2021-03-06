﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Claymore.WikiLive.Properties;

namespace Claymore.WikiLive
{
    public partial class OptionsForm : Form
    {
        public OptionsForm()
        {
            InitializeComponent();

            nameTextBox.Text = Settings.Default.IrcUser;
            descriptionTextBox.Text = Settings.Default.IrcDescription;
            languageComboBox.Text = Settings.Default.Language;
            httpsCheckBox.Checked = Settings.Default.HttpsLinks;
        }

        public string User
        {
            get { return nameTextBox.Text; }
        }

        public string Description
        {
            get { return descriptionTextBox.Text; }
        }

        public string Language
        {
            get { return languageComboBox.Text; }
        }

        public bool HttpsLinks
        {
            get { return httpsCheckBox.Checked; }
        }
    }
}
