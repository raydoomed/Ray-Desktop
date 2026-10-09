using System;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ChildSessionDesktop
{
    // Child Sessions share the user's filesystem but have separate session clipboards.
    // Relay CF_HDROP paths between the two sessions; the files themselves stay in place.
    internal sealed class ClipboardFileRelay : IDisposable
    {
        internal const int WmClipboardUpdate = 0x031D;
        private const int ClipboardMessageMagic = 0x31465344;
        private const string RunValueName = "RayDesktopClipboardAgent";
        private readonly bool isChild;
        private readonly string incomingPath;
        private readonly string outgoingPath;
        private Guid lastIncomingId;
        private uint suppressSequence;
        private string lastSentFingerprint;
        private string lastAppliedFingerprint;
        private DateTime lastSentAt;
        private DateTime lastAppliedAt;
        private bool disposed;
        private bool clipboardErrorLogged;
        private bool retryClipboardRead;

        internal ClipboardFileRelay(bool child)
        {
            isChild = child;
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RayDesktop", "ClipboardBridge");
            incomingPath = Path.Combine(directory, child ? "host-to-child.bin" : "child-to-host.bin");
            outgoingPath = Path.Combine(directory, child ? "child-to-host.bin" : "host-to-child.bin");
        }

        internal static bool AddClipboardFormatListener(IntPtr hwnd)
        {
            return AddClipboardFormatListenerNative(hwnd);
        }

        internal static bool RemoveClipboardFormatListener(IntPtr hwnd)
        {
            return RemoveClipboardFormatListenerNative(hwnd);
        }

        internal static void RegisterChildAgent()
        {
            uint sessionId;
            if (!Program.ProcessIdToSessionId(Program.GetCurrentProcessId(), out sessionId))
            {
                Program.Log("Clipboard bridge agent not registered: could not determine the host session ID");
                return;
            }

            try
            {
                string executable = Application.ExecutablePath;
                string command = "\"" + executable + "\" --clipboard-agent --parent-session " + sessionId;
                using (RegistryKey runKey = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (runKey == null) throw new InvalidOperationException("Could not open the current-user startup key.");
                    runKey.SetValue(RunValueName, command, RegistryValueKind.String);
                }
                Program.Log("Registered the automatic child-session file clipboard bridge");
            }
            catch (Exception ex)
            {
                Program.Log("Could not register the child-session file clipboard bridge: " + ex.GetBaseException().Message);
            }
        }

        internal static void ClearMailbox()
        {
            try
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RayDesktop", "ClipboardBridge");
                foreach (string name in new[] { "host-to-child.bin", "child-to-host.bin" })
                {
                    string path = Path.Combine(directory, name);
                    if (File.Exists(path)) File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Program.Log("Could not clear temporary file clipboard data: " + ex.GetBaseException().Message);
            }
        }

        internal static bool ShouldRunChildAgent(uint parentSessionId)
        {
            uint processSessionId;
            if (!Program.ProcessIdToSessionId(Program.GetCurrentProcessId(), out processSessionId)) return false;
            uint consoleSessionId = Program.WTSGetActiveConsoleSessionId();
            // The startup entry also runs in the normal desktop. It must stay inert there.
            if (processSessionId == consoleSessionId || processSessionId == parentSessionId) return false;
            return true;
        }

        internal void OnClipboardChanged()
        {
            if (disposed) return;
            retryClipboardRead = false;
            try
            {
                uint sequence = GetClipboardSequenceNumber();
                if (suppressSequence != 0 && sequence == suppressSequence)
                {
                    suppressSequence = 0;
                    return;
                }

                StringCollection dropList = Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList() : null;
                string text = null;
                if (dropList == null || dropList.Count == 0)
                {
                    if (Clipboard.ContainsText()) text = Clipboard.GetText();
                    if (string.IsNullOrEmpty(text))
                    {
                        lastSentFingerprint = null;
                        lastAppliedFingerprint = null;
                        return;
                    }
                }

                string[] paths;
                if (dropList != null && dropList.Count > 0)
                {
                    paths = new string[dropList.Count];
                    for (int i = 0; i < dropList.Count; i++) paths[i] = dropList[i];
                }
                else paths = new string[0];

                string fingerprint = paths.Length > 0 ? Fingerprint(paths) : FingerprintText(text);
                DateTime now = DateTime.UtcNow;
                if ((fingerprint == lastAppliedFingerprint && now - lastAppliedAt < TimeSpan.FromSeconds(3)) ||
                    (fingerprint == lastSentFingerprint && now - lastSentAt < TimeSpan.FromSeconds(3)))
                    return;

                WriteMessage(outgoingPath, paths, text);
                lastSentFingerprint = fingerprint;
                lastSentAt = now;
                Program.Log("Clipboard relayed " + (paths.Length > 0 ? paths.Length + " file path(s) " : "text ") + (isChild ? "from child session" : "from host session"));
            }
            catch (ExternalException)
            {
                // Clipboard owners may briefly keep it open; retry from the existing timer.
                retryClipboardRead = true;
            }
            catch (Exception ex)
            {
                if (!clipboardErrorLogged)
                {
                    clipboardErrorLogged = true;
                    Program.Log("File clipboard relay could not read the clipboard: " + ex.GetBaseException().Message);
                }
            }
        }

        internal void RetryPendingClipboardRead()
        {
            if (!disposed && retryClipboardRead) OnClipboardChanged();
        }

        internal void PollIncoming()
        {
            if (disposed || !File.Exists(incomingPath)) return;
            try
            {
                Guid messageId;
                string[] paths;
                string text;
                if (!ReadMessage(incomingPath, out messageId, out paths, out text) || messageId == lastIncomingId) return;
                if (paths.Length == 0 && string.IsNullOrEmpty(text))
                {
                    lastIncomingId = messageId;
                    return;
                }

                string fingerprint = paths.Length > 0 ? Fingerprint(paths) : FingerprintText(text);
                // Never re-apply content this session just sent out: it is an echo of our own
                // clipboard (round-tripped via the other session). Re-applying it overwrites the
                // local clipboard and breaks same-session copy/paste.
                if (fingerprint == lastSentFingerprint)
                {
                    lastIncomingId = messageId;
                    return;
                }
                if (paths.Length > 0)
                {
                    var dropList = new StringCollection();
                    foreach (string path in paths) dropList.Add(path);
                    Clipboard.SetFileDropList(dropList);
                }
                else Clipboard.SetText(text);
                // Mark the message only after the clipboard accepted it. If another process
                // temporarily owns the clipboard, the next poll must retry this same message.
                lastIncomingId = messageId;
                lastAppliedFingerprint = fingerprint;
                lastAppliedAt = DateTime.UtcNow;
                suppressSequence = GetClipboardSequenceNumber();
                Program.Log("Clipboard received " + (paths.Length > 0 ? paths.Length + " file path(s) " : "text ") + (isChild ? "in child session" : "in host session"));
            }
            catch (IOException)
            {
                // A producer is atomically replacing the mailbox file; retry on the next timer tick.
            }
            catch (ExternalException)
            {
                // Retry when another process releases the clipboard.
            }
            catch (Exception ex)
            {
                Program.Log("File clipboard relay could not apply the received file list: " + ex.GetBaseException().Message);
            }
        }

        private static string Fingerprint(string[] paths)
        {
            using (SHA256 hash = SHA256.Create())
            {
                byte[] digest = hash.ComputeHash(Encoding.Unicode.GetBytes(string.Join("\0", paths)));
                return BitConverter.ToString(digest);
            }
        }

        private static string FingerprintText(string text)
        {
            using (SHA256 hash = SHA256.Create())
            {
                byte[] digest = hash.ComputeHash(Encoding.Unicode.GetBytes(text ?? string.Empty));
                return BitConverter.ToString(digest);
            }
        }

        private static void WriteMessage(string path, string[] paths, string text)
        {
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            Guid messageId = Guid.NewGuid();
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    writer.Write(ClipboardMessageMagic);
                    writer.Write(2); // mailbox format version
                    writer.Write(messageId.ToByteArray());
                    writer.Write(text ?? string.Empty);
                    writer.Write(paths.Length);
                    foreach (string item in paths) writer.Write(item ?? string.Empty);
                }

                if (File.Exists(path))
                {
                    try { File.Replace(temporary, path, null); }
                    catch (FileNotFoundException) { File.Move(temporary, path); }
                }
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static bool ReadMessage(string path, out Guid messageId, out string[] paths, out string text)
        {
            messageId = Guid.Empty;
            paths = new string[0];
            text = null;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                if (reader.ReadInt32() != ClipboardMessageMagic) return false;
                if (reader.ReadInt32() != 2) return false;
                byte[] idBytes = reader.ReadBytes(16);
                if (idBytes.Length != 16) return false;
                messageId = new Guid(idBytes);
                text = reader.ReadString();
                int count = reader.ReadInt32();
                if (count < 0 || count > 4096) return false;
                paths = new string[count];
                for (int i = 0; i < count; i++) paths[i] = reader.ReadString();
                return true;
            }
        }

        public void Dispose()
        {
            disposed = true;
        }

        [DllImport("user32.dll", EntryPoint = "AddClipboardFormatListener", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddClipboardFormatListenerNative(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "RemoveClipboardFormatListener", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveClipboardFormatListenerNative(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();
    }

    internal sealed class ClipboardAgentContext : ApplicationContext
    {
        private readonly ClipboardFileRelay relay;
        private readonly ClipboardAgentWindow window;
        private readonly Timer pollTimer;

        internal ClipboardAgentContext()
        {
            relay = new ClipboardFileRelay(true);
            window = new ClipboardAgentWindow(relay);
            pollTimer = new Timer { Interval = 250 };
            pollTimer.Tick += delegate
            {
                relay.PollIncoming();
                relay.RetryPendingClipboardRead();
            };
            pollTimer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                pollTimer.Stop();
                pollTimer.Dispose();
                window.Dispose();
                relay.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class ClipboardAgentWindow : NativeWindow, IDisposable
    {
        private readonly ClipboardFileRelay relay;

        internal ClipboardAgentWindow(ClipboardFileRelay fileRelay)
        {
            relay = fileRelay;
            var parameters = new CreateParams
            {
                Caption = "Ray Desktop Clipboard Bridge",
                Parent = new IntPtr(-3) // HWND_MESSAGE: no visible window or taskbar entry.
            };
            CreateHandle(parameters);
            if (!ClipboardFileRelay.AddClipboardFormatListener(Handle))
                Program.Log("Could not register the child-session clipboard listener; Windows error " + Marshal.GetLastWin32Error());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == ClipboardFileRelay.WmClipboardUpdate) relay.OnClipboardChanged();
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                ClipboardFileRelay.RemoveClipboardFormatListener(Handle);
                DestroyHandle();
            }
        }
    }
}
