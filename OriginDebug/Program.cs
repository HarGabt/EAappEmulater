using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace OriginDebug;

internal class Program
{
    #region Native API

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    const int PROC_THREAD_ATTRIBUTE_PARENT_PROCESS = 0x00020000;
    const int EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    const int CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    const int CREATE_NEW_CONSOLE = 0x00000010;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList, int dwAttributeCount,
        int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute,
        ref IntPtr lpValue, IntPtr cbSize,
        IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcess(
        string lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, int dwCreationFlags,
        IntPtr lpEnvironment, string lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    #endregion

    /// <summary>
    /// 以explorer.exe为父进程启动目标程序，这样来和EAAC打游击战，你他妈有种就把explorer.exe也拉黑
    /// </summary>
    static void StartWithExplorerParent(
        string fileName,
        string arguments,
        string workingDirectory,
        Dictionary<string, string> envVars)
    {
        // 1. 找 explorer 句柄
        var explorers = Process.GetProcessesByName("explorer");
        if (explorers.Length == 0)
            throw new Exception("找不到 explorer.exe 进程");
        IntPtr explorerHandle = explorers[0].Handle;

        // 2. 初始化 AttributeList
        IntPtr lpSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
        IntPtr attrList = Marshal.AllocHGlobal(lpSize);
        try
        {
            InitializeProcThreadAttributeList(attrList, 1, 0, ref lpSize);

            // 3. 设置伪造父进程
            UpdateProcThreadAttribute(
                attrList, 0,
                (IntPtr)PROC_THREAD_ATTRIBUTE_PARENT_PROCESS,
                ref explorerHandle,
                (IntPtr)IntPtr.Size,
                IntPtr.Zero, IntPtr.Zero);

            // 4. 构建 Unicode 环境变量块
            var sb = new StringBuilder();
            foreach (var kv in envVars)
                sb.Append($"{kv.Key}={kv.Value}\0");
            sb.Append('\0');

            byte[] envBytes = Encoding.Unicode.GetBytes(sb.ToString());
            IntPtr envBlock = Marshal.AllocHGlobal(envBytes.Length);
            try
            {
                Marshal.Copy(envBytes, 0, envBlock, envBytes.Length);

                // 5. 构建 STARTUPINFOEX
                var si = new STARTUPINFOEX();
                si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
                si.lpAttributeList = attrList;

                // 6. 启动进程
                string cmdLine = $"\"{fileName}\" {arguments}";
                bool ok = CreateProcess(
                    null,
                    cmdLine,
                    IntPtr.Zero, IntPtr.Zero,
                    false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE,
                    envBlock,
                    workingDirectory,
                    ref si,
                    out var pi);

                if (!ok)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
            }
            finally
            {
                Marshal.FreeHGlobal(envBlock);
            }
        }
        finally
        {
            DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
        }
    }

    private static Dictionary<string, string> GetEnvironmentVariables()
    {
        var environmentVariables = new Dictionary<string, string>();
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            environmentVariables[entry.Key.ToString()] = entry.Value.ToString();
        return environmentVariables;
    }

    static void Main(string[] args)
    {
        while (true)
        {
            using var pipeServer = new NamedPipeServerStream("RunGame_OriginDebug", PipeDirection.In);
            pipeServer.WaitForConnection();
            try
            {
                string serializedData;
                using (var reader = new StreamReader(pipeServer))
                    serializedData = reader.ReadLine();

                string[] data = serializedData.Split(';');
                string fileName = data[0];
                string workingDir = data[1];
                string arguments = data[2];
                string originPCToken = data[3];
                string playerName = data[4];
                string eaRtPLaunch = data[5];
                string contentId = data[6];
                string EAGameLocale = data[7];

                var env = GetEnvironmentVariables();
                env["EAFreeTrialGame"] = "false";
                env["EAAuthCode"] = "NeedsAFreshAuthCode";
                env["EALaunchOfflineMode"] = "false";
                env["OriginSessionKey"] = Guid.NewGuid().ToString();
                env["EAGameLocale"] = EAGameLocale;
                env["EALaunchEnv"] = "production";
                env["EALaunchEAID"] = playerName;
                env["EALicenseToken"] = "Origin.OFR.50.0000721";
                env["EAEntitlementSource"] = "EA";
                env["EAUseIGOAPI"] = "1";
                env["EALaunchUserAuthToken"] = originPCToken;
                env["EAGenericAuthToken"] = originPCToken;
                env["EALaunchCode"] = "unavailable";
                env["EARtPLaunchCode"] = eaRtPLaunch;
                env["EALsxPort"] = "3216";
                env["EAEgsProxyIpcPort"] = "1705";
                env["EASteamProxyIpcPort"] = "1704";
                env["EAExternalSource"] = "EA";
                env["EASecureLaunchTokenTemp"] = "1001006949032";
                env["SteamAppId"] = "";
                env["ContentId"] = contentId;
                env["EAConnectionId"] = contentId;
                env["OPENSSL_ia32cap"] = "~0x200000200000000";
                env["EALaunchOwner"] = "EA";
                env["EAAccessTokenJWS"] = originPCToken;
                StartWithExplorerParent(fileName, arguments, workingDir, env);
            }
            catch (Exception ex)
            {

            }
        }
    }
}
