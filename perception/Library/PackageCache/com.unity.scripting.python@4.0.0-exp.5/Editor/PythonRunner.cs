using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using Python.Runtime;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Collections;
using System.Threading.Tasks;
using System.Threading;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

namespace UnityEditor.Scripting.Python
{
    /// <summary>
    /// Exception thrown when Python is installed incorrectly so we can't
    /// run.
    /// </summary>
    public class PythonInstallException : System.Exception
    {
        /// <summary>
        /// Parameterless constructor 
        /// </summary>
        public PythonInstallException() : base() { }

        /// <summary>
        /// Constructor with message
        /// </summary>
        /// <param name="msg">The message of the exception</param>
        public PythonInstallException(string msg) : base(msg) { }

        /// <summary>
        /// Constructor with message and the exception that triggered this exception
        /// </summary>
        /// <param name="msg">The message of the exception</param>
        /// <param name="innerException">The exception that triggered this exception</param>
        public PythonInstallException(string msg, Exception innerException) : base(msg, innerException) { }

        /// <summary>
        /// Required because base Exception class is serializable.
        /// </summary>
        /// <param name="info">Serializaiton info</param>
        /// <param name="ctx">Serialization context</param>
        protected PythonInstallException(SerializationInfo info , StreamingContext ctx) : base(info, ctx) { }

        /// <summary>
        /// The exception's string
        /// </summary>
        public override string Message => $"Python for Unity: {base.Message}\nPlease check the Python for Unity package documentation for the install troubleshooting instructions.";
    }

    /// <summary>
    /// This class encapsulates the Unity Editor API support for Python.
    /// </summary>
    public static class PythonRunner
    {
#if UNITY_EDITOR_WIN
        const string Platform = "windows";
        const string Z7name = "7z.exe";
#elif UNITY_EDITOR_OSX
        const string Platform = "macos";
        const string Z7name = "7za";
        const string dynLibExt = "dylib";
#endif
        static readonly string BinariesPackageName = $"com.unity.scripting.python.{Platform}";
        const string BinariesPackageVersion = "1.0.0-exp.3";
        const string VersionFile = "Library/PythonInstall/version";

        internal enum BinariesPackageReleaseType
        {
            kPreview, kExperimental, kPreRelease, kRelease
        }

        /// <summary>
        /// The version of the Python interpreter.
        ///
        /// Accessing this initializes Python if it hasn't been already.
        /// </summary>
        /// <value>A string representing the version.</value>
        public static string PythonVersion
        {
            get
            {
                EnsureInitialized();
                using (Py.GIL())
                {
                    dynamic sys = PythonEngine.ImportModule("sys");
                    return sys.version.ToString();
                }
            }
        }

        /// <summary>
        /// The full path to the Python interpreter.
        ///
        /// Accessing this initializes Python if it hasn't been already.
        /// </summary>
        /// <value>A string representing the full path to the Python executable.</value>
        public static string PythonInterpreter
        {
            get
            {
                EnsureInitialized();
                return System.IO.Path.GetFullPath(PythonSettings.kDefaultPython);
            }
        }

        static bool s_IsInitialized = false;

        /// <summary>
        /// Verify whether Python has been initialized yet.
        ///
        /// Normally you'd simply call EnsureInitialized without checking. This
        /// access is principally useful when shutting down.
        /// </summary>
        public static bool IsInitialized
        {
            get
            {
                return s_IsInitialized;
            }
        }

        /// <summary>
        /// Runs Python code in the Unity process.
        /// </summary>
        /// <param name="pythonCodeToExecute">The code to execute.</param>
        /// <param name="scopeName">Value to write to Python special variable `__name__`</param>
        public static void RunString(string pythonCodeToExecute, string scopeName = null)
        {
            if (string.IsNullOrEmpty(pythonCodeToExecute))
            {
                return;
            }

            EnsureInitialized();
            using (Py.GIL ())
            {
                // Clean up the string.
                dynamic inspect = PythonEngine.ImportModule("inspect");
                string code = inspect.cleandoc(pythonCodeToExecute);

                if (string.IsNullOrEmpty(scopeName))
                {
                    PythonEngine.Exec(code);
                }
                else
                {
                    using (PyScope scope = Py.CreateScope())
                    {
                        scope.Set("__name__", scopeName);
                        scope.Exec(code);
                    }
                }
            }
        }

        /// <summary>
        /// Runs a Python script in the Unity process.
        /// </summary>
        /// <param name="pythonFileToExecute">The script to execute.</param>
        /// <param name="scopeName">Value to write to Python special variable `__name__`</param>
        public static void RunFile(string pythonFileToExecute, string scopeName = null)
        {
            EnsureInitialized();
            if (null == pythonFileToExecute)
            {
                throw new System.ArgumentNullException("pythonFileToExecute", "Invalid (null) file path");
            }

            // Ensure we are getting the full path.
            pythonFileToExecute = Path.GetFullPath(pythonFileToExecute);

            // Forward slashes please
            pythonFileToExecute = pythonFileToExecute.Replace("\\","/");
            if (!File.Exists (pythonFileToExecute))
            {
                throw new System.IO.FileNotFoundException("No Python file found at " + pythonFileToExecute, pythonFileToExecute);
            }

            using (Py.GIL ())
            {
                if (string.IsNullOrEmpty(scopeName))
                {
                    PythonEngine.Exec(string.Format("exec(open('{0}').read())", pythonFileToExecute));
                }
                else
                {
                    using (PyScope scope = Py.CreateScope())
                    {
                        scope.Set("__name__", scopeName);
                        scope.Set("__file__", pythonFileToExecute);
                        scope.Exec(string.Format("exec(open('{0}').read())", pythonFileToExecute));
                    }                    
                }
            }
        }

        /// <summary>
        /// Initialize automatically when a new domain has started.
        ///
        /// When Unity starts up the domain reloads multiple times, but only
        /// after the "last one" when Unity becomes ready that EditorApplication.update
        /// is called. Use this to know we're good to go.
        /// </summary>
        [InitializeOnLoadMethod]
        static void InitializeOnLoad()
        {
            EditorApplication.delayCall += EnsureInitialized;
        }

        // To keep the thread state so we can restore it on domain unloads
        static IntPtr s_threadState = IntPtr.Zero;

        /// <summary>
        /// Release the GIL by default; let other threads run.
        /// 
        /// Do *not* unfactor me!
        /// 
        /// Used to "two-step" the initialization process. No code from 
        /// Python.net must be in scope of execution before 
        /// PythonEngine.LoadLibrary has been called. Otherwise, some symbols
        /// may be loaded before the library is loaded in memory, causing errors
        /// and crashes.
        /// </summary>
        static void AllowThreads()
        {
            // Let the threads flow! Since the main thread will only execute 
            // Python sporadically, make the main thread release the GIL. This
            // mean that every time Python-related code is executed (in the main
            // thread and everywhere else), the GIL must be held.
            s_threadState = PythonEngine.BeginAllowThreads();

            // And restore it on shutdown.
            PythonEngine.AddShutdownHandler( () => {PythonEngine.EndAllowThreads(s_threadState);} );
        }

        /// <summary>
        /// Redirect stdout to the Editor.log and to the Python interactive console.
        ///
        /// Automatically undoes itself on domain unload so a print while Unity
        /// is reloading can still occur.
        /// </summary>
        internal static void RedirectStdout()
        {
            EnsureInitialized();
            using (Py.GIL())
            {
                dynamic redirect_stdout = PythonEngine.ImportModule("redirecting_stdout");
                redirect_stdout.redirect_stdout();
                PythonEngine.AddShutdownHandler(UndoRedirectStdout);
            }
        }

        internal static void UndoRedirectStdout()
        {
            if (!IsInitialized)
            {
                return;
            }
            using (Py.GIL())
            {
                try
                {
                    dynamic redirect_stdout = PythonEngine.ImportModule("redirecting_stdout");
                    redirect_stdout.undo_redirection();
                }
                catch (PythonException e)
                {
                    UnityEngine.Debug.LogException(e);
                }
            }
            PythonEngine.RemoveShutdownHandler(UndoRedirectStdout);
        }

        /// <summary>
        /// OnUpdate callback called back during the Editor Idle events.
        /// Used to process the Python jobs queue.
        /// </summary>
        static void OnUpdate()
        {
            if (!IsInitialized)
            {
                return;
            }
            using(Py.GIL())
            {
                dynamic scheduling_module = PythonEngine.ImportModule("unity_python.common.scheduling");
                
                try
                {
                    scheduling_module.process_jobs();
                }
                catch (PythonException e)
                {
                    UnityEngine.Debug.LogException(e);
                }
            }
        }

        /// <summary>
        /// Ensures the Python API is initialized.
        ///
        /// Safe to call frequently.
        ///
        /// Throws if there's an installation error.
        /// </summary>
        public static void EnsureInitialized()
        {
            if (IsInitialized)
            {
                return;
            }
            try
            {
                s_IsInitialized = true;
                DoEnsureInitialized();
            }
            catch
            {
                s_IsInitialized = false;
                throw;
            }
        }

        static class NativeMethods
        {
#if UNITY_EDITOR_WIN
            [DllImport("kernel32.dll", SetLastError = false, CharSet = CharSet.Unicode)]
            internal static extern IntPtr GetModuleHandle(string lpMpduleName);
            internal static string pythonLibraryName = "python37";
            internal static string pythonLibPath = $"{PythonSettings.kDefaultPythonDirectory}";

#elif UNITY_EDITOR_OSX
            [DllImport("libdl." + dynLibExt)]
            static internal extern IntPtr dlopen(string filename, int flags);

            [DllImport("libdl." + dynLibExt)]
            static internal extern int dlclose(IntPtr handle);

            internal const int RTLD_NOW = 2;
            internal const int RTLD_NOLOAD = 4;
            internal static string pythonLibraryName = $"libpython3.7m";
            internal static string pythonLibPath = $"{PythonSettings.kDefaultPythonDirectory}/lib";

#endif
        }
            /// <summary>
            /// Used to check if the Python library has been loaded into the memoryspace.
            /// </summary>
            /// <returns>True if the library has been loaded, False otherwise.</returns>
            internal static bool IsPythonLibraryLoaded()
        {
#if UNITY_EDITOR_WIN
            return NativeMethods.GetModuleHandle($"{NativeMethods.pythonLibraryName}.dll") != IntPtr.Zero;
#elif UNITY_EDITOR_OSX
            string pythonDyLibPath = $"{NativeMethods.pythonLibPath}/{NativeMethods.pythonLibraryName}.{dynLibExt}";
            IntPtr pythonDyLibHandle = NativeMethods.dlopen(pythonDyLibPath, NativeMethods.RTLD_NOW | NativeMethods.RTLD_NOLOAD);
            if (pythonDyLibHandle != IntPtr.Zero) {
                // With RTLD_NOLOAD, the specified image is not loaded. However, a valid handle is returned if the
                // image already exists in the process. This provides a way to query if an image is already loaded.
                // The handle returned is ref-counted, so you eventually need a corresponding call to dlclose().
                // (from: dlopen man page)
                NativeMethods.dlclose(pythonDyLibHandle);
                return true;
            }
            return false;
#endif
        }

        /// <summary>
        /// Helper to wait on a UPM request.
        /// </summary>
        /// <param name="req">A Request to wait the completion of</param>
        static Action<Request> WaitOnRequest = (Request req) => 
            {
                while(!req.IsCompleted) 
                {
                    // Heat the room..
                    Thread.Sleep(1);
                }
            };

        static bool VerifyPythonInstalled()
        {
#if UNITY_EDITOR_WIN
            var site_path = Path.GetFullPath(PythonSettings.kDefaultPythonDirectory + "/Lib/site.py");
#elif UNITY_EDITOR_OSX
            var site_path = Path.GetFullPath(PythonSettings.kDefaultPythonDirectory + "/lib/python3.7/site.py");
#endif
            return File.Exists(site_path);
        }

        /// <summary>
        /// Install or upgrade (as needed) the python install and loads the
        /// library in memory.
        /// </summary>
        static void InstallAndLoadPython()
        {
            InstallPython();

            ///////////////////////
            // Tell the Python interpreter not to generate .pyc files. Packages
            // are installed in read-only locations on some OSes and if package
            // developers forget to remove their .pyc files it could become
            // problematic. This can be changed at runtime by a script.
            System.Environment.SetEnvironmentVariable("PYTHONDONTWRITEBYTECODE", "1");

            // First of all, load the Library in memory because we are accessing
            // the interpreter internals before we initialize.
            string libname = NativeMethods.pythonLibraryName;
            if (libname.StartsWith("lib"))
            {
                // the library loader doesn't want the prefixing 'lib'
                libname = libname.Remove(0, 3);
            }
            // This method's directory argument requires the trailing '/'
            PythonEngine.InitializeLibrary(libname, NativeMethods.pythonLibPath+"/");

            // Let Python know where to find its site.py
            PythonEngine.PythonHome = Path.GetFullPath(PythonSettings.kDefaultPythonDirectory);
        }

        static string InstalledBinariesVersion()
        {
            var ret = "0.0.0";

            if (!File.Exists(VersionFile)) 
            {
                return ret;
            }

            using(var reader = new StreamReader(VersionFile ))
            {
                ret = reader.ReadToEnd().Trim();
            }
            return ret;
        }

        /// <summary>
        /// Convert a version into a tuple.
        /// This can be used to compare two versions and also check that the version is valid.
        /// </summary>
        /// <param name="version">The version string to parse</param>
        /// <returns>a tuple of (major, minor, patch, releaseType, suffix)</returns>
        internal static (int, int, int, BinariesPackageReleaseType, int) ConvertVersionToTuple(string version)
        {
            // 1.0.0-exp.1 -> 1, 0, 0-exp, 1
            var splitVersion = version.Split('.');
            if (splitVersion.Length < 3)
            {
                throw new PythonInstallException("Version is not of the form major.minor.patch");
            }
            if (!int.TryParse(splitVersion[0], out int major))
            {
                throw new PythonInstallException("Major of version is not an integer");
            }
            if (!int.TryParse(splitVersion[1], out int minor))
            {
                throw new PythonInstallException("Minor of version is not an integer");
            }
            var splitPatch = splitVersion[2].Split('-'); // 0-exp becomes 0, exp
            if (!int.TryParse(splitPatch[0], out int patch))
            {
                throw new PythonInstallException("Patch of version is not an integer");
            }

            // if there is no type then it is a release
            var releaseType = BinariesPackageReleaseType.kRelease;
            if (splitPatch.Length > 1)
            {
                var type = splitPatch[1];
                if (type == "exp")
                {
                    releaseType = BinariesPackageReleaseType.kExperimental;
                }
                else if(type == "preview")
                {
                    releaseType = BinariesPackageReleaseType.kPreview;
                }
                else if(type == "pre")
                {
                    releaseType = BinariesPackageReleaseType.kPreRelease;
                }
                else
                {
                    throw new PythonInstallException("Failed to parse release type from version");
                }
            }
            if (splitVersion.Length > 3)
            {
                if (!int.TryParse(splitVersion[3], out int suffix))
                {
                    throw new PythonInstallException("Version suffix is not an integer");
                }
                return (major, minor, patch, releaseType, suffix);
            }
            return (major, minor, patch, releaseType, 0);
        }

        /// <summary>
        /// Less than zero      The installed version is ahead of the current version. (downgrade required)
        /// Zero                The installed version and the current version are the same.
        /// Greater than zero   The installed version is behind the current version. (upgrade required)
        /// </summary>
        /// <returns></returns>
        static int BinariesVersionCheck()
        {
            var installedVersion = ConvertVersionToTuple(InstalledBinariesVersion());
            var currentVersion = ConvertVersionToTuple(BinariesPackageVersion);
            return currentVersion.CompareTo(installedVersion);
        }

        /// <summary>
        /// Checks if the binaries packages is present locally (manually added)
        /// </summary>
        /// <returns>A tuple of (bool, string, string) where the boolean is True if the 
        /// binaries package is present locally, False otherwise. The string will 
        /// be the path of the binaries package, if the boolean is true, null otherwise.
        /// The last string will be the version of the package if the boolean is true, null otherwise</returns>
        static (bool, string, string) AreBinariesLocal()
        {
            ListRequest lreq = Client.List();
            WaitOnRequest(lreq);

            UnityEditor.PackageManager.PackageCollection coll = lreq.Result;

            foreach (var p in coll)
            {
                if (p.name == BinariesPackageName)
                {
                    return (true, p.resolvedPath, p.version);
                }
            }
            return (false, null, null);

        }

        /// <summary>
        /// Check if we can proceed with an install or upgrade of Python.
        /// In case of an upgrade, a Log is displayed if the Python library (python37.dll)
        /// is loaded.
        /// </summary>
        /// <returns>True if it is possible to continue with an install or upgrade.
        /// False otherwise.</returns>
        internal static bool CanInstallPythonCheck(int versionStatus, bool localPackage)
        {
            bool needsUpgrade = (versionStatus > 0);
            bool needsDowngrade = (versionStatus < 0);
            // First, check if it needs to be upgraded
            if (!VerifyPythonInstalled())
            {
                // We need to install
                return true;
            }
            else if (versionStatus == 0)
            {
                return false; // Nothing to do!
            }
            else if (needsUpgrade && IsPythonLibraryLoaded())
            {
                Debug.LogWarning("A newer version of the Python Binaries needs to be installed, restart Unity to install.");
                return false;
            }
            else if (needsDowngrade)
            {
                // Downgrade is unlikely to happen and this is mostly a developper warning
                if(localPackage)
                {
                    Debug.Log("The current Binaries Python package is a lower version than the installed version");
                }
                return false;
            }
            return true;
        }

        static void InstallPython()
        {
            int installedPackageStatus = BinariesVersionCheck();
            string packageLocation = "/dev/null";
            bool binariesPackageLocal = false;
            string binariesPackageVersion = null;
            (binariesPackageLocal, packageLocation, binariesPackageVersion) = AreBinariesLocal();
            bool needsUpgrade = (installedPackageStatus > 0) && VerifyPythonInstalled();

            if (!CanInstallPythonCheck(installedPackageStatus, binariesPackageLocal))
            {
                // Python is already installed and up to date or requires an
                // upgrade but can't proceed.
                return;
            }

            if (!needsUpgrade)
            {
                Debug.Log("Installing Python. This may take a while..");
            }
            else
            {
                Debug.Log("Upgrading the Python for Unity binaries. This may take a while.. In case of errors, please close Unity and delete 'Library/PythonInstall'");
            }

            // Get it from the server if it's not present locally/in the manifest
            if (!binariesPackageLocal)
            {
                AddRequest req = Client.Add($"{BinariesPackageName}@{BinariesPackageVersion}");
                WaitOnRequest(req);
                if (req.Status == StatusCode.Failure)
                {
                    throw new PythonInstallException(req.Error.message);
                }
                
                packageLocation = Path.GetFullPath(req.Result.resolvedPath);
                binariesPackageVersion = req.Result.version;
            }
            var archiveLocation = $"{packageLocation}/bin~/pybin.7z";

            // Create a process and extract the binaries
            var proc = new System.Diagnostics.Process();
#if UNITY_EDITOR_WIN
            proc.StartInfo.FileName = $"{Path.GetDirectoryName(EditorApplication.applicationPath)}/Data/Tools/{Z7name}";
#elif UNITY_EDITOR_OSX
            proc.StartInfo.FileName = $"{Path.GetDirectoryName(EditorApplication.applicationPath)}/Unity.app/Contents/Tools/{Z7name}";
#endif
            var outputFolder = Path.GetFullPath("Library") + "/PythonInstall";
            //  "`-y`: assume Yes on all queries" Useful when upgrading to overwrite existing files.
            var args = $"x -y \"{archiveLocation}\" -o\"{outputFolder}\"";
            proc.StartInfo.Arguments = args;
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.RedirectStandardError = true;
            proc.StartInfo.WorkingDirectory = System.IO.Directory.GetCurrentDirectory();
            Console.WriteLine($"Unpacking Python using {proc.StartInfo.FileName} {args}");
            proc.Start();
            while (!proc.HasExited)
            {
                // Heat the room.. 
                Thread.Sleep(1);
            }
            if (proc.ExitCode != 0)
            {
                throw new PythonInstallException($"Extraction of the Python archive failed: {proc.StandardError.ReadToEnd()}");
            }

            if (!binariesPackageLocal)
            {
                // And remove it because we don't want it anymore.
                RemoveRequest rreq = Client.Remove(BinariesPackageName);
                WaitOnRequest(rreq);
            }

            // Finally, write the version file. Remove it first as needed.
            if (File.Exists(VersionFile))
            {
                File.Delete(VersionFile);
            }
            using (var writer = new StreamWriter(VersionFile))
            {
                writer.Write(binariesPackageVersion);
            }

            if (VerifyPythonInstalled())
            {
                Debug.Log("Python sucessfully installed");
            }
            else
            {
                throw new PythonInstallException("Python installation failed, please check the Editor.log");
            }
        }

        /// <summary>
        /// This is a helper for EnsureInitialized; call that function instead.
        ///
        /// This function assumes the API hasn't been initialized, and does the work of initializing it.
        /// </summary>
        static void DoEnsureInitialized()
        {
            // Install Python if we haven't yet.
            InstallAndLoadPython();

            // Initialize the engine if it hasn't been initialized yet.
            PythonEngine.Initialize(mode: ShutdownMode.Reload);

            ///////////////////////
            // Add the packages we use to the sys.path, and put them at the head.
            // TODO: remove duplicates.
            using (Py.GIL())
            {
                // Start coverage here for in-process coverage
                if(!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("COVERAGE_PROCESS_START")))
                {
                    // Assume that if we are trying to run coverage, the module
                    // is in the path. Try to import it to give a better error message
                    dynamic coverage = null;
                    try
                    {
                         coverage = PythonEngine.ImportModule("coverage");
                    }
                    catch (PythonException e)
                    {
                        // Throw a more understandable exception if we fail.
                        if (e.PythonTypeName == "ModuleNotFoundError" || e.PythonTypeName == "ImportError")
                        {
                            throw new PythonInstallException(
                                $"Environment variable for code coverage is defined but no coverage package can be found. \n{e.Message}"
                                );
                        }
                        else
                        {
                            throw;
                        }
                    }
                    coverage.process_startup();
                }

                ///////////////////////
                // Add the packages we use to the sys.path, and put them at the head.
                // TODO: remove duplicates.

                // Get the builtin module, which is 'builtins' on Python3 and __builtin__ on Python2
                dynamic builtins = PythonEngine.ImportModule("builtins");
                // prepend to sys.path
                dynamic sys = PythonEngine.ImportModule("sys");
                dynamic syspath = sys.GetAttr("path");
                dynamic sitePackages = GetExtraSitePackages();
                dynamic pySitePackages = builtins.list();
                foreach(var sitePackage in sitePackages)
                {
                    pySitePackages.append(sitePackage);
                }
                pySitePackages += syspath;
                sys.SetAttr("path", pySitePackages);

                // Log what we did. TODO: just to the editor log, not the console.
                var sysPath = sys.GetAttr("path").ToString();
                Console.Write($"Python for Unity initialized:\n  version = {PythonEngine.Version}\n  sys.path = {sysPath}\n");
            }

            // Now set up some other features
            RedirectStdout();

            ///////////////////////
            // Finally (this should be last!) we're in a stable state -- allow Python threads to run.
            AllowThreads();
        }

        /// <summary>
        /// Returns a list of the extra site-packages that we need to prepend to sys.path.
        ///
        /// These are absolute paths.
        /// </summary>
        static List<string> GetExtraSitePackages()
        {
            var sitePackages = new List<string>();

            // 1. Our package's Python/site-packages directory. This needs to be first.
            {
                string packageSitePackage = Path.GetFullPath("Packages/com.unity.scripting.python/Python~/site-packages");
                packageSitePackage = packageSitePackage.Replace("\\", "/");
                sitePackages.Add(packageSitePackage);
            }

            // 2 The ScriptsAssemblies folder so that users can load their own Assemblies (and the ones from the packages)
            var scriptsAssemblies = Path.GetFullPath("Library/ScriptAssemblies");
            scriptsAssemblies = scriptsAssemblies.Replace("\\", "/");
            sitePackages.Add(scriptsAssemblies);

            // 3. The packages from the settings.
            foreach(var settingsPackage in PythonSettings.GetSitePackages())
            {
                if (!string.IsNullOrEmpty(settingsPackage))
                {
                    var settingsSitePackage = settingsPackage;
                    // C# can't do tilde expansion. Do a very basic expansion.
                    if (settingsPackage.StartsWith("~"))
                    {
                        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                        // Don't use Path.Combine here. If settingsPackage starts with a '/', then 
                        // settingsPackage will be returned. As per documented behaviour.
                        settingsSitePackage = homeDirectory + "/" + settingsPackage.Substring(1);
                    }
                    settingsSitePackage = Path.GetFullPath(settingsSitePackage);
                    settingsSitePackage = settingsSitePackage.Replace("\\", "/");
                    sitePackages.Add(settingsSitePackage);
                }
            }

            return sitePackages;
        }

#if UNITY_EDITOR_WIN
        /// <summary>
        /// Spawns a Windows Powershell with the PATH set up to find the
        /// Python for Unity's installed Python interpreter.
        /// </summary>
        public static void SpawnShell()
        {
            EnsureInitialized();
            var shell = "powershell.exe";
            using (Py.GIL())
            {
                dynamic unity_py = Py.Import("unity_python.common.spawn_process");
                dynamic builtins = Py.Import("builtins");
                dynamic pyArgs = builtins.list(); // none
                var path = System.Environment.GetEnvironmentVariable("PATH");
                var pythonBinPath = Path.GetDirectoryName(Path.GetFullPath(PythonSettings.kDefaultPython)); // where the binaries/.exes are
                var pythonScripts = pythonBinPath + "/Scripts"; // where pip and friends are
                path = pythonBinPath + Path.PathSeparator + pythonScripts + Path.PathSeparator + path;
                var env = new PyDict();
                env["PATH"] = path.ToPython();
                dynamic process = unity_py.spawn_process_in_environment(
                        shell,
                        pyArgs,
                        env_override: env,
                        show_window: true
                        );
            }
        }
#endif

    }
}
