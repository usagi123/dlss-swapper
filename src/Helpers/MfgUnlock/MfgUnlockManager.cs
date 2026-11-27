using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DLSS_Swapper.Data;

namespace DLSS_Swapper.Helpers.MfgUnlock;

/// <summary>
/// Installs, configures and removes the RTX 30/20 MFG unlock
/// (https://github.com/mcsoderh/RTX30MFG-Unlock) for a single game.
///
/// The unlock is three files placed beside the game's real executable
/// (RTX40MFGCore.dll, RTX40MFG.asi, RTX40MFG-UI.addon64) plus Ultimate ASI Loader
/// installed under a proxy DLL name with a matching .ini that loads RTX40MFG.asi.
/// Every file that is overwritten is backed up first and every change is recorded
/// in a per-game manifest so it can be fully reverted.
/// </summary>
public partial class MfgUnlockManager
{
    public static MfgUnlockManager Instance { get; } = new MfgUnlockManager();

    public const string ModRepository = "mcsoderh/RTX30MFG-Unlock";
    public const string AsiLoaderRepository = "ThirteenAG/Ultimate-ASI-Loader";
    const string AsiLoaderAssetName = "Ultimate-ASI-Loader_x64.zip";

    public const string CoreFileName = "RTX40MFGCore.dll";
    public const string AsiFileName = "RTX40MFG.asi";
    public const string ReShadeAddonFileName = "RTX40MFG-UI.addon64";
    static readonly string[] ModFiles = [CoreFileName, AsiFileName, ReShadeAddonFileName];
    static readonly string[] LegacyModFiles = ["RTX40MFG-Universal.asi", "RTX40MFG-Bridge.asi"];

    public const string ControlFileName = "RTX40MFG-Universal.json";
    public const string StatusFileName = "RTX40MFG-Universal.status.json";
    const string ManifestFileName = "DLSSSwapper-MFGUnlock.json";
    const string BackupFolderName = "DLSSSwapper-MFGUnlock-backup";

    /// <summary>Proxy names offered for Ultimate ASI Loader, in automatic preference order.</summary>
    public static readonly string[] ProxyCandidates = ["dinput8.dll", "version.dll", "winmm.dll"];

    /// <summary>Every proxy name Ultimate ASI Loader supports on x64, used to detect an existing loader.</summary>
    static readonly string[] KnownLoaderProxyNames =
    [
        "dinput8.dll", "version.dll", "winmm.dll", "winhttp.dll", "wininet.dll", "dsound.dll",
        "d3d9.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll", "dxgi.dll", "xinput1_3.dll",
        "xinput1_4.dll", "xinput9_1_0.dll", "binkw64.dll", "bink2w64.dll", "vorbisfile.dll",
    ];

    static readonly Dictionary<string, string> RequiredIniValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["LoadPlugins"] = "1",
        ["LoadFromScriptsOnly"] = "1",
        ["LoadExtraPlugins"] = AsiFileName,
        ["DontLoadFromDllMain"] = "0",
        ["ForceEntryPointHook"] = "0",
    };

    static string CacheRoot => Path.Combine(Storage.GetStorageFolder(), "mfg_unlock");

    MfgUnlockManager()
    {
    }

    #region Downloads

    public record DownloadedPackage(string Tag, string Directory);

    async Task<JsonObject> GetLatestReleaseAsync(string repository)
    {
        var url = $"https://api.github.com/repos/{repository}/releases/latest";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Accept", "application/vnd.github+json");
        using var response = await App.CurrentApp.HttpClient.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return JsonNode.Parse(json)?.AsObject() ?? throw new Exception($"Could not read latest release of {repository}.");
    }

    static IEnumerable<(string Name, string Url)> GetAssets(JsonObject release)
    {
        if (release["assets"] is not JsonArray assets)
        {
            yield break;
        }

        foreach (var asset in assets.OfType<JsonObject>())
        {
            var name = asset["name"]?.GetValue<string>();
            var url = asset["browser_download_url"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name) == false && string.IsNullOrEmpty(url) == false)
            {
                yield return (name, url);
            }
        }
    }

    static string SafeTag(string tag)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(tag.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    async Task<byte[]> DownloadBytesAsync(string url)
    {
        return await App.CurrentApp.HttpClient.GetByteArrayAsync(url).ConfigureAwait(false);
    }

    static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    static string Sha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// Returns the newest locally cached mod package, or null if none has been downloaded.
    /// </summary>
    public DownloadedPackage? GetCachedModPackage()
    {
        return GetCachedPackage(Path.Combine(CacheRoot, "mod"), dir => ModFiles.All(f => File.Exists(Path.Combine(dir, f))));
    }

    public DownloadedPackage? GetCachedAsiLoader()
    {
        return GetCachedPackage(Path.Combine(CacheRoot, "asi_loader"), dir => File.Exists(Path.Combine(dir, "dinput8.dll")));
    }

    static DownloadedPackage? GetCachedPackage(string root, Func<string, bool> isComplete)
    {
        if (Directory.Exists(root) == false)
        {
            return null;
        }

        var newest = new DirectoryInfo(root)
            .GetDirectories()
            .Where(d => isComplete(d.FullName))
            .OrderByDescending(d => d.LastWriteTimeUtc)
            .FirstOrDefault();

        return newest is null ? null : new DownloadedPackage(newest.Name, newest.FullName);
    }

    /// <summary>
    /// Downloads the latest RTX30MFG-Unlock release, verifying the zip against the
    /// published .sha256 file and each extracted file against SHA256SUMS.txt.
    /// </summary>
    public async Task<DownloadedPackage> DownloadLatestModPackageAsync()
    {
        var release = await GetLatestReleaseAsync(ModRepository).ConfigureAwait(false);
        var tag = release["tag_name"]?.GetValue<string>() ?? throw new Exception("Latest release has no tag.");
        var assets = GetAssets(release).ToList();

        var zipAsset = assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (zipAsset == default)
        {
            throw new Exception($"Release {tag} of {ModRepository} has no .zip asset.");
        }

        var targetDirectory = Path.Combine(CacheRoot, "mod", SafeTag(tag));
        if (ModFiles.All(f => File.Exists(Path.Combine(targetDirectory, f))))
        {
            return new DownloadedPackage(tag, targetDirectory);
        }

        var zipBytes = await DownloadBytesAsync(zipAsset.Url).ConfigureAwait(false);

        // Verify the archive against the release's published hash when available.
        var shaAsset = assets.FirstOrDefault(a => a.Name.Equals(zipAsset.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
        if (shaAsset != default)
        {
            var shaText = Encoding.UTF8.GetString(await DownloadBytesAsync(shaAsset.Url).ConfigureAwait(false));
            var expected = shaText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            if (expected.Equals(Sha256Hex(zipBytes), StringComparison.OrdinalIgnoreCase) == false)
            {
                throw new Exception($"Downloaded {zipAsset.Name} does not match its published SHA-256 hash.");
            }
        }

        var stagingDirectory = targetDirectory + ".partial";
        if (Directory.Exists(stagingDirectory))
        {
            Directory.Delete(stagingDirectory, true);
        }
        Directory.CreateDirectory(stagingDirectory);

        using (var zipStream = new MemoryStream(zipBytes))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
        {
            foreach (var entry in archive.Entries)
            {
                // Only flat files are expected; ignore folders and anything that tries to escape.
                if (string.IsNullOrEmpty(entry.Name) || entry.FullName.Contains("..", StringComparison.Ordinal))
                {
                    continue;
                }
                entry.ExtractToFile(Path.Combine(stagingDirectory, entry.Name), true);
            }
        }

        var missing = ModFiles.Where(f => File.Exists(Path.Combine(stagingDirectory, f)) == false).ToList();
        if (missing.Count > 0)
        {
            throw new Exception($"Release {tag} is missing: {string.Join(", ", missing)}");
        }

        var sumsPath = Path.Combine(stagingDirectory, "SHA256SUMS.txt");
        if (File.Exists(sumsPath))
        {
            foreach (var line in File.ReadAllLines(sumsPath))
            {
                var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2)
                {
                    continue;
                }
                var fileName = parts[1].TrimStart('*').Trim();
                var filePath = Path.Combine(stagingDirectory, fileName);
                if (File.Exists(filePath) && Sha256Hex(filePath).Equals(parts[0], StringComparison.OrdinalIgnoreCase) == false)
                {
                    throw new Exception($"{fileName} in release {tag} does not match SHA256SUMS.txt.");
                }
            }
        }

        if (Directory.Exists(targetDirectory))
        {
            Directory.Delete(targetDirectory, true);
        }
        Directory.Move(stagingDirectory, targetDirectory);
        return new DownloadedPackage(tag, targetDirectory);
    }

    /// <summary>
    /// Downloads the latest x64 Ultimate ASI Loader and stores its DLL as dinput8.dll.
    /// </summary>
    public async Task<DownloadedPackage> DownloadLatestAsiLoaderAsync()
    {
        var release = await GetLatestReleaseAsync(AsiLoaderRepository).ConfigureAwait(false);
        var tag = release["tag_name"]?.GetValue<string>() ?? throw new Exception("Latest release has no tag.");
        var asset = GetAssets(release).FirstOrDefault(a => a.Name.Equals(AsiLoaderAssetName, StringComparison.OrdinalIgnoreCase));
        if (asset == default)
        {
            throw new Exception($"Release {tag} of {AsiLoaderRepository} has no {AsiLoaderAssetName}.");
        }

        var targetDirectory = Path.Combine(CacheRoot, "asi_loader", SafeTag(tag));
        var targetDll = Path.Combine(targetDirectory, "dinput8.dll");
        if (File.Exists(targetDll))
        {
            return new DownloadedPackage(tag, targetDirectory);
        }

        var zipBytes = await DownloadBytesAsync(asset.Url).ConfigureAwait(false);
        Directory.CreateDirectory(targetDirectory);
        using (var zipStream = new MemoryStream(zipBytes))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
        {
            var dllEntry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                ?? throw new Exception($"{AsiLoaderAssetName} does not contain a DLL.");
            dllEntry.ExtractToFile(targetDll + ".partial", true);
        }

        if (IsAsiLoader(targetDll + ".partial") == false)
        {
            File.Delete(targetDll + ".partial");
            throw new Exception("The downloaded file is not Ultimate ASI Loader.");
        }
        File.Move(targetDll + ".partial", targetDll, true);
        return new DownloadedPackage(tag, targetDirectory);
    }

    #endregion

    #region Detection

    static bool HasProduct(string path, string needle)
    {
        try
        {
            if (File.Exists(path) == false)
            {
                return false;
            }
            var info = FileVersionInfo.GetVersionInfo(path);
            var text = $"{info.ProductName} {info.FileDescription} {info.InternalName}".Replace("-", " ");
            return text.Contains(needle, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsAsiLoader(string path) => HasProduct(path, "ASI Loader");

    public static bool IsReShade(string path) => HasProduct(path, "ReShade");

    /// <summary>True if the game has a DLSS Frame Generation DLL, which the unlock requires.</summary>
    public static bool GameSupportsFrameGeneration(Game game)
    {
        return game.CurrentDLSS_G is not null || game.GameAssets.Any(a => a.AssetType == GameAssetType.DLSS_G || a.AssetType == GameAssetType.Streamline_DLSS_G);
    }

    [GeneratedRegex(@"(unins|setup|redist|vcredist|vc_redist|crash|reporter|helper|easyanticheat|eac_|battleye|be_service|dxsetup|dotnet|prereq|cefprocess|webhelper|updater|install|ue4prereq|ue5prereq|unitycrash|cleanup|touchup|activation|dxwebsetup)", RegexOptions.IgnoreCase)]
    private static partial Regex ExcludedExecutableRegex();

    static IEnumerable<string> EnumerateFilesSafe(string directory, string pattern, int maxDepth)
    {
        var pending = new Stack<(string Dir, int Depth)>();
        pending.Push((directory, 0));
        while (pending.Count > 0)
        {
            var (dir, depth) = pending.Pop();
            string[] files;
            try
            {
                files = Directory.GetFiles(dir, pattern);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            try
            {
                foreach (var sub in new DirectoryInfo(dir).GetDirectories())
                {
                    if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }
                    pending.Push((sub.FullName, depth + 1));
                }
            }
            catch
            {
                // Ignore unreadable folders.
            }
        }
    }

    /// <summary>
    /// Finds the game's real executable. The unlock must sit beside the process that loads
    /// Streamline, which for Unreal games is the *-Win64-Shipping.exe rather than the launcher stub.
    /// </summary>
    public string? FindGameExecutable(Game game)
    {
        if (string.IsNullOrWhiteSpace(game.InstallPath) || Directory.Exists(game.InstallPath) == false)
        {
            return null;
        }

        var executables = EnumerateFilesSafe(game.InstallPath, "*.exe", 6)
            .Where(p => ExcludedExecutableRegex().IsMatch(Path.GetFileName(p)) == false)
            .ToList();

        if (executables.Count == 0)
        {
            return null;
        }

        var shipping = executables.FirstOrDefault(p => Path.GetFileName(p).EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase));
        if (shipping is not null)
        {
            return shipping;
        }

        // Prefer an executable in the same folder as the Frame Generation / Streamline DLLs.
        var frameGenerationDirectories = game.GameAssets
            .Where(a => a.AssetType is GameAssetType.DLSS_G or GameAssetType.Streamline_DLSS_G or GameAssetType.Streamline_Interposer)
            .Select(a => Path.GetDirectoryName(a.Path))
            .Where(d => string.IsNullOrEmpty(d) == false)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var directory in frameGenerationDirectories)
        {
            var beside = executables
                .Where(p => string.Equals(Path.GetDirectoryName(p), directory, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => new FileInfo(p).Length)
                .FirstOrDefault();
            if (beside is not null)
            {
                return beside;
            }
        }

        return executables.OrderByDescending(p => new FileInfo(p).Length).First();
    }

    public static bool IsExecutableRunning(string exePath)
    {
        var name = Path.GetFileNameWithoutExtension(exePath);
        foreach (var process in Process.GetProcessesByName(name))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (path is null || string.Equals(path, exePath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // Access denied (e.g. elevated game or anti-cheat). Assume it is the game.
                return true;
            }
            finally
            {
                process.Dispose();
            }
        }
        return false;
    }

    #endregion

    #region Manifest

    public class InstallManifest
    {
        public string ExecutablePath { get; set; } = string.Empty;
        public string ModTag { get; set; } = string.Empty;
        public string LoaderProxy { get; set; } = string.Empty;
        public bool LoaderInstalledByUs { get; set; }
        public string IniFileName { get; set; } = string.Empty;
        public bool IniCreatedByUs { get; set; }
        public List<string> InstalledFiles { get; set; } = new List<string>();
        public List<string> BackedUpFiles { get; set; } = new List<string>();
        public DateTime InstalledAtUtc { get; set; }

        public JsonObject ToJson()
        {
            return new JsonObject
            {
                ["executablePath"] = ExecutablePath,
                ["modTag"] = ModTag,
                ["loaderProxy"] = LoaderProxy,
                ["loaderInstalledByUs"] = LoaderInstalledByUs,
                ["iniFileName"] = IniFileName,
                ["iniCreatedByUs"] = IniCreatedByUs,
                ["installedFiles"] = new JsonArray(InstalledFiles.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
                ["backedUpFiles"] = new JsonArray(BackedUpFiles.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
                ["installedAtUtc"] = InstalledAtUtc.ToString("o", CultureInfo.InvariantCulture),
                ["version"] = 1,
            };
        }

        public static InstallManifest FromJson(JsonObject json)
        {
            static List<string> ReadList(JsonNode? node) =>
                node is JsonArray array ? array.Select(n => n?.GetValue<string>()).OfType<string>().ToList() : new List<string>();

            var manifest = new InstallManifest
            {
                ExecutablePath = json["executablePath"]?.GetValue<string>() ?? string.Empty,
                ModTag = json["modTag"]?.GetValue<string>() ?? string.Empty,
                LoaderProxy = json["loaderProxy"]?.GetValue<string>() ?? string.Empty,
                LoaderInstalledByUs = json["loaderInstalledByUs"]?.GetValue<bool>() ?? false,
                IniFileName = json["iniFileName"]?.GetValue<string>() ?? string.Empty,
                IniCreatedByUs = json["iniCreatedByUs"]?.GetValue<bool>() ?? false,
                InstalledFiles = ReadList(json["installedFiles"]),
                BackedUpFiles = ReadList(json["backedUpFiles"]),
            };
            if (DateTime.TryParse(json["installedAtUtc"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var installedAt))
            {
                manifest.InstalledAtUtc = installedAt;
            }
            return manifest;
        }
    }

    /// <summary>
    /// Looks for an install made by DLSS Swapper anywhere in the game folder.
    /// </summary>
    public (string Directory, InstallManifest Manifest)? FindInstall(Game game)
    {
        if (string.IsNullOrWhiteSpace(game.InstallPath) || Directory.Exists(game.InstallPath) == false)
        {
            return null;
        }

        foreach (var manifestPath in EnumerateFilesSafe(game.InstallPath, ManifestFileName, 6))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(manifestPath)) is JsonObject json)
                {
                    return (Path.GetDirectoryName(manifestPath)!, InstallManifest.FromJson(json));
                }
            }
            catch (Exception err)
            {
                Logger.Error(err, $"Could not read {manifestPath}");
            }
        }
        return null;
    }

    /// <summary>True if the unlock files are in the folder but were not installed by DLSS Swapper.</summary>
    public static bool HasManualInstall(string directory)
    {
        return File.Exists(Path.Combine(directory, ManifestFileName)) == false && File.Exists(Path.Combine(directory, AsiFileName));
    }

    static void WriteManifest(string directory, InstallManifest manifest)
    {
        File.WriteAllText(Path.Combine(directory, ManifestFileName), manifest.ToJson().ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    #endregion

    #region Install / uninstall

    /// <summary>
    /// Installs (or updates) the unlock beside <paramref name="exePath"/>.
    /// </summary>
    /// <param name="requestedProxy">One of <see cref="ProxyCandidates"/>, or null to pick automatically.</param>
    /// <returns>Human readable log of what was done.</returns>
    public async Task<List<string>> InstallAsync(string exePath, string? requestedProxy)
    {
        var log = new List<string>();

        if (File.Exists(exePath) == false)
        {
            throw new Exception($"Executable not found: {exePath}");
        }
        if (IsExecutableRunning(exePath))
        {
            throw new Exception("Close the game before installing the MFG unlock.");
        }

        var mod = GetCachedModPackage();
        if (mod is null)
        {
            log.Add("Downloading the latest RTX30MFG-Unlock release...");
            mod = await DownloadLatestModPackageAsync();
        }

        var directory = Path.GetDirectoryName(exePath)!;
        var isFreshInstall = File.Exists(Path.Combine(directory, ManifestFileName)) == false;
        try
        {
            return await InstallCoreAsync(exePath, requestedProxy, mod, log);
        }
        catch when (isFreshInstall && File.Exists(Path.Combine(directory, ManifestFileName)))
        {
            // Undo whatever part of a new install was already applied, then report the original error.
            try
            {
                Uninstall(directory);
            }
            catch (Exception rollbackError)
            {
                Logger.Error(rollbackError, "Could not roll back failed MFG unlock install");
            }
            throw;
        }
    }

    async Task<List<string>> InstallCoreAsync(string exePath, string? requestedProxy, DownloadedPackage mod, List<string> log)
    {
        var directory = Path.GetDirectoryName(exePath)!;
        var manifestPath = Path.Combine(directory, ManifestFileName);
        InstallManifest manifest;
        if (File.Exists(manifestPath) && JsonNode.Parse(File.ReadAllText(manifestPath)) is JsonObject existing)
        {
            manifest = InstallManifest.FromJson(existing);
            log.Add($"Updating existing install ({manifest.ModTag}).");
        }
        else
        {
            manifest = new InstallManifest();
        }

        manifest.ExecutablePath = exePath;
        manifest.ModTag = mod.Tag;
        manifest.InstalledAtUtc = DateTime.UtcNow;

        var backupDirectory = Path.Combine(directory, BackupFolderName);

        void BackUp(string fileName, bool move)
        {
            var source = Path.Combine(directory, fileName);
            if (File.Exists(source) == false || manifest.BackedUpFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }
            Directory.CreateDirectory(backupDirectory);
            var destination = Path.Combine(backupDirectory, fileName);
            if (move)
            {
                File.Move(source, destination, true);
            }
            else
            {
                File.Copy(source, destination, true);
            }
            manifest.BackedUpFiles.Add(fileName);
            log.Add($"Backed up {fileName}.");
        }

        void MarkInstalled(string fileName)
        {
            if (manifest.InstalledFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase) == false)
            {
                manifest.InstalledFiles.Add(fileName);
            }
        }

        // Write the manifest first so a failure part way through can still be undone.
        WriteManifest(directory, manifest);

        // 1. Legacy builds must not be loaded alongside the new one.
        foreach (var legacy in LegacyModFiles)
        {
            if (File.Exists(Path.Combine(directory, legacy)))
            {
                BackUp(legacy, move: true);
                log.Add($"Moved legacy {legacy} out of the game folder.");
            }
        }

        // 2. Mod files.
        foreach (var fileName in ModFiles)
        {
            if (manifest.InstalledFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase) == false)
            {
                BackUp(fileName, move: false);
            }
            File.Copy(Path.Combine(mod.Directory, fileName), Path.Combine(directory, fileName), true);
            MarkInstalled(fileName);
        }
        log.Add($"Copied {string.Join(", ", ModFiles)} ({mod.Tag}).");
        WriteManifest(directory, manifest);

        // 3. Ultimate ASI Loader.
        string proxy;
        if (string.IsNullOrEmpty(manifest.LoaderProxy) == false && File.Exists(Path.Combine(directory, manifest.LoaderProxy)))
        {
            proxy = manifest.LoaderProxy;
            log.Add($"Keeping Ultimate ASI Loader as {proxy}.");
        }
        else
        {
            var existingLoader = KnownLoaderProxyNames.FirstOrDefault(n => IsAsiLoader(Path.Combine(directory, n)));
            if (existingLoader is not null && (requestedProxy is null || string.Equals(requestedProxy, existingLoader, StringComparison.OrdinalIgnoreCase)))
            {
                proxy = existingLoader;
                manifest.LoaderInstalledByUs = false;
                log.Add($"Found an existing Ultimate ASI Loader ({proxy}); it will be reused.");
            }
            else
            {
                if (requestedProxy is not null)
                {
                    var requestedPath = Path.Combine(directory, requestedProxy);
                    if (File.Exists(requestedPath) && IsAsiLoader(requestedPath) == false)
                    {
                        throw new Exception($"{requestedProxy} already exists in the game folder and is not Ultimate ASI Loader (it may belong to ReShade or another mod). Pick a different loader name.");
                    }
                    proxy = requestedProxy;
                }
                else
                {
                    proxy = ProxyCandidates.FirstOrDefault(n => File.Exists(Path.Combine(directory, n)) == false)
                        ?? throw new Exception($"{string.Join(", ", ProxyCandidates)} all already exist in the game folder. Pick a loader name manually.");
                }

                var loader = GetCachedAsiLoader();
                if (loader is null)
                {
                    log.Add("Downloading Ultimate ASI Loader...");
                    loader = await DownloadLatestAsiLoaderAsync();
                }

                File.Copy(Path.Combine(loader.Directory, "dinput8.dll"), Path.Combine(directory, proxy), true);
                manifest.LoaderInstalledByUs = true;
                MarkInstalled(proxy);
                log.Add($"Installed Ultimate ASI Loader {loader.Tag} as {proxy}.");
            }
            manifest.LoaderProxy = proxy;
        }
        WriteManifest(directory, manifest);

        // 4. Loader ini.
        var iniFileName = Path.ChangeExtension(proxy, ".ini");
        var iniPath = Path.Combine(directory, iniFileName);
        if (string.Equals(manifest.IniFileName, iniFileName, StringComparison.OrdinalIgnoreCase) == false)
        {
            manifest.IniFileName = iniFileName;
            if (File.Exists(iniPath))
            {
                BackUp(iniFileName, move: false);
                manifest.IniCreatedByUs = false;
            }
            else
            {
                manifest.IniCreatedByUs = true;
            }
        }

        // Other plugins in the game root would stop loading with LoadFromScriptsOnly=1, so only set it when there are none.
        var otherRootPlugins = Directory.GetFiles(directory, "*.asi")
            .Select(Path.GetFileName)
            .Where(n => string.Equals(n, AsiFileName, StringComparison.OrdinalIgnoreCase) == false)
            .ToList();
        MergeIni(iniPath, keepRootPlugins: otherRootPlugins.Count > 0);
        if (otherRootPlugins.Count > 0)
        {
            log.Add($"Other plugins found beside the game ({string.Join(", ", otherRootPlugins)}); LoadFromScriptsOnly was left unchanged so they keep loading.");
        }
        log.Add($"Configured {iniFileName}.");
        WriteManifest(directory, manifest);

        // 5. ReShade information.
        var reshade = new[] { "dxgi.dll", "d3d12.dll", "d3d11.dll", "opengl32.dll" }.FirstOrDefault(n => IsReShade(Path.Combine(directory, n)));
        if (reshade is null)
        {
            log.Add("ReShade was not found. The unlock still works; the multiplier is set from DLSS Swapper instead of an in-game menu.");
        }
        else
        {
            log.Add($"ReShade found ({reshade}); the DLSS MFG tab will appear in its menu.");
        }

        return log;
    }

    static void MergeIni(string iniPath, bool keepRootPlugins)
    {
        var lines = File.Exists(iniPath) ? File.ReadAllLines(iniPath).ToList() : new List<string>();

        var sectionStart = lines.FindIndex(l => l.Trim().Equals("[GlobalSets]", StringComparison.OrdinalIgnoreCase));
        if (sectionStart < 0)
        {
            if (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]) == false)
            {
                lines.Add(string.Empty);
            }
            lines.Add("[GlobalSets]");
            sectionStart = lines.Count - 1;
        }

        var sectionEnd = lines.FindIndex(sectionStart + 1, l => l.TrimStart().StartsWith('['));
        if (sectionEnd < 0)
        {
            sectionEnd = lines.Count;
        }

        foreach (var (key, requiredValue) in RequiredIniValues)
        {
            if (keepRootPlugins && key.Equals("LoadFromScriptsOnly", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var index = -1;
            for (var i = sectionStart + 1; i < sectionEnd; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith(';') || trimmed.StartsWith('#'))
                {
                    continue;
                }
                var equals = trimmed.IndexOf('=');
                if (equals > 0 && trimmed[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }

            var value = requiredValue;
            if (key.Equals("LoadExtraPlugins", StringComparison.OrdinalIgnoreCase) && index >= 0)
            {
                // Keep any extra plugins another mod already relies on; entries are separated by '|'.
                var current = lines[index][(lines[index].IndexOf('=') + 1)..].Split(';')[0].Trim();
                var entries = current.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (entries.Contains(AsiFileName, StringComparer.OrdinalIgnoreCase) == false)
                {
                    entries.Insert(0, AsiFileName);
                }
                value = string.Join("|", entries);
            }

            var newLine = $"{key}={value}";
            if (index >= 0)
            {
                lines[index] = newLine;
            }
            else
            {
                // Insert after the section's last setting, before any blank lines that separate it from the next section.
                var insertAt = sectionEnd;
                while (insertAt > sectionStart + 1 && string.IsNullOrWhiteSpace(lines[insertAt - 1]))
                {
                    insertAt--;
                }
                lines.Insert(insertAt, newLine);
                sectionEnd++;
            }
        }

        File.WriteAllLines(iniPath, lines);
    }

    /// <summary>
    /// Removes everything DLSS Swapper installed in <paramref name="directory"/> and restores backed up files.
    /// </summary>
    public List<string> Uninstall(string directory)
    {
        var log = new List<string>();
        var manifestPath = Path.Combine(directory, ManifestFileName);
        if (File.Exists(manifestPath) == false || JsonNode.Parse(File.ReadAllText(manifestPath)) is not JsonObject json)
        {
            throw new Exception("No MFG unlock install made by DLSS Swapper was found in this folder.");
        }

        var manifest = InstallManifest.FromJson(json);
        if (string.IsNullOrEmpty(manifest.ExecutablePath) == false && IsExecutableRunning(manifest.ExecutablePath))
        {
            throw new Exception("Close the game before removing the MFG unlock.");
        }

        foreach (var fileName in manifest.InstalledFiles)
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path))
            {
                File.Delete(path);
                log.Add($"Removed {fileName}.");
            }
        }

        if (manifest.IniCreatedByUs && string.IsNullOrEmpty(manifest.IniFileName) == false)
        {
            var iniPath = Path.Combine(directory, manifest.IniFileName);
            if (File.Exists(iniPath))
            {
                File.Delete(iniPath);
                log.Add($"Removed {manifest.IniFileName}.");
            }
        }

        var backupDirectory = Path.Combine(directory, BackupFolderName);
        foreach (var fileName in manifest.BackedUpFiles)
        {
            var backup = Path.Combine(backupDirectory, fileName);
            if (File.Exists(backup))
            {
                File.Move(backup, Path.Combine(directory, fileName), true);
                log.Add($"Restored {fileName}.");
            }
        }

        if (Directory.Exists(backupDirectory) && Directory.EnumerateFileSystemEntries(backupDirectory).Any() == false)
        {
            Directory.Delete(backupDirectory);
        }

        var statusPath = Path.Combine(directory, StatusFileName);
        if (File.Exists(statusPath))
        {
            File.Delete(statusPath);
        }

        File.Delete(manifestPath);
        log.Add($"Kept {ControlFileName} so your multiplier choice is remembered if you reinstall.");
        return log;
    }

    #endregion

    #region Multiplier control

    public enum ControlMode
    {
        FollowGame,
        Fixed,
        Dynamic,
    }

    public record ControlSettings(ControlMode Mode, int Multiplier, int DynamicTargetFrameRate);

    /// <summary>
    /// Where the core reads its live control file from. Cyberpunk 2077 with the CET mod
    /// uses the CET sandbox; every other game uses the executable folder.
    /// </summary>
    static string GetControlPath(string directory)
    {
        var cetDirectory = Path.Combine(directory, "plugins", "cyber_engine_tweaks", "mods", "RTX40MFG");
        if (File.Exists(Path.Combine(cetDirectory, "init.lua")))
        {
            return Path.Combine(cetDirectory, ControlFileName);
        }
        return Path.Combine(directory, ControlFileName);
    }

    public ControlSettings ReadControl(string directory)
    {
        var path = GetControlPath(directory);
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject json)
            {
                var followGame = json["followGame"]?.GetValue<bool>() ?? false;
                var mode = json["mode"]?.GetValue<string>();
                var multiplier = json["multiplier"]?.GetValue<int>() ?? 2;
                var target = json["dynamicTargetFrameRate"]?.GetValue<int>() ?? 0;
                var controlMode = followGame ? ControlMode.FollowGame : mode == "dynamic" ? ControlMode.Dynamic : ControlMode.Fixed;
                return new ControlSettings(controlMode, Math.Clamp(multiplier, 2, 6), Math.Clamp(target, 0, 1000));
            }
        }
        catch (Exception err)
        {
            Logger.Error(err, $"Could not read {path}");
        }
        return new ControlSettings(ControlMode.FollowGame, 2, 0);
    }

    /// <summary>
    /// Writes the control file in the same format the ReShade add-on writes. The core watches
    /// this file, so changes also apply to a running game.
    /// </summary>
    public void WriteControl(string directory, ControlSettings settings)
    {
        var followGame = settings.Mode == ControlMode.FollowGame;
        var json = new JsonObject
        {
            ["followGame"] = followGame,
            ["mode"] = settings.Mode switch
            {
                ControlMode.FollowGame => "follow",
                ControlMode.Dynamic => "dynamic",
                _ => "fixed",
            },
            ["multiplier"] = followGame ? 2 : Math.Clamp(settings.Multiplier, 2, 6),
            ["dynamicTargetFrameRate"] = Math.Clamp(settings.DynamicTargetFrameRate, 0, 1000),
            ["dynamicExperimental56"] = false,
            ["generatedOnlyDebug"] = false,
            ["intervalLogging"] = true,
            ["version"] = 11,
        };

        var path = GetControlPath(directory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json.ToJsonString() + "\n");
        File.Move(temporary, path, true);
    }

    /// <summary>
    /// Highest multiplier the core reported as safe for this game on its last run, if it has run.
    /// </summary>
    public int? ReadSafeMaximumMultiplier(string directory)
    {
        var path = Path.Combine(Path.GetDirectoryName(GetControlPath(directory))!, StatusFileName);
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject json)
            {
                return json["safeMaximumMultiplier"]?.GetValue<int>();
            }
        }
        catch
        {
            // The core rewrites this file while the game runs; ignore partial reads.
        }
        return null;
    }

    #endregion
}
