//-----------------------------------------------------------------------
// <copyright>
// Copyright (C) Ruslan Yakushev for the PHP Manager for IIS project.
//
// This file is subject to the terms and conditions of the Microsoft Public License (MS-PL).
// See http://www.microsoft.com/opensource/licenses.mspx#Ms-PL for more details.
// </copyright>
//----------------------------------------------------------------------- 

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Web.Administration;
using Microsoft.Web.Management.Server;
using Web.Management.PHP.FastCgi;
using Web.Management.PHP.Handlers;

namespace Web.Management.PHP.Config
{   

    /// <summary>
    /// Provides functions to register PHP with IIS and to manage IIS and PHP configuration.
    /// </summary>
    internal sealed class PHPConfigHelper
    {
        private ManagementUnit _managementUnit;

        private ApplicationElement _currentFastCgiApplication;
        private HandlerElement _currentPHPHandler;
        private HandlersCollection _handlersCollection;
        private FastCgiApplicationCollection _fastCgiApplicationCollection;

        public PHPConfigHelper(ManagementUnit mgmtUnit)
        {
            _managementUnit = mgmtUnit;
            Initialize();
        }

        private void ApplyRecommendedFastCgiSettings(string phpDirectory)
        {
            // Set the handler mapping resource type to "File or Folder"
            _currentPHPHandler.ResourceType = ResourceType.Either;

            // Set PHP_FCGI_MAX_REQUESTS and instanceMaxRequests
            EnvironmentVariableElement envVariableElement = _currentFastCgiApplication.EnvironmentVariables["PHP_FCGI_MAX_REQUESTS"];
            if (envVariableElement == null)
            {
                _currentFastCgiApplication.EnvironmentVariables.Add("PHP_FCGI_MAX_REQUESTS", "10000");
            }
            else
            {
                envVariableElement.Value = "10000";
            }
            _currentFastCgiApplication.InstanceMaxRequests = 10000;

            // Set PHPRC
            envVariableElement = _currentFastCgiApplication.EnvironmentVariables["PHPRC"];
            if (envVariableElement == null)
            {
                _currentFastCgiApplication.EnvironmentVariables.Add("PHPRC", phpDirectory);
            }
            else
            {
                envVariableElement.Value = phpDirectory;
            }

            // If monitorChangesTo is supported then set it
            if (_currentFastCgiApplication.MonitorChangesToExists())
            {
                string phpIniFilePath = Path.Combine(phpDirectory, "php.ini");
                _currentFastCgiApplication.MonitorChangesTo = phpIniFilePath;
            }

            _managementUnit.Update();
        }

        private void ApplyRecommendedPHPIniSettings(PHPIniFile file, bool isNewRegistration)
        {
            string phpDirectory = GetPHPDirectory();
            string handlerName = _currentPHPHandler.Name;
            string phpIniPath = file.FileName;

            // Set the recommended php.ini settings
            List<PHPIniSetting> settings = new List<PHPIniSetting>();

            // Set extension directory path
            string value = Path.Combine(phpDirectory, "ext");
            settings.Add(new PHPIniSetting("extension_dir", value, "PHP"));

            // Set log_errors
            settings.Add(new PHPIniSetting("log_errors", "On", "PHP"));

            // Set error_log path
            value = Path.Combine(Environment.ExpandEnvironmentVariables(@"%WINDIR%\Temp\"), handlerName + "_errors.log");
            settings.Add(new PHPIniSetting("error_log", value, "PHP"));

            // Set session path
            value = Environment.ExpandEnvironmentVariables(@"%WINDIR%\Temp\");
            settings.Add(new PHPIniSetting("session.save_path", value, "Session"));

            // Set cgi.force_redirect
            settings.Add(new PHPIniSetting("cgi.force_redirect", "0", "PHP"));
            
            // Set cgi.fix_pathinfo
            settings.Add(new PHPIniSetting("cgi.fix_pathinfo", "1", "PHP"));

            // Enable fastcgi impersonation
            settings.Add(new PHPIniSetting("fastcgi.impersonate", "1", "PHP"));

            if (isNewRegistration)
            {
                // Disable fastcgi logging
                settings.Add(new PHPIniSetting("fastcgi.logging", "0", "PHP"));

                // Set maximum script execution time
                settings.Add(new PHPIniSetting("max_execution_time", "300", "PHP"));

                // Turn off display errors
                settings.Add(new PHPIniSetting("display_errors", "Off", "PHP"));

                // Enable the most common PHP extensions
                List<PHPIniExtension> extensions = new List<PHPIniExtension>();
                extensions.Add(new PHPIniExtension("php_curl.dll", true));
                extensions.Add(new PHPIniExtension("php_gd2.dll", true));
                extensions.Add(new PHPIniExtension("php_gettext.dll", true));
                extensions.Add(new PHPIniExtension("php_mysql.dll", true));
                extensions.Add(new PHPIniExtension("php_mysqli.dll", true));
                extensions.Add(new PHPIniExtension("php_mbstring.dll", true));
                extensions.Add(new PHPIniExtension("php_openssl.dll", true));
                extensions.Add(new PHPIniExtension("php_soap.dll", true));
                extensions.Add(new PHPIniExtension("php_xmlrpc.dll", true));
                file.UpdateExtensions(extensions);
            }

            file.AddOrUpdateSettings(settings);
            file.Save(phpIniPath);
        }

        public string ApplyRecommendedSettings()
        {
            string result = String.Empty;

            // Check if PHP is not registered
            if (_currentFastCgiApplication == null || _currentPHPHandler == null)
            {
                throw new InvalidOperationException("Cannot apply recommended settings because PHP is not registered properly");
            }

            string phpDirectory = GetPHPDirectory();
            string phpIniFilePath = Path.Combine(phpDirectory, "php.ini");
            PHPIniFile phpIniFile = new PHPIniFile(phpIniFilePath);
            phpIniFile.Parse();

            // Apply FastCGI settings if they are not already correct
            if (ValidateFastCgiConfiguration(phpIniFile) != true)
            {
                ApplyRecommendedFastCgiSettings(phpDirectory);
            }

            // Apply PHP settings only if they are not already correct.
            if (ValidatePHPConfiguration(phpIniFile) != true)
            {
                // Make a copy of php.ini just in case
                File.Copy(phpIniFilePath, phpIniFilePath + "-phpmanager", true);
                ApplyRecommendedPHPIniSettings(phpIniFile,  false /* This is an update to an existing PHP registration */);
                result = phpIniFilePath + "-phpmanager";
            }

            return result;
        }

        private void CopyInheritedHandlers()
        {
            if (_managementUnit.ConfigurationPath.PathType == ConfigurationPathType.Server)
            {
                return;
            }

            HandlerElement[] list = new HandlerElement[_handlersCollection.Count];
            ((ICollection)_handlersCollection).CopyTo(list, 0);

            _handlersCollection.Clear();

            foreach (HandlerElement handler in list)
            {
                _handlersCollection.AddCopy(handler);
            }
        }

        private static string GenerateHandlerName(HandlersCollection collection, string phpVersion)
        {
            string prefix = "php-" + phpVersion;
            string name = prefix;

            for (int i = 1; true; i++)
            {
                if (collection[name] != null)
                {
                    name = prefix + "_" + i.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    break;
                }
            }
            return name;
        }

        public ArrayList GetAllPHPVersions()
        {
            ArrayList result = new ArrayList();
            
            foreach (HandlerElement handler in _handlersCollection)
            {
                if (String.Equals(handler.Path, "*.php", StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(handler.ScriptProcessor))
                    {
                        result.Add(new string[] { handler.Name, handler.ScriptProcessor, GetPHPExecutableVersion(handler.ScriptProcessor) });
                    }
                }
            }

            return result;
        }

        public PHPConfigInfo GetPHPConfigInfo()
        {
            // Check if PHP is not registered
            if (_currentFastCgiApplication == null || _currentPHPHandler == null)
            {
                return null;
            }

            PHPConfigInfo configInfo = new PHPConfigInfo();
            configInfo.HandlerName = _currentPHPHandler.Name;
            configInfo.ScriptProcessor = _currentPHPHandler.ScriptProcessor;
            configInfo.Version = GetPHPExecutableVersion(_currentPHPHandler.ScriptProcessor);
            string phpIniPath = GetPHPIniPath();

            if (String.IsNullOrEmpty(phpIniPath))
            {
                throw new FileNotFoundException("php.ini file does not exist");
            }

            configInfo.PHPIniFilePath = phpIniPath;

            PHPIniFile file = new PHPIniFile(phpIniPath);
            file.Parse();

            PHPIniSetting setting = file.GetSetting("error_log");
            if (setting != null)
            {
                configInfo.ErrorLog = setting.Value;
            }
            else
            {
                configInfo.ErrorLog = String.Empty;
            }

            configInfo.EnabledExtCount = file.GetEnabledExtensionsCount();
            configInfo.InstalledExtCount = file.Extensions.Count;

            configInfo.IsConfigOptimal = ValidateFastCgiConfiguration(file) && ValidatePHPConfiguration(file);

            return configInfo;
        }

        private string GetPHPDirectory()
        {
            string phpDirectory = Path.GetDirectoryName(Environment.ExpandEnvironmentVariables(_currentPHPHandler.ScriptProcessor));
            if (!phpDirectory.EndsWith(@"\", StringComparison.Ordinal))
            {
                phpDirectory += @"\";
            }

            return phpDirectory;
        }

        private static string GetPHPExecutableVersion(string phpexePath)
        {
            FileVersionInfo fileVersionInfo = FileVersionInfo.GetVersionInfo(phpexePath);
            return fileVersionInfo.ProductVersion;
        }

        public string GetPHPIniPath()
        {
            // PHP is not registered so we do not know the path to php.ini file
            if (_currentFastCgiApplication == null || _currentPHPHandler == null)
            {
                return String.Empty;
            }

            // If PHPRC environment variable is set then use the path specified there.
            // Otherwise use the same path as where PHP executable is located.
            string directoryPath = String.Empty;
            EnvironmentVariableElement phpRcElement = _currentFastCgiApplication.EnvironmentVariables["PHPRC"];
            if (phpRcElement != null && !String.IsNullOrEmpty(phpRcElement.Value))
            {
                directoryPath = phpRcElement.Value;
            }
            else
            {
                directoryPath = Path.GetDirectoryName(_currentPHPHandler.ScriptProcessor);
            }

            string phpIniPath = Path.Combine(directoryPath, "php.ini");

            if (File.Exists(phpIniPath))
            {
                return phpIniPath;
            }

            return String.Empty;
        }

        private void Initialize()
        {
            // Get the handlers collection
            ManagementConfiguration config = _managementUnit.Configuration;
            HandlersSection handlersSection = (HandlersSection)config.GetSection("system.webServer/handlers", typeof(HandlersSection));
            _handlersCollection = handlersSection.Handlers;

            // Get the FastCgi application collection
            Configuration appHostConfig = _managementUnit.ServerManager.GetApplicationHostConfiguration();
            FastCgiSection fastCgiSection = (FastCgiSection)appHostConfig.GetSection("system.webServer/fastCgi", typeof(FastCgiSection));
            _fastCgiApplicationCollection = fastCgiSection.Applications;

            // Find the currently active PHP handler and FastCGI application
            HandlerElement handler = _handlersCollection.GetActiveHandler("*.php");
            if (handler != null)
            {
                string executable = handler.ScriptProcessor;
                
                ApplicationElement fastCgiApplication = _fastCgiApplicationCollection.GetApplication(executable, "");
                if (fastCgiApplication != null)
                {
                    _currentPHPHandler = handler;
                    _currentFastCgiApplication = fastCgiApplication;
                }
            }
        }

        private static bool IsAbsoluteFilePath(string path, bool isFile)
        {
            string directory = Environment.ExpandEnvironmentVariables(path);
            if (Path.IsPathRooted(path))
            {
                if (isFile)
                {
                    directory = Path.GetDirectoryName(directory);
                }

                return Directory.Exists(directory);
            }

            return false;
        }

        private static bool IsExpectedSettingValue(PHPIniFile file, string settingName, string expectedValue)
        {
            PHPIniSetting setting = file.GetSetting(settingName);
            if (setting == null || String.IsNullOrEmpty(setting.Value) ||
                !String.Equals(setting.Value, expectedValue, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private HandlerElement MakeHandlerActive(string handlerName)
        {
            // We have to look up the handler elements by name because we may be working
            // on the copy of the handlers collection.
            HandlerElement handlerElement = _handlersCollection[handlerName];
            HandlerElement activeHandlerElement = _handlersCollection[_currentPHPHandler.Name];
            Debug.Assert(handlerElement != null && activeHandlerElement != null);

            int activeHandlerIndex = _handlersCollection.IndexOf(activeHandlerElement);
            _handlersCollection.Remove(handlerElement);
            return _handlersCollection.AddCopyAt(activeHandlerIndex, handlerElement);
        }

        public void RegisterPHPWithIIS(string path)
        {
            string phpexePath = Environment.ExpandEnvironmentVariables(path);
            
            if (!String.Equals(Path.GetFileName(phpexePath), "php-cgi.exe", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(Path.GetFileName(phpexePath), "php.exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The provided php executable path is invalid", phpexePath);
            }

            // Check for existence of php executable in the specified directory
            if (!File.Exists(phpexePath))
            {
                throw new FileNotFoundException("php-cgi.exe and php.exe do not exist");
            }

            // Check for existense of php extensions directory
            string phpDir = Path.GetDirectoryName(phpexePath);
            if (!phpDir.EndsWith(@"\"))
            {
                phpDir += @"\";
            }
            string extDir = Path.Combine(phpDir, "ext");
            if (!Directory.Exists(extDir))
            {
                throw new DirectoryNotFoundException("ext directory does not exist in " + phpDir);
            }
            
            // Check for existence of php.ini file. If it does not exist then copy php.ini-recommended
            // or php.ini-production to it
            string phpIniFilePath = Path.Combine(phpDir, "php.ini");
            if (!File.Exists(phpIniFilePath))
            {
                string phpIniRecommendedPath = Path.Combine(phpDir, "php.ini-recommended");
                string phpIniProductionPath = Path.Combine(phpDir, "php.ini-production");
                if (File.Exists(phpIniRecommendedPath))
                {
                    File.Copy(phpIniRecommendedPath, phpIniFilePath);
                }
                else if (File.Exists(phpIniProductionPath))
                {
                    File.Copy(phpIniProductionPath, phpIniFilePath);
                }
                else
                {
                    throw new FileNotFoundException("php.ini and php.ini recommended do not exist in " + phpDir);
                }
            }

            ApplicationElement fastCgiApplication = _fastCgiApplicationCollection.GetApplication(phpexePath, "");
            // Create a FastCGI application if it does not exist
            bool isNewFastCgi = false;
            if (fastCgiApplication == null)
            {
                fastCgiApplication = _fastCgiApplicationCollection.CreateElement();
                fastCgiApplication.FullPath = phpexePath;
                // monitorChangesTo may not exist if FastCGI update is not installed
                if (fastCgiApplication.MonitorChangesToExists())
                {
                    fastCgiApplication.MonitorChangesTo = phpIniFilePath;
                }
                fastCgiApplication.InstanceMaxRequests = 10000;
                fastCgiApplication.ActivityTimeout = 300;
                fastCgiApplication.RequestTimeout = 300;

                fastCgiApplication.EnvironmentVariables.Add("PHPRC", phpDir);
                fastCgiApplication.EnvironmentVariables.Add("PHP_FCGI_MAX_REQUESTS", "10000");

                _fastCgiApplicationCollection.Add(fastCgiApplication);
                isNewFastCgi = true;
            }
                
            // Check if file mapping with this executable already exists
            HandlerElement handlerElement = _handlersCollection.GetHandler("*.php", phpexePath);
            // Create a handler mapping if it does not exist
            bool isNewHandler = false;
            if (handlerElement == null)
            {
                // Create a PHP file handler if it does not exist
                handlerElement = _handlersCollection.CreateElement();
                handlerElement.Name = GenerateHandlerName(_handlersCollection, GetPHPExecutableVersion(phpexePath));
                handlerElement.Modules = "FastCgiModule";
                handlerElement.RequireAccess = RequireAccess.Script;
                handlerElement.Verb = "*";
                handlerElement.Path = "*.php";
                handlerElement.ScriptProcessor = phpexePath;
                handlerElement.ResourceType = ResourceType.Either;
                handlerElement = _handlersCollection.AddAt(0, handlerElement);
                isNewHandler = true;
            }
            else if (_currentPHPHandler != null && handlerElement != _currentPHPHandler)
            {
                // Move the existing PHP file handler mapping on top
                CopyInheritedHandlers();
                handlerElement = MakeHandlerActive(handlerElement.Name);
            }
                
            _managementUnit.Update();

            // We need to call Initialize() again to set references to current handler and 
            // fastcgi application and to avoid the read-only exception from IIS config
            Initialize();

            // Make recommended changes to existing iis configuration 
            if (!isNewFastCgi || !isNewHandler)
            {
                ApplyRecommendedFastCgiSettings(phpDir);
            }

            // Make the recommended changes to php.ini file
            PHPIniFile phpIniFile = new PHPIniFile(phpIniFilePath);
            phpIniFile.Parse();
            ApplyRecommendedPHPIniSettings(phpIniFile, true /* this is a new registration of PHP */);
        }

        public void SelectPHPHandler(string name)
        {
            // PHP is not registered properly so we don't attempt to do anything.
            if (_currentFastCgiApplication == null || _currentPHPHandler == null)
            {
                return;
            }

            HandlerElement handler = _handlersCollection[name];
            // If the handler is already an active PHP handler then no need to do anything.
            if (handler != null && handler != _currentPHPHandler)
            {
                CopyInheritedHandlers();
                handler = MakeHandlerActive(name);
                _managementUnit.Update();

                // Update the references to current php handler and application
                _currentPHPHandler = handler;
                _currentFastCgiApplication = _fastCgiApplicationCollection.GetApplication(handler.ScriptProcessor, "");
            }
        }

        private bool ValidateFastCgiConfiguration(PHPIniFile file)
        {

            // Check if handler mapping is configured for "File or Folder"
            if (_currentPHPHandler.ResourceType != ResourceType.Either)
            {
                return false;
            }

            // Check if PHP_FCGI_MAX_REQUESTS is set and is bigger than instanceMaxRequests
            EnvironmentVariableElement envVariableElement = _currentFastCgiApplication.EnvironmentVariables["PHP_FCGI_MAX_REQUESTS"];
            if (envVariableElement == null)
            {
                return false;
            }
            else
            {
                long maxRequests;
                if (!Int64.TryParse(envVariableElement.Value, out maxRequests) || 
                    (maxRequests < _currentFastCgiApplication.InstanceMaxRequests))
                {
                    return false;
                }
            }

            // Check if PHPRC is set and points to a directory that has php.ini file
            envVariableElement = _currentFastCgiApplication.EnvironmentVariables["PHPRC"];
            if (envVariableElement == null)
            {
                return false;
            }
            else
            {
                string path = Path.Combine(envVariableElement.Value, "php.ini");
                if (!File.Exists(path))
                {
                    return false;
                }
            }

            // Check if monitorChangesTo setting is supported and is set correctly
            if (_currentFastCgiApplication.MonitorChangesToExists())
            {
                string path = _currentFastCgiApplication.MonitorChangesTo;
                if (String.IsNullOrEmpty(path) || !File.Exists(path) || 
                    !String.Equals(file.FileName, path, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private bool ValidatePHPConfiguration(PHPIniFile file)
        {
            string phpDirectory = GetPHPDirectory();

            // Check if extention_dir is set to an absolute path
            string expectedValue = Path.Combine(phpDirectory, "ext");
            if (!IsExpectedSettingValue(file, "extension_dir", expectedValue))
            {
                return false;
            }

            // Check if log_errors is set to On
            if (!IsExpectedSettingValue(file, "log_errors", "On"))
            {
                return false;
            }

            // Check if error_log is set to an absolute path and that path exists
            PHPIniSetting setting = file.GetSetting("error_log");
            if (setting == null || String.IsNullOrEmpty(setting.Value) ||
                !IsAbsoluteFilePath(setting.Value, true /* this is supposed to be a file */))
            {
                return false;
            }

            // Check if session path is set to an absolute path and that path exists
            setting = file.GetSetting("session.save_path");
            if (setting == null || String.IsNullOrEmpty(setting.Value) ||
                !IsAbsoluteFilePath(setting.Value, false /* this is supposed to be a directory */))
            {
                return false;
            }

            // Check if cgi.force_redirect is set correctly
            if (!IsExpectedSettingValue(file, "cgi.force_redirect", "0"))
            {
                return false;
            }

            // Check if cgi.fix_pathinfo is set correctly
            if (!IsExpectedSettingValue(file, "cgi.fix_pathinfo", "1"))
            {
                return false;
            }

            // Check if fastcgi impersonation is turned on
            if (!IsExpectedSettingValue(file, "fastcgi.impersonate", "1"))
            {
                return false;
            }

            return true;
        }

    }
}