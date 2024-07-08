using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ComponentModel;
using System.Linq;
using System.Windows.Forms;
using System.Drawing;
using System.IO;

namespace ServiceTogglerApp
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            if (!IsRunAsAdministrator())
            {
                // Restart and run as admin
                var exeName = Process.GetCurrentProcess().MainModule.FileName;
                ProcessStartInfo startInfo = new ProcessStartInfo(exeName);
                startInfo.Verb = "runas";
                startInfo.Arguments = string.Join(" ", Environment.GetCommandLineArgs().Skip(1));
                try
                {
                    Process.Start(startInfo);
                }
                catch (Win32Exception)
                {
                    // The user refused the elevation
                    MessageBox.Show("This application requires administrator privileges to function properly.");
                }
                Application.Exit();
                return;
            }

            ApplicationConfiguration.Initialize();
            Application.Run(new ServiceToggler());
        }

        private static bool IsRunAsAdministrator()
        {
            WindowsIdentity id = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(id);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public class ServiceToggler : Form
    {
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatus(IntPtr hService, out SERVICE_STATUS lpServiceStatus);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern IntPtr OpenSCManager(string lpMachineName, string lpDatabaseName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr hSCObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool StartService(IntPtr hService, int dwNumServiceArgs, string[] lpServiceArgVectors);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ControlService(IntPtr hService, uint dwControl, out SERVICE_STATUS lpServiceStatus);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ChangeServiceConfig(IntPtr hService, uint dwServiceType, uint dwStartType, uint dwErrorControl, string lpBinaryPathName, string lpLoadOrderGroup, IntPtr lpdwTagId, string lpDependencies, string lpServiceStartName, string lpPassword, string lpDisplayName);

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [StructLayout(LayoutKind.Sequential)]
        public struct SERVICE_STATUS
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
        }

        const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
        const uint SERVICE_ALL_ACCESS = 0xF01FF;
        const uint SERVICE_QUERY_STATUS = 0x0004;
        const uint SERVICE_CHANGE_CONFIG = 0x0002;
        const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
        const uint SERVICE_DISABLED = 0x00000004;
        const uint SERVICE_AUTO_START = 0x00000002;

        const int HOTKEY_ID = 9000;
        const uint MOD_CTRL = 0x0002;
        const uint MOD_SHIFT = 0x0004;
        const uint VK_K = 0x4B;  // 'K' key

        private string _serviceName;

        public ServiceToggler()
        {
            InitializeServiceToggler();
            SetupForm();
        }

        private void InitializeServiceToggler()
        {
            _serviceName = "WpcMonSvc";

            if (!RegisterHotKey(this.Handle, HOTKEY_ID, MOD_CTRL | MOD_SHIFT, VK_K))
            {
                MessageBox.Show("Failed to register hotkey.");
                return;
            }

            MessageBox.Show("Hotkey registered. Press Ctrl + Shift + K to toggle the service.\nPress 'Q' to quit the program.");
        }

        private void SetupForm()
        {
            this.Text = "Service Toggler";
            this.Size = new System.Drawing.Size(300, 200);

            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
            if (File.Exists(iconPath))
            {
                try
                {
                    this.Icon = new Icon(iconPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error loading icon: {ex.Message}");
                }
            }
            else
            {
                MessageBox.Show($"Icon not found at: {iconPath}");
            }
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            if (m.Msg == 0x0312 && m.WParam.ToInt32() == HOTKEY_ID)
            {
                ToggleService(_serviceName);
            }
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);

            if (e.KeyChar == 'q' || e.KeyChar == 'Q')
            {
                Application.Exit();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            UnregisterHotKey(this.Handle, HOTKEY_ID);
        }

        static void ToggleService(string serviceName)
        {
            IntPtr scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
            if (scm == IntPtr.Zero)
            {
                MessageBox.Show("Failed to open Service Control Manager.");
                return;
            }

            IntPtr service = OpenService(scm, serviceName, SERVICE_ALL_ACCESS);
            if (service == IntPtr.Zero)
            {
                MessageBox.Show($"Failed to open service: {serviceName}");
                CloseServiceHandle(scm);
                return;
            }

            try
            {
                SERVICE_STATUS status;
                if (QueryServiceStatus(service, out status))
                {
                    if (status.dwCurrentState == 4) // Running
                    {
                        ControlService(service, 1, out status); // 1 = stop
                        ChangeServiceConfig(service, SERVICE_NO_CHANGE, SERVICE_DISABLED, SERVICE_NO_CHANGE, null, null, IntPtr.Zero, null, null, null, string.Empty);
                        MessageBox.Show($"{serviceName} service has been stopped and disabled.");
                    }
                    else if (status.dwCurrentState == 1) // Stopped
                    {
                        ChangeServiceConfig(service, SERVICE_NO_CHANGE, SERVICE_AUTO_START, SERVICE_NO_CHANGE, null, null, IntPtr.Zero, null, null, null, string.Empty);
                        StartService(service, 0, Array.Empty<string>());
                        MessageBox.Show($"{serviceName} service has been enabled and started.");
                    }
                    else
                    {
                        MessageBox.Show($"{serviceName} service is in an unexpected state: {status.dwCurrentState}");
                    }
                }
                else
                {
                    MessageBox.Show("Failed to query service status.");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred: {ex.Message}");
            }
            finally
            {
                CloseServiceHandle(service);
                CloseServiceHandle(scm);
            }
        }
    }
}