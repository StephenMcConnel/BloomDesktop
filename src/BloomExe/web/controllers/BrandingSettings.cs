﻿using Newtonsoft.Json;
using SIL.IO;
using SIL.Linq;
using System;
using System.Diagnostics;
using System.Dynamic;
using System.IO; // Add this for Path operations
using System.Linq;
using System.Text;

namespace Bloom.Api
{
    /// <summary>
    /// Supports branding (e.g. logos, CC License) needed by projects.
    /// Currently we don't allow the image server to see these requests, which always occur in xmatter.
    /// Instead, as part of the process of bringing xmatter up to date, we change the image src attributes
    /// to point to the svg or png file which we copy into the book folder.
    /// This process (in XMatterHelper.CleanupBrandingImages()) allows the books to look right when
    /// opened in a browser and also in BloomReader. (It would also help with making Epubs, though that
    /// code is already written to handle branding.)
    /// Keeping this class active (a) because most of its logic is used by CleanupBrandingImages(),
    /// and (b) as a safety net, in case there's some way an api/branding url still gets presented
    /// to the image server.
    /// </summary>
    public class BrandingSettings
    {
        public const string kBrandingImageUrlPart = "branding/image";
        private static readonly object _cacheLock = new object();

        /// <summary>
        /// Find the requested branding image file for the given branding, looking for a .png file if the .svg file does not exist.
        /// </summary>
        /// <remarks>
        /// This method is used by EpubMaker as well as here in BrandingApi.
        /// </remarks>
        /* JDH Sep 2020 commenting out because I found this to be unused by anything
         public static string FindBrandingImageFileIfPossible(string branding, string filename, Layout layout)
        {
            string path;
            if (layout.SizeAndOrientation.IsLandScape)
            {
                // we will first try to find a landscape-specific image
                var ext = Path.GetExtension(filename);
                var filenameNoExt = Path.ChangeExtension(filename, null);
                var landscapeFileName = Path.ChangeExtension(filenameNoExt + "-landscape", ext);
                path = BloomFileLocator.GetOptionalBrandingFile(branding, landscapeFileName);
                if (!string.IsNullOrEmpty(path))
                    return path;
                path = BloomFileLocator.GetOptionalBrandingFile(branding, Path.ChangeExtension(landscapeFileName, "png"));
                if (!string.IsNullOrEmpty(path))
                    return path;
            }
            // Note: in Bloom 3.7, our Firefox, when making PDFs, would render svg's as blurry. This was fixed in Bloom 3.8 with
            // a new Firefox. So SVGs are requested by the html...
            path = BloomFileLocator.GetOptionalBrandingFile(branding, filename);

            // ... but if there is no SVG, we can actually send back a PNG instead, and that works fine:
            if(string.IsNullOrEmpty(path))
                path = BloomFileLocator.GetOptionalBrandingFile(branding, Path.ChangeExtension(filename, "png"));

            // ... and if there is no PNG, look for a "jpg":
            if (string.IsNullOrEmpty(path))
                path = BloomFileLocator.GetOptionalBrandingFile(branding, Path.ChangeExtension(filename, "jpg"));

            return path;
        }
        */

        public class PresetItem
        {
            // this was originally only for data-book, but it may now also used for other presets, in which case it's just the "key" of the preset
            [JsonProperty("data-book")]
            public string DataBook;

            // used when you don't want to introduce a data-book item, just supply a string to something. The `content` is the string.
            [JsonProperty("key")]
            public string Key;

            [JsonProperty("lang")]
            public string Lang;

            [JsonProperty("content")]
            public string Content;

            [JsonProperty("condition")]
            public string Condition; // one of always (override), ifEmpty (default), ifAllCopyrightEmpty
        }

        public class Settings
        {
            [JsonProperty("presets")]
            public PresetItem[] Presets;

            [JsonProperty("appearance")]
            public ExpandoObject Appearance;

            public string GetXmatterToUse()
            {
                var x = this.Presets.FirstOrDefault(p => p.DataBook == "xmatter");
                return x?.Content;
            }

            public string GetPresetKeyValueOrDefault(string key, string defaultValue)
            {
                var kv = this.Presets.FirstOrDefault(p => p.Key == key);
                return kv?.Content ?? defaultValue;
            }
        }

        /// <summary>
        /// We sometimes want temporary subscriptions that use the default branding files, but which provide
        /// access to Enterprise level features.  The base branding names for these subscriptions are listed
        /// here.  (This is useful for workshops and other training venues.)  See BL-14872.
        ///
        /// There are also enterprise users who want the features but do not care to have custom branding.
        /// We also list those here for now.
        /// </summary>
        public static string[] SubscriptionsThatUseDefaultBranding = new[]
        {
            "BloomProgram",
            "Pretend-Project",
            // Real enterprise descriptors which just use the default branding.
            "Gereja-Protestan-Maluku"
        };

        /// <summary>
        /// extract the various parts of a Subscription Descriptor
        /// </summary>
        /// <param name="subscriptionDescriptor">the part of the subcription code before the numbers start</param>
        /// <param name="folderName">the name before any flavor or subUnitName; this will match the folder holding all the files.</param>
        /// <param name="flavor">a name or empty string</param>
        /// <param name="subUnitName">a name (normally a country) or empty string</param>
        public static void ParseSubscriptionDescriptor(
            String subscriptionDescriptor,
            out String folderName,
            out String flavor,
            out String subUnitName
        )
        {
            if (subscriptionDescriptor.ToLowerInvariant().EndsWith("-lc"))
            {
                folderName = "Local-Community";
                flavor = "";
                subUnitName = "";
                return;
            }
            if (subscriptionDescriptor.ToLowerInvariant().EndsWith("-pro"))
            {
                // Pro tier doesn't have brandings, so we use the default.
                folderName = "Default";
                flavor = "";
                subUnitName = "";
                return;
            }
            if (subscriptionDescriptor.ToLowerInvariant().EndsWith("-trainer"))
            {
                // Trainer subscriptions don't have brandings, so we use the default.
                folderName = "Default";
                flavor = "";
                subUnitName = "";
                return;
            }

            // A Subscription Descriptor may optionally have a suffix of the form "[FLAVOR]" where flavor is typically
            // a language name. This is used to select different logo files without having to create
            // a completely separate branding folder (complete with summary, stylesheets, etc) for each
            // language in a project that is publishing in a situation with multiple major languages.
            var parts = subscriptionDescriptor.Split('[');
            folderName = parts[0];
            flavor = parts.Length > 1 ? parts[1].Replace("]", "") : "";

            // A Subscription Descriptor may optionally have a suffix of the form "(SUBUNIT)" where subUnitName is typically
            // a country name. This is useful when the project wants different codes, but wants *exactly*
            // the same branding.
            parts = folderName.Split('(');
            folderName = parts[0];
            subUnitName = parts.Length > 1 ? parts[1].Replace(")", "") : "";

            // If the branding is for a subscription that uses the default branding, we want to use the "Default" folder.
            // In this case, the flavor and subUnitName are irrelevant, so we set them to empty strings.
            if (BrandingSettings.SubscriptionsThatUseDefaultBranding.Contains(folderName))
            {
                folderName = "Default";
                flavor = "";
                subUnitName = "";
            }
        }

        /// <summary>
        /// branding folders can optionally contain a branding.json file which aligns with this Settings class
        /// </summary>
        /// <param name="brandingNameOrFolderPath"> Normally, the branding is just a name, which we look up in the official branding folder
        //but unit tests can instead provide a path to the folder.
        /// </param>

        public static Settings CachedBrandingSettings;
        private static string CachedBrandingSettingsName;
        private static string CachedBrandingSettingsPath;
        private static DateTime CachedBrandingSettingsLastModified;

        public static Settings GetSettingsOrNull(string brandingNameOrFolderPath)
        {
            lock (_cacheLock)
            {
                if (CachedBrandingSettingsName == brandingNameOrFolderPath)
                {
                    if (
                        !Debugger.IsAttached // in production, we don't want to check this
                        || RobustFile.GetLastWriteTimeUtc(CachedBrandingSettingsPath) // during development, we may be changing the branding.json file
                            == CachedBrandingSettingsLastModified
                    )
                    {
                        return CachedBrandingSettings;
                    }
                }
            }

            try
            {
                string settingsPath = null;
                string brandingFolderName = null;
                string flavor = null;

                // First check if this is a direct path to a folder
                if (Directory.Exists(brandingNameOrFolderPath))
                {
                    // If it's a directory path, look for branding.json directly in that folder
                    settingsPath = Path.Combine(brandingNameOrFolderPath, "branding.json");
                    if (!RobustFile.Exists(settingsPath))
                    {
                        settingsPath = null;
                    }
                }
                else
                {
                    // Regular case: treat it as a branding name/key
                    ParseSubscriptionDescriptor(
                        brandingNameOrFolderPath,
                        out brandingFolderName,
                        out flavor,
                        out var subUnitName
                    );

                    // check to see if we have a special branding.json just for this flavor.
                    // Note that we could instead add code that allows a single branding.json to
                    // have rules that apply only on a flavor basis. As of 4.9, all we have is the
                    // ability for a branding.json (and anything else) to use "{flavor}" anywhere in the
                    // name of an image; this will often be enough to avoid making a new branding.json.
                    // But if we needed to have different boilerplate text, well then we would need to
                    // either use this here mechanism (separate json) or implement the ability to add
                    // "flavor:" to the rules.
                    if (!string.IsNullOrEmpty(flavor))
                    {
                        settingsPath = BloomFileLocator.GetOptionalBrandingFile(
                            brandingFolderName,
                            "branding[" + flavor + "].json"
                        );
                    }

                    // if not, fall back to just "branding.json"
                    if (string.IsNullOrEmpty(settingsPath))
                    {
                        settingsPath = BloomFileLocator.GetOptionalBrandingFile(
                            brandingFolderName,
                            "branding.json"
                        );
                        if (string.IsNullOrEmpty(settingsPath))
                        {
                            // Is the branding missing? If not, it is guaranteed to have a branding.css.
                            var cssPath = BloomFileLocator.GetOptionalBrandingFile(
                                brandingFolderName,
                                "branding.css"
                            );
                            if (string.IsNullOrEmpty(cssPath))
                            {
                                // Branding has not yet shipped. We want the branding.json from the "Missing" branding
                                settingsPath = BloomFileLocator.GetOptionalBrandingFile(
                                    "Missing",
                                    "branding.json"
                                );
                            }
                        }
                    }
                }

                if (!string.IsNullOrEmpty(settingsPath))
                {
                    var content = RobustFile.ReadAllText(settingsPath);
                    if (string.IsNullOrEmpty(content))
                    {
                        NonFatalProblem.Report(
                            ModalIf.Beta,
                            PassiveIf.All,
#if DEBUG
                            // note:  That's the only place I've seen this happen.
                            $"The branding settings at '{settingsPath}' are empty. Sometimes the watch:branding:files command starts emitting empty files."
#else
                            $"The branding settings at '{settingsPath}' are empty. "
#endif
                        );
                        return null;
                    }
                    var settings = JsonConvert.DeserializeObject<Settings>(content);
                    if (settings == null)
                    {
                        NonFatalProblem.Report(
                            ModalIf.Beta,
                            PassiveIf.All,
                            "Trouble reading branding settings",
                            "branding.json of the branding "
                                + brandingNameOrFolderPath
                                + " may be corrupt. It had: "
                                + content
                        );
                        return null;
                    }

                    if (settings.Presets != null && !string.IsNullOrEmpty(flavor))
                    {
                        settings.Presets.ForEach(p =>
                        {
                            if (p.Content != null)
                            {
                                p.Content = p.Content.Replace("{flavor}", flavor);
                            }
                        });
                    }
                    // lock for thread safety
                    lock (_cacheLock)
                    {
                        CachedBrandingSettings = settings;
                        CachedBrandingSettingsName = brandingNameOrFolderPath;
                        CachedBrandingSettingsPath = settingsPath;
                        CachedBrandingSettingsLastModified = RobustFile.GetLastWriteTimeUtc(
                            settingsPath
                        );
                    }
                    return settings;
                }
            }
            catch (Exception e)
            {
                NonFatalProblem.Report(
                    ModalIf.Beta,
                    PassiveIf.All,
                    "Trouble reading branding settings",
                    exception: e
                );
            }
            return null; // it is normal not to find the brandings. We hand out license keys before the brandings have been fully developed and shipped.
        }
        public static string GetSummaryHtml(string branding)
        {
            BrandingSettings.ParseSubscriptionDescriptor(
                branding,
                out var baseKey,
                out var flavor,
                out var subUnitName
            );
            var summaryFile = BloomFileLocator.GetOptionalBrandingFile(baseKey, "summary.htm");
            if (summaryFile == null)
                return "";

            var html = RobustFile.ReadAllText(summaryFile, Encoding.UTF8);
            return html.Replace("{flavor}", flavor).Replace("SUBUNIT", subUnitName);
        }

    }
}
