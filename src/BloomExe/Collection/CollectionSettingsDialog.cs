using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Bloom.Book;
using Bloom.Properties;
using L10NSharp;
using SIL.Reporting;
using SIL.Windows.Forms.WritingSystems;
using SIL.Extensions;
using SIL.WritingSystems;
using Bloom.TeamCollection;
using Bloom.MiscUI;
using Bloom.web.controllers;
using Bloom.Api;
using Bloom.Publish.BloomLibrary;
using SIL.IO;
using SIL.Windows.Forms.SettingProtection;
using System.Collections.Generic;
using System.Diagnostics;
using Bloom.WebLibraryIntegration;

namespace Bloom.Collection
{
    public partial class CollectionSettingsDialog
        : SIL.Windows.Forms.Miscellaneous.FormForUsingPortableClipboard
    {
        public delegate CollectionSettingsDialog Factory(); //autofac uses this

        private readonly CollectionSettings _collectionSettings;
        private readonly QueueRenameOfCollection _queueRenameOfCollection;
        private readonly XMatterPackFinder _xmatterPackFinder;
        private bool _restartRequired;
        private bool _loaded;
        private string _subscriptionCode;
        private string _brand;
        private bool _settingsProtectionRequirePassword;
        private bool _settingsProtectionNormallyHidden;
        private bool _currentCollectionIsTeamCollection;

        // Pending values edited through the CollectionSettingsApi
        private string _pendingBookshelf;
        public string PendingDefaultBookshelf
        {
            set
            {
                if (value != _collectionSettings.DefaultBookshelf)
                    Invoke((Action)ChangeThatRequiresRestart);
                _pendingBookshelf = value;
            }
            get { return _pendingBookshelf; }
        }

        // "Internal" so CollectionSettingsApi can update these.
        internal readonly string[] PendingFontSelections = new[] { "", "", "" };
        internal string PendingNumberingStyle { get; set; }
        internal string PendingXmatter { get; set; }
        internal string PendingAdministrators { get; set; }

        internal WritingSystem PendingLanguage1;
        internal WritingSystem PendingLanguage2;
        internal WritingSystem PendingLanguage3;
        internal WritingSystem PendingSignLanguage;

        // Ugly I know, but we need to be able to access these by an index number sometimes.
        internal WritingSystem[] PendingLanguages = new WritingSystem[3];

        public CollectionSettingsDialog(
            CollectionSettings collectionSettings,
            QueueRenameOfCollection queueRenameOfCollection,
            TeamCollectionManager tcManager,
            XMatterPackFinder xmatterPackFinder
        )
        {
            _collectionSettings = collectionSettings;
            _queueRenameOfCollection = queueRenameOfCollection;
            _xmatterPackFinder = xmatterPackFinder;
            _settingsProtectionRequirePassword = SettingsProtectionSingleton
                .Settings
                .RequirePassword;
            _settingsProtectionNormallyHidden = SettingsProtectionSingleton.Settings.NormallyHidden;
            InitializeComponent();

            _language1Name.UseMnemonic = false; // Allow & to be part of the language display names.
            _language2Name.UseMnemonic = false; // This may be unlikely, but can't be ruled out.
            _language3Name.UseMnemonic = false; // See https://issues.bloomlibrary.org/youtrack/issue/BL-9919.

            PendingLanguage1 = _collectionSettings.Language1.Clone();
            PendingLanguage2 = _collectionSettings.Language2.Clone();
            PendingLanguage3 = _collectionSettings.Language3.Clone();
            PendingSignLanguage = _collectionSettings.SignLanguage.Clone();
            PendingLanguages[0] = PendingLanguage1;
            PendingLanguages[1] = PendingLanguage2;
            PendingLanguages[2] = PendingLanguage3;

            PendingFontSelections[0] = _collectionSettings.AllLanguages[0].FontName;
            PendingFontSelections[1] = _collectionSettings.AllLanguages[1].FontName;
            var have3rdLanguage = _collectionSettings.AllLanguages[2] != null;
            PendingFontSelections[2] = have3rdLanguage
                ? _collectionSettings.AllLanguages[2].FontName
                : "";
            PendingNumberingStyle = _collectionSettings.PageNumberStyle;
            PendingXmatter = _collectionSettings.XMatterPackName;
            PendingAdministrators = _collectionSettings.AdministratorsDisplayString;
            CollectionSettingsApi.DialogBeingEdited = this;
            // Currently, ExperimentalFeatures.IsFeatureEnabled(ExperimentalFeatures.kTeamCollections)
            // can be false when we're in a team collection, as the user can open a preexisting TC
            // (and then access the TC tab in Collection Settings) without checking/enabling
            // Team Collections under Experimental Features
            _currentCollectionIsTeamCollection =
                tcManager.CurrentCollectionEvenIfDisconnected != null;

            _showExperimentalBookSources.Checked = ExperimentalFeatures.IsFeatureEnabled(
                ExperimentalFeatures.kExperimentalSourceBooks
            );
            _allowTeamCollection.Checked = ExperimentalFeatures.IsFeatureEnabled(
                ExperimentalFeatures.kTeamCollections
            );
            _allowSpreadsheetImportExport.Checked = ExperimentalFeatures.IsFeatureEnabled(
                ExperimentalFeatures.kSpreadsheetImportExport
            );

            if (
                !ExperimentalFeatures.IsFeatureEnabled(ExperimentalFeatures.kTeamCollections)
                && tcManager.CurrentCollectionEvenIfDisconnected == null
            )
            {
                this._tab.Controls.Remove(this._teamCollectionTab);
            }

            if (_collectionSettings.LockedToOneDownloadedBook)
            {
                // Don't give the slightest encouragement to making a download-for-edit collection into a team collection.
                _tab.Controls.Remove(this._teamCollectionTab);
            }
            // Don't allow the user to disable the Team Collection feature if we're currently in a Team Collection.
            _allowTeamCollection.Enabled = !(
                _allowTeamCollection.Checked
                && tcManager.CurrentCollectionEvenIfDisconnected != null
            );

            // AutoUpdate applies only to Windows: see https://silbloom.myjetbrains.com/youtrack/issue/BL-2317.
            // Also, we are stranding pre-windows 10 people at 5.4.
            if (
                SIL.PlatformUtilities.Platform.IsWindows
                && Environment.OSVersion.Version.Major >= 10
            )
                _automaticallyUpdate.Checked = Settings.Default.AutoUpdate;
            else
                _automaticallyUpdate.Hide();

            // Without this, PendingDefaultBookshelf stays null unless the user changes it.
            // The result is the bookshelf selection gets cleared when other collection settings are saved. See BL-10093.
            PendingDefaultBookshelf = _collectionSettings.DefaultBookshelf;

            CollectionSettingsApi.BrandingChangeHandler = ChangeBranding;

            TeamCollectionApi.TheOneInstance.SetCallbackToReopenCollection(() =>
            {
                _restartRequired = true;
                ReactDialog.CloseCurrentModal(); // close the top Create dialog
                _okButton_Click(null, null); // close this dialog
            });

            UpdateDisplay();

            if (FixingEnterpriseSubscriptionCode)
            {
                _tab.SelectedTab = _enterpriseTab;
            }

            if (tcManager.CurrentCollectionEvenIfDisconnected == null)
            {
                _noRenameTeamCollectionLabel.Visible = false;
            }
            else
            {
                _bloomCollectionName.Enabled = false;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // BL-552, BL-779: a bug in Mono requires us to wait to set Icon until handle created.
            this.Icon = global::Bloom.Properties.Resources.BloomIcon;
        }

        public void SetDesiredTab(string tab)
        {
            if (tab == "enterprise")
                _tab.SelectedTab = _enterpriseTab;
        }

        private void UpdateDisplay()
        {
            var lang1UiName = PendingLanguage1.Name;
            var lang2UiName = PendingLanguage2.Name;
            _language1Name.Text = string.Format("{0} ({1})", lang1UiName, PendingLanguage1.Tag);
            _language2Name.Text = string.Format("{0} ({1})", lang2UiName, PendingLanguage2.Tag);
            const string unsetLanguageName = "--";
            if (string.IsNullOrEmpty(PendingLanguage3.Tag))
            {
                _language3Name.Text = unsetLanguageName;
                _removeLanguage3Link.Visible = false;
                _changeLanguage3Link.Text = LocalizationManager.GetString(
                    "CollectionSettingsDialog.LanguageTab.SetThirdLanguageLink",
                    "Set...",
                    "If there is no third or sign language specified, the link changes to this."
                );
            }
            else
            {
                var lang3UiName = PendingLanguage3.Name;
                _language3Name.Text = string.Format("{0} ({1})", lang3UiName, PendingLanguage3.Tag);
                _removeLanguage3Link.Visible = true;
                _changeLanguage3Link.Text = LocalizationManager.GetString(
                    "CollectionSettingsDialog.LanguageTab.ChangeLanguageLink",
                    "Change..."
                );
            }

            if (string.IsNullOrEmpty(PendingSignLanguage.Tag))
            {
                _signLanguageName.Text = unsetLanguageName;
                _removeSignLanguageLink.Visible = false;
                _changeSignLanguageLink.Text = LocalizationManager.GetString(
                    "CollectionSettingsDialog.LanguageTab.SetThirdLanguageLink",
                    "Set...",
                    "If there is no third or sign language specified, the link changes to this."
                );
            }
            else
            {
                var signLangUiName = PendingSignLanguage.Name;
                _signLanguageName.Text = string.Format(
                    "{0} ({1})",
                    signLangUiName,
                    PendingSignLanguage.Tag
                );
                _removeSignLanguageLink.Visible = true;
                _changeSignLanguageLink.Text = LocalizationManager.GetString(
                    "CollectionSettingsDialog.LanguageTab.ChangeLanguageLink",
                    "Change..."
                );
            }

            _restartReminder.Visible = AnyReasonToRestart();
            _okButton.Text = AnyReasonToRestart()
                ? LocalizationManager.GetString(
                    "CollectionSettingsDialog.Restart",
                    "Restart",
                    "If you make certain changes in the settings dialog, the OK button changes to this."
                )
                : LocalizationManager.GetString("Common.OKButton", "&OK");
        }

        private void _language1ChangeLink_LinkClicked(
            object sender,
            LinkLabelLinkClickedEventArgs e
        )
        {
            var potentiallyCustomName = PendingLanguage1.Name;

            void onLanguageChange(LanguageChangeEventArgs args)
            {
                PendingLanguage1.Tag = args.LanguageTag;
                PendingLanguage1.SetName(args.DesiredName, args.DesiredName != args.DefaultName);
                ChangeThatRequiresRestart();
            }
            ChangeLanguage(onLanguageChange, PendingLanguage1.Tag, potentiallyCustomName);
        }

        private void _language2ChangeLink_LinkClicked(
            object sender,
            LinkLabelLinkClickedEventArgs e
        )
        {
            var potentiallyCustomName = PendingLanguage2.Name;
            void onLanguageChange(LanguageChangeEventArgs args)
            {
                PendingLanguage2.Tag = args.LanguageTag;
                PendingLanguage2.SetName(args.DesiredName, args.DesiredName != args.DefaultName);
                ChangeThatRequiresRestart();
            }
            ChangeLanguage(onLanguageChange, PendingLanguage2.Tag, potentiallyCustomName);
        }

        private void _language3ChangeLink_LinkClicked(
            object sender,
            LinkLabelLinkClickedEventArgs e
        )
        {
            var potentiallyCustomName = PendingLanguage3.Name;
            void onLanguageChange(LanguageChangeEventArgs args)
            {
                PendingLanguage3.Tag = args.LanguageTag;
                PendingLanguage3.SetName(args.DesiredName, args.DesiredName != args.DefaultName);
                ChangeThatRequiresRestart();
            }
            ChangeLanguage(onLanguageChange, PendingLanguage3.Tag, potentiallyCustomName);
        }

        private void _removeSecondNationalLanguageButton_LinkClicked(
            object sender,
            LinkLabelLinkClickedEventArgs e
        )
        {
            PendingLanguage3.ChangeTag(string.Empty); // null causes a crash in trying to set it again (BL-5795)
            PendingLanguage3.SetName(string.Empty, false);
            ChangeThatRequiresRestart();
        }

        private void _signLanguageChangeLink_LinkClicked(
            object sender,
            LinkLabelLinkClickedEventArgs e
        )
        {
            var potentiallyCustomName = PendingSignLanguage.Name;
            void onLanguageChange(LanguageChangeEventArgs args)
            {
                PendingSignLanguage.Tag = args.LanguageTag;
                var slIsCustom = args.DefaultName != args.DesiredName;
                PendingSignLanguage.SetName(args.DesiredName, slIsCustom);
                ChangeThatRequiresRestart();
            }
            ChangeLanguage(onLanguageChange, PendingSignLanguage.Tag, potentiallyCustomName);
        }

        private void _removeSignLanguageButton_LinkClicked(
            object sender,
            LinkLabelLinkClickedEventArgs e
        )
        {
            PendingSignLanguage.ChangeTag(string.Empty);
            PendingSignLanguage.SetName(string.Empty, false);
            ChangeThatRequiresRestart();
        }

        public static void ChangeLanguage(
            Action<LanguageChangeEventArgs> onLanguageChange,
            string languageIdentifier,
            string potentiallyCustomName = null
        )
        {
            // There shouldn't be listeners at this point, but clear just in case any are left over from a previous dialog opening
            CollectionSettingsApi.UnsubscribeAllLanguageChangeListeners();
            EventHandler<LanguageChangeEventArgs> onLanguageChangeListener = null;
            onLanguageChangeListener = delegate(object sender, LanguageChangeEventArgs args)
            {
                onLanguageChange(args);
                CollectionSettingsApi.LanguageChange -= onLanguageChangeListener;
            };
            CollectionSettingsApi.LanguageChange += onLanguageChangeListener;

            using (
                var dlg = new ReactDialog(
                    "languageChooserBundle",
                    new
                    {
                        initialLanguageTag = languageIdentifier,
                        initialCustomName = potentiallyCustomName
                    }
                )
            )
            {
                dlg.Width = 1000;
                dlg.Height = 580;

                dlg.ShowDialog(Shell.GetShellOrOtherOpenForm());
            }
        }

        private void _okButton_Click(object sender, EventArgs e)
        {
            Logger.WriteMinorEvent("Settings Dialog OK Clicked");

            // Validate before we save the settings
            if (_currentCollectionIsTeamCollection)
            {
                if (!CollectionSettings.ValidateAdministrators(PendingAdministrators))
                {
                    // The user has entered invalid email address(es)
                    BloomMessageBox.ShowWarning(
                        LocalizationManager.GetString(
                            "TeamCollection.InvalidAdminEmails",
                            "Please enter one or more valid administrator email addresses, separated by commas or spaces."
                        )
                    );
                    return;
                }

                _collectionSettings.ModifyAdministrators(PendingAdministrators);
            }

            CollectionSettingsApi.DialogBeingEdited = null;

            Settings.Default.AutoUpdate =
                _automaticallyUpdate.Checked && Environment.OSVersion.Version.Major >= 10;
            UpdateExperimentalBookSources();
            UpdateTeamCollectionAllowed();
            UpdateSpreadsheetImportExportAllowed();

            _collectionSettings.Country = _countryText.Text.Trim();
            _collectionSettings.Province = _provinceText.Text.Trim();
            _collectionSettings.District = _districtText.Text.Trim();

            _collectionSettings.PageNumberStyle = PendingNumberingStyle; // non-localized key

            var oldBrand = _collectionSettings.BrandingProjectKey;
            _collectionSettings.BrandingProjectKey = _brand;
            _collectionSettings.SubscriptionCode = _subscriptionCode;
            // An early version of this code allowed a download-for-edit collection to be unlocked once a valid
            // branding code was provided. We decided not to do that, but here is the code we'd reinstate if we change our minds.
            // We've definitely selected some branding, even if it's the default. If necessary, we have a valid code
            // for it. So if this collection was created using the "Download for editing" button on Blorg
            // and has been getting special permission to use some branding because of that, it no longer needs
            // that special permission. Nor does the rule that books can't be added to such a collection apply
            // any longer. And in case the user has now selected a different branding, we no longer want the book to use the
            // branding we downloaded it with. A simple way to achieve all this is to delete the file (if any) that
            // constitutes our record that this is a collection made using "Download for editing".
            // (Everything that depends on it gets cleaned up when we restart bloom with the new branding.)
            //var downloadEditPath = Path.Combine(
            //    _collectionSettings.FolderPath,
            //    BloomLibraryPublishModel.kNameOfDownloadForEditFile
            //);
            //RobustFile.Delete(downloadEditPath);

            // We don't know which if any of the new branding's bookshelves we should upload to by default,
            // but it will certainly be wrong to upload to one that belongs to some previous branding.
            if (oldBrand != _brand)
                _collectionSettings.DefaultBookshelf = "";

            string xmatterKeyForcedByBranding =
                _collectionSettings.GetXMatterPackNameSpecifiedByBrandingOrNull();
            PendingXmatter = this._xmatterPackFinder.GetValidXmatter(
                xmatterKeyForcedByBranding,
                PendingXmatter
            );
            _collectionSettings.XMatterPackName = PendingXmatter;

            //no point in letting them have the Nat lang 2 be the same as 1
            if (PendingLanguage2.Tag == PendingLanguage3.Tag)
            {
                PendingLanguage3.ChangeTag(String.Empty);
                PendingLanguage3.SetName(String.Empty, false);
            }

            UpdateLanguageSettings(
                _collectionSettings.AllLanguages,
                PendingLanguages,
                PendingFontSelections
            );

            _collectionSettings.SignLanguage.ChangeTag(PendingSignLanguage.Tag);
            if (!String.IsNullOrEmpty(PendingSignLanguage.Tag))
                _collectionSettings.SignLanguage.SetName(
                    PendingSignLanguage.Name,
                    PendingSignLanguage.IsCustomName
                );

            if (_bloomCollectionName.Text.Trim() != _collectionSettings.CollectionName)
            {
                _queueRenameOfCollection.Raise(_bloomCollectionName.Text.SanitizeFilename('-'));
                //_collectionSettings.PrepareToRenameCollection(_bloomCollectionName.Text.SanitizeFilename('-'));
            }
            Logger.WriteEvent("Closing Settings Dialog");

            _collectionSettings.DefaultBookshelf = PendingDefaultBookshelf;
            _collectionSettings.Save();
            Close();

            DialogResult = AnyReasonToRestart() ? DialogResult.Yes : DialogResult.OK;
        }

        // internal and static to facilitate unit testing
        internal static void UpdateLanguageSettings(
            List<WritingSystem> languages,
            WritingSystem[] pendingLanguages,
            string[] pendingFonts
        )
        {
            Debug.Assert(languages.Count >= 3);
            Debug.Assert(pendingLanguages.Length == 3);
            Debug.Assert(pendingFonts.Length == 3);

            // Provide some useful abbreviations for the first 3 languages.
            // (This method is static so that it can be tested without creating a dialog.)
            var Language1 = languages[0];
            var Language2 = languages[1];
            var Language3 = languages.Count > 2 ? languages[2] : null;
            var PendingLanguage1 = pendingLanguages[0];
            var PendingLanguage2 = pendingLanguages[1];
            var PendingLanguage3 = pendingLanguages[2];

            // NOTE: if one of the first 3 languages is replaced, we need to add it to
            // the list after the first 3.  If one of the first 3 languages was already
            // in the list, we need to remove it from its old position.

            // Copy the old Language1 if it's not in the first 3 languages.
            if (
                Language1.Tag != PendingLanguage1.Tag
                && Language1.Tag != PendingLanguage2.Tag
                && Language1.Tag != PendingLanguage3.Tag
            )
            {
                languages.Add(Language1.Clone()); // need a fresh copy
            }
            // Copy the old Language2 if it's not in the first 3 languages, and is not
            // the same as the old Language1.
            if (
                Language2.Tag != PendingLanguage1.Tag
                && Language2.Tag != PendingLanguage2.Tag
                && Language2.Tag != PendingLanguage3.Tag
                && Language2.Tag != Language1.Tag
            )
            {
                languages.Add(Language2.Clone());
            }
            // Copy the old Language3 if it exists and is not in the first 3 languages, and
            // is not the same as either the old Language1 or the old Language2.
            if (
                !String.IsNullOrEmpty(Language3.Tag)
                && Language3.Tag != PendingLanguage1.Tag
                && Language3.Tag != PendingLanguage2.Tag
                && Language3.Tag != PendingLanguage3.Tag
                && Language3.Tag != Language1.Tag
                && Language3.Tag != Language2.Tag
            )
            {
                languages.Add(Language3.Clone());
            }
            // Remove the languages that are now in the first 3 languages from later in the list
            // if they were in the list after the first 3 languages.
            for (int i = languages.Count - 1; i >= 3; i--)
            {
                if (languages[i].Tag == PendingLanguage1.Tag)
                {
                    languages.RemoveAt(i);
                }
                else if (languages[i].Tag == PendingLanguage2.Tag)
                {
                    languages.RemoveAt(i);
                }
                else if (languages[i].Tag == PendingLanguage3.Tag)
                {
                    languages.RemoveAt(i);
                }
            }
            // Update the values in the first three languages.
            for (int i = 0; i < 3; i++)
            {
                if (languages[i] == null)
                    continue;
                languages[i].FontName = pendingFonts[i];
                languages[i].IsRightToLeft = pendingLanguages[i].IsRightToLeft;
                languages[i].LineHeight = pendingLanguages[i].LineHeight;
                languages[i].BaseUIFontSizeInPoints = pendingLanguages[i].BaseUIFontSizeInPoints;
                languages[i].BreaksLinesOnlyAtSpaces = pendingLanguages[i].BreaksLinesOnlyAtSpaces;
            }

            Language1.ChangeTag(PendingLanguage1.Tag);
            Language1.SetName(PendingLanguage1.Name, PendingLanguage1.IsCustomName);
            Language2.ChangeTag(PendingLanguage2.Tag);
            if (!String.IsNullOrEmpty(PendingLanguage2.Tag))
                Language2.SetName(PendingLanguage2.Name, PendingLanguage2.IsCustomName);
            Language3.ChangeTag(PendingLanguage3.Tag);
            if (!String.IsNullOrEmpty(PendingLanguage3.Tag))
                Language3.SetName(PendingLanguage3.Name, PendingLanguage3.IsCustomName);
        }

        private bool XMatterChangePending
        {
            get { return PendingXmatter != _collectionSettings.XMatterPackName; }
        }

        /// <summary>
        /// Internal so api can trigger this.
        /// </summary>
        internal void ChangeThatRequiresRestart()
        {
            if (!_loaded) //ignore false events that come while setting upt the dialog
                return;

            _restartRequired = true;
            UpdateDisplay();
        }

        private bool AnyReasonToRestart()
        {
            return _restartRequired || XMatterChangePending;
        }

        /// <summary>
        /// Client that is launching dialog sets this if we are running the dialog for the purpose of
        /// fixing a subscription code. It forces the Enterprise tab and shows the branding that
        /// the code is needed for.
        /// </summary>
        public bool FixingEnterpriseSubscriptionCode;

        private void OnLoad(object sender, EventArgs e)
        {
            _countryText.Text = _collectionSettings.Country;
            _provinceText.Text = _collectionSettings.Province;
            _districtText.Text = _collectionSettings.District;
            _bloomCollectionName.Text = _collectionSettings.CollectionName;
            _brand = _collectionSettings.BrandingProjectKey;
            _subscriptionCode = _collectionSettings.SubscriptionCode;
            // Set the branding as an (incomplete) code if we are running with a legacy branding
            if (
                CollectionSettingsApi.LegacyBrandingName != null
                && string.IsNullOrEmpty(_subscriptionCode)
            )
            {
                _subscriptionCode = CollectionSettingsApi.LegacyBrandingName;
            }
            CollectionSettingsApi.SetSubscriptionCode(
                _subscriptionCode,
                _collectionSettings.IsSubscriptionCodeKnown(),
                _collectionSettings.GetEnterpriseStatus(FixingEnterpriseSubscriptionCode)
            );
            _loaded = true;
            Logger.WriteEvent("Entered Settings Dialog");
        }

        private void _cancelButton_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            // Restore original value if we cancel this dialog.
            if (
                SettingsProtectionSingleton.Settings.RequirePassword
                    != _settingsProtectionRequirePassword
                || SettingsProtectionSingleton.Settings.NormallyHidden
                    != _settingsProtectionNormallyHidden
            )
            {
                SettingsProtectionSingleton.Settings.RequirePassword =
                    _settingsProtectionRequirePassword;
                SettingsProtectionSingleton.Settings.NormallyHidden =
                    _settingsProtectionNormallyHidden;
                SettingsProtectionSingleton.Settings.Save();
            }
            CollectionSettingsApi.DialogBeingEdited = null;
            Close();
        }

        private void _helpButton_Click(object sender, EventArgs e)
        {
            if (_tab.SelectedTab == tabPage1)
                HelpLauncher.Show(this, "Tasks/Basic_tasks/Change_languages.htm");
            else if (_tab.SelectedTab == _bookMakingTab)
                HelpLauncher.Show(
                    this,
                    "Tasks/Basic_tasks/Select_front_matter_or_back_matter_from_a_pack.htm"
                );
            else if (_tab.SelectedTab == tabPage3)
                HelpLauncher.Show(this, "Tasks/Basic_tasks/Enter_project_information.htm");
            else if (_tab.SelectedTab == _enterpriseTab)
                HelpLauncher.Show(this, "Tasks/Basic_tasks/Select_Bloom_Enterprise_Status.htm");
            else
                HelpLauncher.Show(this, "User_Interface/Dialog_boxes/Settings_dialog_box.htm");
        }

        private void _bloomCollectionName_TextChanged(object sender, EventArgs e)
        {
            if (_bloomCollectionName.Text.Trim() == _collectionSettings.CollectionName)
                return;

            ChangeThatRequiresRestart();
        }

        private void _showExperimentalBookSources_CheckedChanged(object sender, EventArgs e)
        {
            ChangeThatRequiresRestart();
        }

        private void UpdateExperimentalBookSources()
        {
            ExperimentalFeatures.SetValue(
                ExperimentalFeatures.kExperimentalSourceBooks,
                _showExperimentalBookSources.Checked
            );
        }

        public bool FontSettingsLinkClicked(int zeroBasedLanguageNumber)
        {
            var pendingLanguage = PendingLanguages[zeroBasedLanguageNumber];
            using (var frm = new ScriptSettingsDialog())
            {
                frm.LanguageName = pendingLanguage.Name;
                frm.LanguageRightToLeft = pendingLanguage.IsRightToLeft;
                frm.LanguageLineSpacing = pendingLanguage.LineHeight;
                frm.UIFontSize = pendingLanguage.BaseUIFontSizeInPoints;
                frm.BreakLinesOnlyAtSpaces = pendingLanguage.BreaksLinesOnlyAtSpaces;
                frm.ShowDialog();

                // get the changes

                // We usually don't need to restart, just gather the changes up. The caller
                // will save the .bloomCollection file. Later when a book
                // is edited, defaultLangStyles.css will be written out in the book's folder, which is all
                // that is needed for this setting to take effect.
                pendingLanguage.LineHeight = frm.LanguageLineSpacing;
                pendingLanguage.BreaksLinesOnlyAtSpaces = frm.BreakLinesOnlyAtSpaces;
                pendingLanguage.BaseUIFontSizeInPoints = frm.UIFontSize;
                pendingLanguage.IsRightToLeft = frm.LanguageRightToLeft;
                return pendingLanguage.IsRightToLeft
                    != _collectionSettings.AllLanguages[zeroBasedLanguageNumber].IsRightToLeft;
            }
        }

        /// <summary>
        /// We configure the SettingsApi to use this method to notify this (as the manager of the whole dialog
        /// including the "need to reload" message and the Ok/Cancel buttons) of changes the user makes
        /// in the Enterprise tab.
        /// </summary>
        /// <param name="fullBrandingName"></param>
        /// <param name="subscriptionCode"></param>
        /// <returns></returns>
        public bool ChangeBranding(string fullBrandingName, string subscriptionCode)
        {
            if (
                _brand != fullBrandingName
                || DifferentSubscriptionCodes(subscriptionCode, _subscriptionCode)
            )
            {
                Invoke((Action)ChangeThatRequiresRestart);
                _brand = fullBrandingName;
                _subscriptionCode = subscriptionCode;
                //if (BrandingProject.HaveFilesForBranding(fullBrandingName))
                //{
                //	// if the branding.json specifies an xmatter, set the default for this collection to that.
                //	var correspondingXMatterPack = BrandingSettings.GetSettingsOrNull(fullBrandingName).GetXmatterToUse();
                //	if (!string.IsNullOrEmpty(correspondingXMatterPack))
                //	{
                //		_collectionSettings.XMatterPackName = correspondingXMatterPack;
                //	}
                //}
                return true;
            }
            return false;
        }

        private bool DifferentSubscriptionCodes(string code1, string code2)
        {
            if (string.IsNullOrEmpty(code1) && string.IsNullOrEmpty(code2))
                return false;
            return code1 != code2;
        }

        private void _numberStyleCombo_SelectedIndexChanged(object sender, EventArgs e)
        {
            ChangeThatRequiresRestart();
        }

        private void _allowTeamCollection_CheckedChanged(object sender, EventArgs e)
        {
            ChangeThatRequiresRestart();
        }

        private void UpdateTeamCollectionAllowed()
        {
            ExperimentalFeatures.SetValue(
                ExperimentalFeatures.kTeamCollections,
                _allowTeamCollection.Checked
            );
        }

        private void _allowSpreadsheetImportExport_CheckedChanged(object sender, EventArgs e)
        {
            ChangeThatRequiresRestart();
        }

        private void UpdateSpreadsheetImportExportAllowed()
        {
            ExperimentalFeatures.SetValue(
                ExperimentalFeatures.kSpreadsheetImportExport,
                _allowSpreadsheetImportExport.Checked
            );
        }
    }
}
