namespace Chickensoft.GodotEnv.Features.Godot.Domain;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Chickensoft.GodotEnv.Common.Clients;
using Chickensoft.GodotEnv.Common.Models;
using Chickensoft.GodotEnv.Common.Utilities;
using Chickensoft.GodotEnv.Features.Godot.Models;
using Downloader;
using Humanizer;
using Newtonsoft.Json;

public struct RemoteVersions {
  public List<string> Versions { get; set; }
}

public interface IGodotRepository {
  ConfigFile Config { get; }
  IFileClient FileClient { get; }
  INetworkClient NetworkClient { get; }
  IZipClient ZipClient { get; }
  ISystemEnvironmentVariableClient SystemEnvironmentVariableClient { get; }
  IGodotEnvironment Platform { get; }
  string GodotInstallationsPath { get; }
  string GodotCachePath { get; }
  string GodotSymlinkPath { get; }
  string GodotSymlinkTarget { get; }
  string GodotSharpSymlinkPath { get; }

  /// <summary>
  /// Clears the Godot installations cache and recreates the cache directory.
  /// </summary>
  void ClearCache();

  /// <summary>
  /// Gets the installation associated with the specified version of Godot.
  /// If both the .NET-enabled and the non-.NET-enabled versions of Godot with
  /// the same version are installed, this returns the .NET-enabled version.
  /// </summary>
  /// <param name="version">Godot version.</param>
  /// <param name="isDotnetVersion">True to search for an installed
  /// .NET-enabled version of Godot. False to search for an installed non-.NET
  /// version of Godot. Null to search for either.</param>
  /// <returns>Godot installation, or null if none found.</returns>
  GodotInstallation? GetInstallation(
    SemanticVersion version, bool? isDotnetVersion = null
  );

  /// <summary>
  /// Downloads the specified version of Godot.
  /// </summary>
  /// <param name="version">Godot version.</param>
  /// <param name="isDotnetVersion">True to download the .NET version.</param>
  /// <param name="log">Output log.</param>
  /// <param name="token">Cancellation token.</param>
  /// <returns>The fully resolved / absolute path of the Godot installation zip
  /// file for the Platform.</returns>
  Task<GodotCompressedArchive> DownloadGodot(
      SemanticVersion version,
      bool isDotnetVersion,
      ILog log,
      CancellationToken token
    );

  /// <summary>
  /// Extracts the Godot compressed archive files into the correct directory.
  /// </summary>
  /// <param name="archive">Godot installation archive.</param>
  /// <param name="log">Output log.</param>
  /// <returns>Path to the subfolder in the Godot installations directory
  /// containing the extracted contents.</returns>
  Task<GodotInstallation> ExtractGodotInstaller(
    GodotCompressedArchive archive, ILog log
  );

  /// <summary>
  /// Updates the symlink to point to the specified Godot installation.
  /// </summary>
  /// <param name="installation">Godot installation.</param>
  /// <param name="log">Output log.</param>
  Task UpdateGodotSymlink(GodotInstallation installation, ILog log);

  /// <summary>
  /// Adds (or updates) the GODOT system  environment variable to point to the
  /// symlink which points to the active version of Godot.
  /// </summary>
  /// <param name="log">Output log.</param>
  /// <returns>Completion task.</returns>
  Task AddOrUpdateGodotEnvVariable(ILog log);

  /// <summary>
  /// Gets the GODOT system environment variable.
  /// </summary>
  /// <returns>GODOT system environment variable value.</returns>
  string GetGodotEnvVariable();

  /// <summary>
  /// Get the list of installed Godot versions.
  /// </summary>
  /// <returns>List of semantic versions.</returns>
  List<GodotInstallation> GetInstallationsList();

  /// <summary>
  /// Get the list of available Godot versions.
  /// </summary>
  /// <returns></returns>
  Task<List<string>> GetRemoteVersionsList();

  /// <summary>
  /// Uninstalls the specified version of Godot.
  /// </summary>
  /// <param name="version">Godot version.</param>
  /// <param name="isDotnetVersion">True to uninstall the .NET version.</param>
  /// <param name="log">Output log.</param>
  /// <returns>True if successful, false if installation doesn't exist.
  /// </returns>
  Task<bool> Uninstall(
    SemanticVersion version, bool isDotnetVersion, ILog log
  );
}

public class GodotRepository : IGodotRepository {
  public ConfigFile Config { get; }
  public IFileClient FileClient { get; }
  public INetworkClient NetworkClient { get; }
  public IZipClient ZipClient { get; }
  public IGodotEnvironment Platform { get; }
  public ISystemEnvironmentVariableClient SystemEnvironmentVariableClient {
    get;
  }

  private const string GODOT_REMOTE_VERSIONS_URL = "https://api.nuget.org/v3-flatcontainer/godotsharp/index.json";

  public string GodotInstallationsPath => FileClient.Combine(
    FileClient.AppDataDirectory,
    Defaults.GODOT_PATH,
    Config.GodotInstallationsPath
  );

  public string GodotCachePath => FileClient.Combine(
    FileClient.AppDataDirectory, Defaults.GODOT_PATH, Defaults.GODOT_CACHE_PATH
  );

  public string GodotSymlinkPath => FileClient.Combine(
    FileClient.AppDataDirectory, Defaults.GODOT_PATH, Defaults.GODOT_BIN_PATH
  );

  public string GodotSharpSymlinkPath => FileClient.Combine(
    FileClient.AppDataDirectory, Defaults.GODOT_PATH, Defaults.GODOT_SHARP_PATH
  );

  public string GodotSymlinkTarget => FileClient.FileSymlinkTarget(
    GodotSymlinkPath
  );

  // Regex for converting directory names back into version strings to see
  // what versions we have installed.
  public static readonly Regex DirectoryToVersionStringRegex = new(
    @"godot_(dotnet_)?(?<major>\d+)_(?<minor>\d+)_(?<patch>\d+)_?(?<label>[a-zA-Z]+_[\d]+)?",
    RegexOptions.Compiled | RegexOptions.IgnoreCase
  );

  public GodotRepository(
    ConfigFile config,
    IFileClient fileClient,
    INetworkClient networkClient,
    IZipClient zipClient,
    IGodotEnvironment platform,
    ISystemEnvironmentVariableClient systemEnvironmentVariableClient
  ) {
    Config = config;
    FileClient = fileClient;
    NetworkClient = networkClient;
    ZipClient = zipClient;
    Platform = platform;
    SystemEnvironmentVariableClient = systemEnvironmentVariableClient;
  }

  public GodotInstallation? GetInstallation(
    SemanticVersion version, bool? isDotnetVersion = null
  ) {
    if (isDotnetVersion is bool isDotnet) {
      return ReadInstallation(version, isDotnet);
    }

    return ReadInstallation(version, isDotnetVersion: true) ??
      ReadInstallation(version, isDotnetVersion: false);
  }

  public void ClearCache() {
    if (FileClient.DirectoryExists(GodotCachePath)) {
      FileClient.DeleteDirectory(GodotCachePath);
    }
    FileClient.CreateDirectory(GodotCachePath);
  }

  public async Task<GodotCompressedArchive> DownloadGodot(
    SemanticVersion version,
    bool isDotnetVersion,
    ILog log,
    CancellationToken token
  ) {
    log.Info("⬇ Downloading Godot...");

    var downloadUrl = Platform.GetDownloadUrl(
      version, isDotnetVersion, isTemplate: false
    );

    log.Info($"🌏 Godot download url: {downloadUrl}");

    var fsName = GetVersionFsName(version, isDotnetVersion);
    // Tux server packages use .zip for everything.
    var cacheDir = FileClient.Combine(GodotCachePath, fsName);
    var cacheFilename = fsName + ".zip";
    var didFinishDownloadFilePath = FileClient.Combine(
      cacheDir, Defaults.DID_FINISH_DOWNLOAD_FILE_NAME
    );

    var compressedArchivePath = FileClient.Combine(cacheDir, cacheFilename);

    var didFinishAnyPreviousDownload = File.Exists(didFinishDownloadFilePath);
    var downloadedFileExists = File.Exists(compressedArchivePath);

    var archive = new GodotCompressedArchive(
      Name: fsName,
      Filename: cacheFilename,
      Version: version,
      IsDotnetVersion: isDotnetVersion,
      Path: cacheDir
    );

    if (downloadedFileExists && didFinishAnyPreviousDownload) {
      log.Info("📦 Existing compressed Godot installation archive found.");
      log.Print($"  {compressedArchivePath}");
      log.Print("");
      log.Success("✅ Using previous download instead.");
      log.Print("");
      log.Print("If you want to force a download to occur,");
      log.Print("use the following command to clear the downloads cache.");
      log.Print("");
      log.Info("  godotenv godot cache clear");
      log.Print("");
      return archive;
    }

    log.Info("🧼 Cleaning up...");
    if (didFinishAnyPreviousDownload) {
      log.Print($"🗑 Deleting {didFinishDownloadFilePath}");
      await FileClient.DeleteFile(didFinishDownloadFilePath);
    }

    if (downloadedFileExists) {
      log.Print($"🗑 Deleting {compressedArchivePath}");
      await FileClient.DeleteFile(compressedArchivePath);
    }
    log.Info("✨ All clean!");

    FileClient.CreateDirectory(cacheDir);

    log.Info($"🗄 Cache path: {cacheDir}");
    log.Info($"📄 Cache filename: {cacheFilename}");
    log.Info($"💾 Compressed installer path: {compressedArchivePath}");

    var lastPercent = 0d;
    var threshold = 1d;

    log.PrintInPlace("🚀 Downloading Godot: 0%");

    try {
      await NetworkClient.DownloadFileAsync(
        url: downloadUrl,
        destinationDirectory: cacheDir,
        filename: cacheFilename,
        new BasicProgress<DownloadProgressChangedEventArgs>((args) => {
          var speed = args.BytesPerSecondSpeed;
          var humanizedSpeed = speed.Bytes().Per(1.Seconds()).Humanize("#.##");
          var percent = args.ProgressPercentage;
          var p = Math.Round(percent);
          if (p - lastPercent >= threshold) {
            log.PrintInPlace(
              $"🚀 Downloading Godot: {p}% at {humanizedSpeed}" +
              "      "
            );
            lastPercent = p;
          }
        }),
        token: token
      );
      log.Print("🚀 Downloaded Godot: 100%");
    }
    catch (Exception) {
      log.ClearLastLine();
      log.Err("🛑 Aborting Godot installation.");
      throw;
    }

    FileClient.CreateFile(didFinishDownloadFilePath, "done");

    log.Print("");
    log.Success("✅ Godot successfully downloaded.");

    return archive;
  }

  public async Task<GodotInstallation> ExtractGodotInstaller(
    GodotCompressedArchive archive,
    ILog log
  ) {
    var archivePath = FileClient.Combine(archive.Path, archive.Filename);
    var destinationDirName =
      FileClient.Combine(GodotInstallationsPath, archive.Name);
    var lastPercent = 0d;

    await ZipClient.ExtractToDirectory(
      archivePath,
      destinationDirName,
      new BasicProgress<double>((percent) => {
        var p = Math.Round(percent * 100);
        log.PrintInPlace($"🗜  Extracting Godot installation files: {p}%");
        lastPercent = p;
      }),
      log
    );

    log.Print("🚀 Extracting Godot installation files: 100%");
    log.Success("🗜 Successfully extracted Godot to:");
    log.Info($"  {destinationDirName}");
    log.Print("");

    var execPath = GetExecutionPath(
      installationPath: destinationDirName,
      version: archive.Version,
      isDotnetVersion: archive.IsDotnetVersion
    );

    return new GodotInstallation(
      Name: archive.Name,
      IsActiveVersion: true, // we always switch to the newly installed version.
      Version: archive.Version,
      IsDotnetVersion: archive.IsDotnetVersion,
      Path: destinationDirName,
      ExecutionPath: execPath
    );
  }

  public async Task UpdateGodotSymlink(
    GodotInstallation installation, ILog log
  ) {
    // Create or update the symlink to the new version of Godot.
    await FileClient.CreateSymlink(GodotSymlinkPath, installation.ExecutionPath);
    await FileClient.CreateShortcuts(installation.Path);

    if (installation.IsDotnetVersion) {
      // Update GodotSharp symlinks
      var godotSharpPath = GetGodotSharpPath(
        installation.Path, installation.Version, installation.IsDotnetVersion
      );

      log.Print("");
      log.Print(
        $"🔗 Linking GodotSharp {GodotSharpSymlinkPath} -> " +
        $"{godotSharpPath}"
      );

      await FileClient.CreateSymlink(
        GodotSharpSymlinkPath, godotSharpPath
      );
    }

    if (!FileClient.FileExists(installation.ExecutionPath)) {
      log.Err("🛑 Execution path does not seem to be correct. Am I okay?");
      log.Err("Please help fix me by opening an issue or pull request on Github!");
    }

    log.Print("✅ Godot symlink updated.");
    log.Print("");
    log.Info($"{GodotSymlinkPath} -> {installation.ExecutionPath}");
    log.Print("");
    log.Info("Godot symlink path:");
    log.Print("");
    log.Print(GodotSymlinkPath);
  }

  public async Task AddOrUpdateGodotEnvVariable(ILog log) {
    var symlinkPath = GodotSymlinkPath;
    var godotVar = Defaults.GODOT_ENV_VAR_NAME;

    log.Print("");
    log.Info($"📝 Adding or updating the {godotVar} environment variable.");
    log.Print("");

    await SystemEnvironmentVariableClient.SetEnv(godotVar, symlinkPath);

    log.Success("Successfully updated the GODOT environment variable.");

    log.Print("");
    if (Platform is MacOS) {
      log.Warn("You may need to restart your shell or run the following ");
      log.Warn("to get the updated environment variable value.");
      log.Print("");
      log.Info("    source ~/.zshrc");
    }
    else if (Platform is Linux) {
      log.Warn("You may need to restart your shell or run the following ");
      log.Warn("to get the updated environment variable value.");
      log.Print("");
      log.Info("    source ~/.bashrc");
    }
    log.Print("");
  }

  public string GetGodotEnvVariable() =>
    SystemEnvironmentVariableClient.GetEnv(Defaults.GODOT_ENV_VAR_NAME);

  public List<GodotInstallation> GetInstallationsList() {
    var installations = new List<GodotInstallation>();

    if (!FileClient.DirectoryExists(GodotInstallationsPath)) {
      return installations;
    }

    foreach (var dir in FileClient.GetSubdirectories(GodotInstallationsPath)) {
      var name = dir.Name;

      var versionParts = DirectoryToVersionStringRegex.Match(name);
      var versionString = $"{versionParts.Groups["major"].Value}." +
        $"{versionParts.Groups["minor"].Value}." +
        $"{versionParts.Groups["patch"].Value}";

      var isDotnetVersion = dir.Name.Contains("dotnet");

      var label = versionParts.Groups.ContainsKey("label") ?
        versionParts.Groups["label"].Value : "";
      if (!string.IsNullOrWhiteSpace(label)) {
        versionString += $"-{label.Replace("_", ".")}";
      }
      var version = SemanticVersion.Parse(versionString);

      var installation = GetInstallation(version, isDotnetVersion)!;

      installations.Add(installation);
    }

    return installations.OrderBy(i => i.VersionName).ToList();
  }

  public async Task<List<string>> GetRemoteVersionsList() {
    var response = await NetworkClient.WebRequestGetAsync(GODOT_REMOTE_VERSIONS_URL);
    response.EnsureSuccessStatusCode();

    var responseBody = await response.Content.ReadAsStringAsync();
    var deserializedBody = JsonConvert.DeserializeObject<RemoteVersions>(responseBody);

    return deserializedBody.Versions;
  }

  public async Task<bool> Uninstall(
    SemanticVersion version, bool isDotnetVersion, ILog log
  ) {
    var potentialInstallation = GetInstallation(version, isDotnetVersion);

    if (potentialInstallation is not GodotInstallation installation) {
      return false;
    }

    await FileClient.DeleteDirectory(installation.Path);

    if (installation.IsActiveVersion) {
      // Remove symlink if we're deleting the active version.
      await FileClient.DeleteFile(GodotSymlinkPath);
      log.Print("");
      log.Warn("Removed the active version of Godot — your GODOT environment");
      log.Warn("may still be pointing to a non-existent symlink.");
      log.Print("");
      log.Warn("Please consider switching to a different version to");
      log.Warn("reconstruct the proper symlinks.");
      log.Print("");
      log.Warn("    godotenv godot use <version>");
      log.Print("");
    }

    return true;
  }

  private string GetExecutionPath(
    string installationPath, SemanticVersion version, bool isDotnetVersion
  ) =>
  FileClient.Combine(
    installationPath,
    Platform.GetRelativeExtractedExecutablePath(version, isDotnetVersion)
  );

  private string GetGodotSharpPath(
    string installationPath, SemanticVersion version, bool isDotnetVersion
  ) => FileClient.Combine(
    installationPath,
    Platform.GetRelativeGodotSharpPath(version, isDotnetVersion)
  );

  private GodotInstallation? ReadInstallation(
    SemanticVersion version, bool isDotnetVersion
  ) {
    var directoryName = GetVersionFsName(version, isDotnetVersion);
    var symlinkTarget = GodotSymlinkTarget;
    var installationDir = FileClient.Combine(
      GodotInstallationsPath, directoryName
    );

    if (!FileClient.DirectoryExists(installationDir)) { return null; }

    var executionPath = GetExecutionPath(
      installationPath: installationDir,
      version: version,
      isDotnetVersion: isDotnetVersion
    );

    return new GodotInstallation(
      Name: directoryName,
      IsActiveVersion: symlinkTarget == executionPath,
      Version: version,
      IsDotnetVersion: isDotnetVersion,
      Path: installationDir,
      ExecutionPath: executionPath
    );
  }

  private string LabelSanitized(SemanticVersion version) =>
    FileClient.Sanitize(version.Label).Replace(".", "_");

  private string GetVersionFsName(
    SemanticVersion version, bool isDotnetVersion
  ) =>
    ($"godot_{(isDotnetVersion ? "dotnet_" : "")}" +
    $"{version.Major}_{version.Minor}_{version.Patch}_" +
    $"{LabelSanitized(version)}").Trim('_');
}
