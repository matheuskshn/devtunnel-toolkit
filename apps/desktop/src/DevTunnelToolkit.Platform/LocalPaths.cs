using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.Platform;

public sealed class LocalPaths
{
    public string DataDirectory { get; }
    public string DatabasePath => Path.Combine(DataDirectory, "state.db");
    public string SocketPath => Path.Combine(DataDirectory, "agent.sock");
    public string PipeName => "devtunnel-toolkit-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DataDirectory)))[..24];
    public string VaultDirectory => Path.Combine(DataDirectory, "vault");

    public LocalPaths(string? dataDirectory = null)
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        DataDirectory = Path.GetFullPath(dataDirectory ?? Path.Combine(baseDir, "devtunnel-toolkit"));
        if (Path.GetPathRoot(DataDirectory) == DataDirectory) throw new ToolkitException("INVALID_DATA_DIRECTORY");
    }

    public void Initialize() => EnsurePrivateDirectory(DataDirectory);

    public static void EnsurePrivateFile(string path)
    {
        RejectLinks(path);
        if (!File.Exists(path)) return;
        if (OperatingSystem.IsWindows())
        {
            var identity = WindowsIdentity.GetCurrent().User ?? throw new ToolkitException("USER_IDENTITY_UNAVAILABLE");
            var security = new FileSecurity();
            security.SetOwner(identity);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static void RejectLinks(string path)
    {
        for (var item = new DirectoryInfo(Path.GetFullPath(path)); item is not null; item = item.Parent)
        {
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new ToolkitException("DATA_PATH_LINK_REJECTED");
        }
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new ToolkitException("DATA_PATH_LINK_REJECTED");
    }

    public static void EnsurePrivateDirectory(string path)
    {
        RejectLinks(path);
        if (OperatingSystem.IsWindows())
        {
            var info = Directory.CreateDirectory(path);
            var identity = WindowsIdentity.GetCurrent().User ?? throw new ToolkitException("USER_IDENTITY_UNAVAILABLE");
            var security = new DirectorySecurity();
            security.SetOwner(identity);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public static async Task WritePrivateAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        RejectLinks(path);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temp, options))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
