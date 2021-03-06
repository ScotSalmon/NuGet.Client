// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using NuGet.Common;

namespace NuGet.Configuration
{
    internal sealed class SettingsFile
    {
        /// <summary>
        /// Full path to the settings file
        /// </summary>
        public string ConfigFilePath => Path.GetFullPath(Path.Combine(DirectoryPath, FileName));

        /// <summary>
        /// Folder under which the settings file is present
        /// </summary>
        internal string DirectoryPath { get; }

        /// <summary>
        /// Filename of the settings file
        /// </summary>
        internal string FileName { get; }

        /// <summary>
        /// Next config file to read in the hierarchy
        /// </summary>
        internal SettingsFile Next { get; private set; }

        /// <summary>
        /// Defines if the configuration settings have been changed but have not been saved to disk
        /// </summary>
        internal bool IsDirty { get; set; }

        /// <summary>
        /// Defines if the settings file is considered a machine wide settings file
        /// </summary>
        /// <remarks>Machine wide settings files cannot be eddited.</remarks>
        internal bool IsMachineWide { get; }

        /// <summary>
        /// Order in which the files will be read.
        /// A larger number means closer to the user.
        /// </summary>
        internal int Priority { get; private set; }

        /// <summary>
        /// XML element for settings file
        /// </summary>
        private readonly XDocument _xDocument;

        /// <summary>
        /// Root element of configuration file.
        /// By definition of a nuget.config, the root element has to be a 'configuration' element
        /// </summary>
        private readonly NuGetConfiguration _rootElement;

        /// <summary>
        /// Creates an instance of a non machine wide SettingsFile with the default filename.
        /// </summary>
        /// <param name="directoryPath">path to the directory where the file is</param>
        public SettingsFile(string directoryPath)
            : this(directoryPath, Settings.DefaultSettingsFileName, isMachineWide: false)
        {
        }

        /// <summary>
        /// Creates an instance of a non machine wide SettingsFile.
        /// </summary>
        /// <param name="directoryPath">path to the directory where the file is</param>
        /// <param name="fileName">name of config file</param>
        public SettingsFile(string directoryPath, string fileName)
            : this(directoryPath, fileName, isMachineWide: false)
        {
        }

        /// <summary>
        /// Creates an instance of a SettingsFile
        /// </summary>
        /// <remarks>It will parse the specified document,
        /// if it doesn't exist it will create one with the default configuration.</remarks>
        /// <param name="directoryPath">path to the directory where the file is</param>
        /// <param name="fileName">name of config file</param>
        /// <param name="isMachineWide">specifies if the SettingsFile is machine wide</param>
        public SettingsFile(string directoryPath, string fileName, bool isMachineWide)
        {
            if (string.IsNullOrEmpty(directoryPath))
            {
                throw new ArgumentException(Resources.Argument_Cannot_Be_Null_Or_Empty, nameof(directoryPath));
            }

            if (string.IsNullOrEmpty(fileName))
            {
                throw new ArgumentException(Resources.Argument_Cannot_Be_Null_Or_Empty, nameof(fileName));
            }

            if (!FileSystemUtility.IsPathAFile(fileName))
            {
                throw new ArgumentException(Resources.Settings_FileName_Cannot_Be_A_Path, nameof(fileName));
            }

            DirectoryPath = directoryPath;
            FileName = fileName;
            IsMachineWide = isMachineWide;
            Priority = 0;

            XDocument config = null;
            ExecuteSynchronized(() =>
            {
                config = XmlUtility.GetOrCreateDocument(CreateDefaultConfig(), ConfigFilePath);
            });

            _xDocument = config;

            _rootElement = new NuGetConfiguration(_xDocument.Root, origin: this);
        }

        /// <summary>
        /// Gets the section with a given name.
        /// </summary>
        /// <param name="sectionName">name to match sections</param>
        /// <returns>null if no section with the given name was found</returns>
        public SettingSection GetSection(string sectionName)
        {
            return _rootElement.GetSection(sectionName);
        }

        /// <summary>
        /// Adds or updates the given <paramref name="item"/> to the settings.
        /// </summary>
        /// <param name="sectionName">section where the <paramref name="item"/> has to be added. If this section does not exist, one will be created.</param>
        /// <param name="item">item to be added to the settings.</param>
        /// <returns>true if the item was successfully updated or added in the settings</returns>
        internal void AddOrUpdate(string sectionName, SettingItem item)
        {
            _rootElement.AddOrUpdate(sectionName, item);
        }

        /// <summary>
        /// Removes the given <paramref name="item"/> from the settings.
        /// If the <paramref name="item"/> is the last item in the section, the section will also be removed.
        /// </summary>
        /// <param name="sectionName">Section where the <paramref name="item"/> is stored. If this section does not exist, the method will throw</param>
        /// <param name="item">item to be removed from the settings</param>
        /// <remarks> If the SettingsFile is a machine wide config this method will throw</remarks>
        internal void Remove(string sectionName, SettingItem item)
        {
            _rootElement.Remove(sectionName, item);
        }

        /// <summary>
        /// Flushes any in-memory updates in the SettingsFile to disk.
        /// </summary>
        internal void SaveToDisk()
        {
            if (IsDirty)
            {
                ExecuteSynchronized(() =>
                {
                    FileSystemUtility.AddFile(ConfigFilePath, _xDocument.Save);
                });

                IsDirty = false;
            }
        }

        internal bool IsEmpty() => _rootElement == null || _rootElement.IsEmpty();

        /// <remarks>
        /// This method gives you a reference to the actual abstraction instead of a clone of it.
        /// It should be used only when intended. For most purposes you should be able to use
        /// GetSection(...) instead.
        /// </remarks>
        internal bool TryGetSection(string sectionName, out SettingSection section)
        {
           return _rootElement.Sections.TryGetValue(sectionName, out section);
        }

        internal static void ConnectSettingsFilesLinkedList(IList<SettingsFile> settingFiles)
        {
            if (settingFiles == null && !settingFiles.Any())
            {
                throw new ArgumentException(Resources.Argument_Cannot_Be_Null_Or_Empty, nameof(settingFiles));
            }

            settingFiles.First().Priority = settingFiles.Count;

            if (settingFiles.Count > 1)
            {
                // if multiple setting files were loaded, chain them in a linked list
                for (var i = 1; i < settingFiles.Count; ++i)
                {
                    settingFiles[i].SetNextFile(settingFiles[i - 1]);
                }
            }
        }

        internal void SetNextFile(SettingsFile settingsFile)
        {
            Next = settingsFile;
            Priority = settingsFile.Priority - 1;
        }

        internal void MergeSectionsInto(Dictionary<string, VirtualSettingSection> sectionsContainer)
        {
            _rootElement.MergeSectionsInto(sectionsContainer);
        }

        private XDocument CreateDefaultConfig()
        {
            var configurationElement = new NuGetConfiguration(this);
            return new XDocument(configurationElement.AsXNode());
        }

        private void ExecuteSynchronized(Action ioOperation)
        {
            ConcurrencyUtilities.ExecuteWithFileLocked(filePath: ConfigFilePath, action: () =>
            {
                try
                {
                    ioOperation();
                }
                catch (InvalidOperationException e)
                {
                    throw new NuGetConfigurationException(
                        string.Format(CultureInfo.CurrentCulture, Resources.ShowError_ConfigInvalidOperation, ConfigFilePath, e.Message), e);
                }

                catch (UnauthorizedAccessException e)
                {
                    throw new NuGetConfigurationException(
                        string.Format(CultureInfo.CurrentCulture, Resources.ShowError_ConfigUnauthorizedAccess, ConfigFilePath, e.Message), e);
                }

                catch (XmlException e)
                {
                    throw new NuGetConfigurationException(
                        string.Format(CultureInfo.CurrentCulture, Resources.ShowError_ConfigInvalidXml, ConfigFilePath, e.Message), e);
                }

                catch (Exception e)
                {
                    throw new NuGetConfigurationException(
                        string.Format(CultureInfo.CurrentCulture, Resources.Unknown_Config_Exception, ConfigFilePath, e.Message), e);
                }
            });
        }
    }
}
