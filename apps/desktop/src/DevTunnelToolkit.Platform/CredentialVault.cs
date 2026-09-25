using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DevTunnelToolkit.Core;

namespace DevTunnelToolkit.Platform;

/// <summary>Platform protected secrets; no plaintext persistence or silent fallback.</summary>
public sealed partial class CredentialVault : ICredentialVault, IDisposable
{
    private readonly LocalPaths paths;
    private readonly byte[]? key;
    private readonly string scope;
    private readonly string? secretTool;
    private readonly object keySync = new();
    private int disposed;
    public const int MaxSecretBytes = 1024 * 1024;
    private const int MaxEncryptedPayloadBytes = MaxSecretBytes + 4096;
    public bool Available => Volatile.Read(ref disposed) == 0 && (key is not null || OperatingSystem.IsWindows() || secretTool is not null);

    public CredentialVault(LocalPaths paths, string? encryptionKey = null)
    {
        this.paths = paths;
        scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paths.DataDirectory)));
        encryptionKey ??= Environment.GetEnvironmentVariable("TOOLKIT_CREDENTIAL_KEY");
        if (!string.IsNullOrEmpty(encryptionKey))
        {
            if (encryptionKey.Length != 44) throw new ToolkitException("INVALID_CREDENTIAL_KEY");
            try { key = Convert.FromBase64String(encryptionKey); }
            catch (FormatException) { throw new ToolkitException("INVALID_CREDENTIAL_KEY"); }
            if (key.Length != 32)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new ToolkitException("INVALID_CREDENTIAL_KEY");
            }
        }
        if (OperatingSystem.IsLinux()) secretTool = new[] { "/usr/bin/secret-tool", "/bin/secret-tool" }.FirstOrDefault(File.Exists);
        if (key is not null || OperatingSystem.IsWindows()) LocalPaths.EnsurePrivateDirectory(paths.VaultDirectory);
    }

    [GeneratedRegex("^[a-zA-Z0-9_.-]{1,160}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    private string SecretPath(string name)
    {
        if (!KeyPattern().IsMatch(name)) throw new ToolkitException("INVALID_SECRET_REFERENCE");
        return Path.Combine(paths.VaultDirectory, name + ".secret");
    }

    public async Task<string?> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var path = SecretPath(name);
        if (key is null && !OperatingSystem.IsWindows()) return await SecretToolAsync("lookup", name, null, cancellationToken);
        LocalPaths.RejectLinks(path);
        if (!File.Exists(path)) return null;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length > MaxEncryptedPayloadBytes) throw new ToolkitException("SECRET_TOO_LARGE");
        var payload = new byte[(int)file.Length];
        await file.ReadExactlyAsync(payload, cancellationToken);
        byte[]? plain = null;
        try
        {
            lock (keySync)
            {
                ThrowIfDisposed();
                if (key is not null)
                {
                    if (payload.Length < 29 || payload[0] != 1) throw new ToolkitException("VAULT_DECRYPTION_FAILED");
                    if (payload.Length - 29 > MaxSecretBytes) throw new ToolkitException("SECRET_TOO_LARGE");
                    plain = new byte[payload.Length - 29];
                    using var aes = new AesGcm(key, 16);
                    aes.Decrypt(payload.AsSpan(1, 12), payload.AsSpan(29), payload.AsSpan(13, 16), plain, Encoding.UTF8.GetBytes(scope + ":" + name));
                }
                else if (OperatingSystem.IsWindows()) plain = ProtectedData.Unprotect(payload, Encoding.UTF8.GetBytes(scope + ":" + name), DataProtectionScope.CurrentUser);
                else throw new ToolkitException("CREDENTIAL_VAULT_UNAVAILABLE");
            }
            if (plain.Length > MaxSecretBytes) throw new ToolkitException("SECRET_TOO_LARGE");
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException) { throw new ToolkitException("VAULT_DECRYPTION_FAILED"); }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task WriteAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var path = SecretPath(name);
        if (Encoding.UTF8.GetByteCount(value) > MaxSecretBytes) throw new ToolkitException("SECRET_TOO_LARGE");
        if (key is null && !OperatingSystem.IsWindows())
        {
            await SecretToolAsync("store", name, value, cancellationToken);
            return;
        }
        var plain = Encoding.UTF8.GetBytes(value);
        try
        {
            byte[] payload;
            lock (keySync)
            {
                ThrowIfDisposed();
                if (key is not null)
                {
                    payload = new byte[29 + plain.Length];
                    payload[0] = 1;
                    RandomNumberGenerator.Fill(payload.AsSpan(1, 12));
                    using var aes = new AesGcm(key, 16);
                    aes.Encrypt(payload.AsSpan(1, 12), plain, payload.AsSpan(29), payload.AsSpan(13, 16), Encoding.UTF8.GetBytes(scope + ":" + name));
                }
                else if (OperatingSystem.IsWindows()) payload = ProtectedData.Protect(plain, Encoding.UTF8.GetBytes(scope + ":" + name), DataProtectionScope.CurrentUser);
                else throw new ToolkitException("CREDENTIAL_VAULT_UNAVAILABLE");
            }
            await LocalPaths.WritePrivateAsync(path, payload, cancellationToken);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var path = SecretPath(name);
        if (key is null && !OperatingSystem.IsWindows()) await SecretToolAsync("clear", name, null, cancellationToken);
        else
        {
            LocalPaths.RejectLinks(path);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private async Task<string?> SecretToolAsync(string operation, string name, string? value, CancellationToken cancellationToken)
    {
        if (secretTool is null) throw new ToolkitException("CREDENTIAL_VAULT_UNAVAILABLE", "Install libsecret-tools and unlock your desktop keyring, or supply TOOLKIT_CREDENTIAL_KEY for headless use.");
        var info = new ProcessStartInfo(secretTool) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        info.ArgumentList.Add(operation);
        if (operation == "store") info.ArgumentList.Add("--label=DevTunnel Toolkit");
        foreach (var argument in new[] { "application", "devtunnel-toolkit", "scope", scope, "reference", name }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new ToolkitException("CREDENTIAL_VAULT_UNAVAILABLE");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var output = ReadProcessTextAsync(process.StandardOutput.BaseStream, MaxSecretBytes + 2, timeout);
            var error = ReadProcessTextAsync(process.StandardError.BaseStream, 65536, timeout);
            if (value is not null) await process.StandardInput.WriteAsync(value.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token));
            var result = await output;
            var failed = await error;
            if (process.ExitCode == 0)
            {
                var trimmed = result.TrimEnd('\r', '\n');
                if (Encoding.UTF8.GetByteCount(trimmed) > MaxSecretBytes) throw new ToolkitException("SECRET_TOO_LARGE");
                return trimmed;
            }
            if (process.ExitCode == 1 && operation is "lookup" or "clear" && string.IsNullOrWhiteSpace(failed)) return null;
            throw new ToolkitException("CREDENTIAL_VAULT_UNAVAILABLE");
        }
        finally { if (!process.HasExited) process.Kill(true); }
    }

    private static async Task<string> ReadProcessTextAsync(Stream stream, int limit, CancellationTokenSource cancellation)
    {
        using var content = new MemoryStream();
        var buffer = new byte[4096];
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellation.Token)) != 0)
            {
                if (content.Length + count > limit)
                {
                    await cancellation.CancelAsync();
                    throw new ToolkitException("SECRET_TOO_LARGE");
                }
                await content.WriteAsync(buffer.AsMemory(0, count), cancellation.Token);
            }
            return Encoding.UTF8.GetString(content.GetBuffer(), 0, (int)content.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (content.TryGetBuffer(out var bytes)) CryptographicOperations.ZeroMemory(bytes.AsSpan());
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    public void Dispose()
    {
        lock (keySync)
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0 && key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }
}
