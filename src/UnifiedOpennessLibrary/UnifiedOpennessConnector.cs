﻿using Siemens.Engineering;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.HW;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using Siemens.Engineering.HmiUnified.UI.ScreenGroup;
using Siemens.Engineering.HmiUnified.UI.Screens;

namespace UnifiedOpennessLibrary
{
    public class CmdArgument
    {
        public string OptionToSet = "";
        public string OptionShort = "";
        public string OptionLong = "";
        public string HelpText = "";
    }
    public class UnifiedOpennessConnector : IDisposable
    {
        public Dictionary<string, string> CmdArgs { get; private set; } = new Dictionary<string, string>();

        private string TiaPortalVersion { get; set; }
        public string FileDirectory { get; private set; }
        public ExclusiveAccess AccessObject { get; private set; }
        public Project TiaPortalProject { get; private set; }
        public TiaPortal TiaPortal { get; private set; }
        public string DeviceName { get; private set; }
        public HmiSoftware UnifiedSoftware { get; private set; }
        public IEnumerable<HmiScreen> Screens { get; private set; }

        /// <summary>
        /// If your tool changes anything on the TIA Portal project, please use transactions!
        /// </summary>
        /// <param name="tiaPortalVersion">e.g. V18 or V19. It must be the part of the path in the installation folder</param>
        /// <param name="args">just pass the arguments that you got from the command line here. You may have access via the public member "CmdArgs" to your arguments afterwards</param>
        /// <param name="toolName">define the name of the tool (exe), so help text and the waiting text is more beautiful</param>
        /// <param name="additionalHelpText">if your tool needs additional help text, it can be added here</param>
        public UnifiedOpennessConnector(string tiaPortalVersion, string[] args, IEnumerable<CmdArgument> additionalParameters, string toolName = "MyTool")
        {
            TiaPortalVersion = tiaPortalVersion;
            ParseArguments(args.ToList(), toolName, additionalParameters);
            AppDomain.CurrentDomain.AssemblyResolve += AssemblyResolver;
            Work(toolName);
        }
        void Work(string toolName)
        {
            var processes = TiaPortal.GetProcesses();
            if (processes.Count > 0)
            {
                try
                {
                    if (!CmdArgs.ContainsKey("ProcessId") || CmdArgs["ProcessId"] == "")  // just take the first opened TIA Portal, if it is not specified
                    {
                        TiaPortal = processes.First().Attach();
                    }
                    else
                    {
                        TiaPortal = processes.First(x => x.Id == int.Parse(CmdArgs["ProcessId"])).Attach();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex);
                }
            }
            else
            {
                throw new Exception("No TIA Portal instance is open. Please open TIA Portal with your project.");
            }

            AccessObject = TiaPortal.ExclusiveAccess(toolName + " tool running.\nBe careful: Cancelling this request will not cancel the tool. Please close the Command line to avoid any changes.");
            TiaPortalProject = TiaPortal.Projects.FirstOrDefault();
            if (TiaPortalProject == null)
            {
                throw new Exception("Please check, if the search is working in TIA Portal and install missing GSD files if not. Then run this tool again.");
            }
            FileDirectory = TiaPortalProject.Path.DirectoryName + "\\UserFiles\\" + DeviceName + "\\" + toolName + "\\";
            SetHmiByDeviceName();
        }

        private void SetHmiByDeviceName()
        {
            var hmiSoftwares = GetHmiSoftwares();
            UnifiedSoftware = hmiSoftwares.FirstOrDefault(x => (x.Parent as SoftwareContainer).OwnedBy.Container.Name == DeviceName);
            if (UnifiedSoftware == null)
            {
                throw new Exception("Device with name " + DeviceName + " cannot be found. Please check, if the search is working in TIA Portal and install missing GSD files if not. Then run this tool again.");
            }
            Screens = GetScreens();
            if (CmdArgs.ContainsKey("Include"))
            {
                var screenNames = CmdArgs["Include"].Split(';').Where(x => !string.IsNullOrWhiteSpace(x));
                Screens = Screens.Where(x => screenNames.Contains(x.Name));
            }
            else if (CmdArgs.ContainsKey("Exclude"))
            {
                var screenNames = CmdArgs["Exclude"].Split(';').Where(x => !string.IsNullOrWhiteSpace(x));
                Screens = Screens.Where(x => !screenNames.Contains(x.Name));
            }
        }

        private IEnumerable<HmiScreen> GetScreens()
        {
            var allScreens = UnifiedSoftware.Screens.ToList();
            allScreens.AddRange(ParseGroups(UnifiedSoftware.ScreenGroups));
            return allScreens;
        }

        private IEnumerable<HmiScreen> ParseGroups(HmiScreenGroupComposition parentGroups)
        {
            foreach (var group in parentGroups)
            {
                foreach (var screen in group.Screens)
                {
                    yield return screen;
                }
                foreach (var screen in ParseGroups(group.Groups))
                {
                    yield return screen;
                }
            }
        }
        private IEnumerable<Device> GetAllDevices(DeviceUserGroupComposition parentGroups)
        {
            foreach (var parentGroup in parentGroups)
            {
                foreach (var device in parentGroup.Devices)
                {
                    yield return device;
                }
                foreach (var device in GetAllDevices(parentGroup.Groups))
                {
                    yield return device;
                }
            }
        }

        private IEnumerable<HmiSoftware> GetHmiSoftwares()
        {
            return
                from device in TiaPortalProject.Devices.Concat(GetAllDevices(TiaPortalProject.DeviceGroups))
                from deviceItem in device.DeviceItems
                let softwareContainer = deviceItem.GetService<SoftwareContainer>()
                where softwareContainer?.Software is HmiSoftware
                select softwareContainer.Software as HmiSoftware;
        }

        public void Dispose()
        {
            AccessObject?.Dispose();
            TiaPortal?.Dispose();
        }

        public Assembly AssemblyResolver(object sender, ResolveEventArgs args)
        {
            int index = args.Name.IndexOf(',');
            string dllPathToTry = string.Empty;
            string directory = string.Empty;
            if (index != -1)
            {
                string name = args.Name.Substring(0, index);
                string path = @"C:\Program Files\Siemens\Automation\Portal " + TiaPortalVersion + @"\bin\Siemens.Automation.Portal.exe";
                if (path != null & path != string.Empty)
                {
                    if (name == "Siemens.Engineering")
                    {
                        try
                        {
                            FileInfo exeFileInfo = new FileInfo(path);
                            dllPathToTry = exeFileInfo.Directory + @"\..\PublicAPI\" + TiaPortalVersion + @"\Siemens.Engineering.dll";
                        }
                        catch (NullReferenceException e)
                        {
                            MessageBox.Show("Tool cannot start due to an inconsistent TIA installation. Please contact support.",
                                "Error",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                            Environment.Exit(1);
                        }
                    }
                    else if (name == "Siemens.Engineering.Hmi")
                    {
                        try
                        {
                            FileInfo exeFileInfo = new FileInfo(path);
                            dllPathToTry = exeFileInfo.Directory + @"\..\PublicAPI\" + TiaPortalVersion + @"\Siemens.Engineering.Hmi.dll";
                        }
                        catch (NullReferenceException e)
                        {
                            MessageBox.Show("Tool cannot start due to an inconsistent TIA installation. Please contact support.",
                                "Error",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                            Environment.Exit(1);
                        }
                    }
                }



                if (dllPathToTry != string.Empty)
                {
                    string assemblyPath = Path.GetFullPath(dllPathToTry);

                    if (File.Exists(assemblyPath))
                    {
                        return Assembly.LoadFrom(assemblyPath);
                    }
                    else
                    {
                        MessageBox.Show("Tool cannot start due to an inconsistent TIA installation. Please contact support.",
                            "Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        Environment.Exit(1);
                    }
                }
            }
            return null;
        }
        /// <summary>
        /// parses the arguments of the input string, e.g. HMI_RT_1 -p=1234 --include="Screen_1;Screen 5" will be parsed to elements in the dictionairy: -p with string "1234" and --include with string "Screen_1;Screen 5"
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        private void ParseArguments(List<string> args, string toolname, IEnumerable<CmdArgument> additionalParameters)
        {
            var argConfiguration = new List<CmdArgument>() {
                new CmdArgument() { OptionToSet = "ProcessId", OptionShort = "-p", OptionLong = "--processid", HelpText = "define a process id the tool connects to. If empty, the first TIA Portal process will be connected to" } ,
                new CmdArgument() { OptionToSet = "Include", OptionShort = "-i", OptionLong = "--include", HelpText = "add a list of screen names on which the tool will work on, split by semicolon (cannot be combined with --exclude), e.g. \"Screen_1;My screen 2\"" } ,
                new CmdArgument() { OptionToSet = "Exclude", OptionShort = "-e", OptionLong = "--exclude", HelpText = "add a list of screen names on which the tool will not work on, split by semicolon (cannot be combined with --include), e.g. \"Screen_1;My screen 2\"" }
            };
            if (args.Count == 0)
            {
                DisplayHelp(argConfiguration, toolname);
                throw new Exception("There must be at least one argument to define the device name.");
            }
            if (args.Contains("-h") || args.Contains("--help"))
            {
                DisplayHelp(argConfiguration, toolname);
                throw new Exception();
            }
            DeviceName = args[0];
            args.RemoveAt(0);
            if (additionalParameters != null)
            {
                argConfiguration.AddRange(additionalParameters);
            }
            foreach (var arg in args)
            {
                foreach (var cmdArg in argConfiguration)
                {
                    SetParameter(arg, cmdArg);
                }
            }
        }
        private void SetParameter(string arg, CmdArgument cmdArg)
        {
            if ((cmdArg.OptionShort != "" && arg.ToLower().StartsWith(cmdArg.OptionShort)) || (cmdArg.OptionLong != "" && arg.ToLower().StartsWith(cmdArg.OptionLong)))
            {
                var parts = arg.Split('=').ToList();
                if (parts.Count == 2)
                {
                    parts.RemoveAt(0);
                    CmdArgs[cmdArg.OptionToSet] = string.Join("=", parts).Trim('"');
                }
            }
        }
        static void DisplayHelp(List<CmdArgument> argConfiguration, string toolName)
        {
            string helpText = @"
Usage: " + toolName + @".exe DEVICE_NAME [OPTION]

Always add a DEVICE_NAME. This is the name of your device, that you can see inside the 'Project tree', e.g. HMI_1

Options:
";
            foreach (var argConfig in argConfiguration)
            {
                helpText += argConfig.OptionShort + "\t" + argConfig.OptionLong + "\t\t\t\t" + argConfig.HelpText + "\n";
            }
            Console.WriteLine(helpText);
        }
    }
}
