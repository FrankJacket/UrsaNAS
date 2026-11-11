using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Nas.Agent.Services;

/// <summary>
/// Un programme (par défaut un shell de connexion) branché sur un pseudo-terminal Linux. Les octets reçus de <see cref="Output"/> sont ce que
/// le terminal affiche ; ceux écrits dans <see cref="Input"/> sont ce que l'utilisateur tape.
/// </summary>
public sealed class PtySession : IDisposable
{
    const int O_RDWR = 2, O_NOCTTY = 0x100;
    const ulong TIOCSWINSZ = 0x5414;

    [StructLayout(LayoutKind.Sequential)]
    struct WinSize { public ushort Rows, Cols, X, Y; }

    [DllImport("libc", SetLastError = true)] static extern int posix_openpt(int flags);
    [DllImport("libc", SetLastError = true)] static extern int grantpt(int fd);
    [DllImport("libc", SetLastError = true)] static extern int unlockpt(int fd);
    [DllImport("libc", SetLastError = true)] static extern int ptsname_r(int fd, byte[] buf, nuint len);
    [DllImport("libc", SetLastError = true)] static extern int ioctl(int fd, ulong request, ref WinSize size);

    readonly SafeFileHandle master;
    readonly Process process;
    readonly FileStream stream;

    public Stream Input => stream;
    public Stream Output => stream;
    public bool HasExited { get { try { return process.HasExited; } catch (InvalidOperationException) { return true; } } }

    PtySession(SafeFileHandle master, Process process)
    {
        this.master = master;
        this.process = process;
        stream = new FileStream(master, FileAccess.ReadWrite, bufferSize: 1, isAsync: false);
    }

    /// <summary>Lance <paramref name="fileName"/> avec ses arguments sur un nouveau terminal de <paramref name="cols"/> x <paramref name="rows"/>.</summary>
    public static PtySession Start(string fileName, IEnumerable<string> args, int cols, int rows, string workDir = "/", IDictionary<string, string>? env = null)
    {
        var fd = posix_openpt(O_RDWR | O_NOCTTY);
        if (fd < 0) throw new IOException("posix_openpt failed (errno " + Marshal.GetLastPInvokeError() + ").");
        var handle = new SafeFileHandle(fd, ownsHandle: true);
        string slave;
        try
        {
            if (grantpt(fd) != 0 || unlockpt(fd) != 0) throw new IOException("The pseudo-terminal could not be set up.");
            var name = new byte[256];
            if (ptsname_r(fd, name, (nuint)name.Length) != 0) throw new IOException("The pseudo-terminal name is unavailable.");
            slave = System.Text.Encoding.UTF8.GetString(name, 0, Array.IndexOf(name, (byte)0));
            Resize(handle, cols, rows);

            // Le shell ouvre lui-même l'esclave comme entrée et sorties, puis « setsid -c » en fait son terminal de contrôle (Ctrl+C, redimensionnement…).
            var psi = new ProcessStartInfo("/bin/sh") { WorkingDirectory = workDir, UseShellExecute = false };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("exec setsid -w -c \"$@\" <>\"$0\" >&0 2>&1");
            psi.ArgumentList.Add(slave);
            psi.ArgumentList.Add(fileName);
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["TERM"] = "xterm-256color";
            foreach (var (k, v) in env ?? new Dictionary<string, string>()) psi.Environment[k] = v;
            var p = Process.Start(psi) ?? throw new IOException("The shell could not be started.");
            return new PtySession(handle, p);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    static void Resize(SafeFileHandle h, int cols, int rows)
    {
        var ws = new WinSize { Cols = (ushort)Math.Clamp(cols, 1, 1000), Rows = (ushort)Math.Clamp(rows, 1, 500) };
        ioctl((int)h.DangerousGetHandle(), TIOCSWINSZ, ref ws);
    }

    public void Resize(int cols, int rows) { if (!master.IsClosed) Resize(master, cols, rows); }

    bool disposed;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        try { stream.Dispose(); } catch (IOException) { }
        process.Dispose();
    }
}
