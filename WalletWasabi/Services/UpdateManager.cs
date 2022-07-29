using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WalletWasabi.Logging;
using WalletWasabi.Microservices;
using WalletWasabi.Models;
using WalletWasabi.Tor.Http;

namespace WalletWasabi.Services;

public class UpdateManager : IDisposable
{
	private string InstallerName { get; set; } = "";
	private const byte MaxTries = 3;
	private const string ReleaseURL = "https://api.github.com/repos/zkSNACKs/WalletWasabi/releases/latest";

	public UpdateManager(string dataDir, bool downloadNewVersion, IHttpClient httpClient)
	{
		InstallerDir = Path.Combine(dataDir, "Installer");
		HttpClient = httpClient;
		DownloadNewVersion = downloadNewVersion;
	}

	private async void UpdateChecker_UpdateStatusChangedAsync(object? sender, UpdateStatus updateStatus)
	{
		var tries = 0;
		bool updateAvailable = !updateStatus.ClientUpToDate || !updateStatus.BackendCompatible;
		Version targetVersion = updateStatus.ClientVersion;
		if (!updateAvailable)
		{
			return;
		}
		if (DownloadNewVersion)
		{
			Logger.LogInfo($"Trying to download new version: {targetVersion}");
			do
			{
				tries++;
				try
				{
					bool isReadyToInstall = await GetInstallerAsync(targetVersion).ConfigureAwait(false);
					Logger.LogInfo($"Version {targetVersion} downloaded successfuly.");
					updateStatus.IsReadyToInstall = isReadyToInstall;
					break;
				}
				catch (Exception ex)
				{
					Logger.LogError($"Geting version {targetVersion} failed with error.", ex);
				}
			} while (tries < MaxTries);
		}

		UpdateAvailableToGet?.Invoke(this, updateStatus);
	}

	private void UpdateChecker_CleanupAfterUpdate(object? sender, EventArgs e)
	{
		try
		{
			var folder = new DirectoryInfo(InstallerDir);
			if (folder.Exists)
			{
				Directory.Delete(InstallerDir, true);
			}
		}
		catch (Exception exc)
		{
			Logger.LogError("Failed to delete installer directory.", exc);
		}
	}

	private bool CheckIfInstallerDownloaded(FileSystemInfo[] files, string version)
	{
		FileSystemInfo[] installers = files.Where(file => file.Name.Contains("Wasabi")).ToArray();
		foreach (var file in installers)
		{
			if (file.Name.Contains(version))
			{
				InstallerName = file.Name;
				return true;
			}
		}
		return false;
	}

	private async Task<bool> GetInstallerAsync(Version targetVersion)
	{
		DirectoryInfo folder = new(InstallerDir);
		if (folder.Exists)
		{
			FileSystemInfo[] files = folder.GetFileSystemInfos();

			if (CheckIfInstallerDownloaded(files, targetVersion.ToString()))
			{
				if (InstallerName.Contains("tmp"))
				{
					Logger.LogWarning("Corrupted/unfinished installer found, deleting.");
					File.Delete(Path.Combine(InstallerDir, InstallerName));
				}
				else
				{
					return true;
				}
			}
		}
		else
		{
			Directory.CreateDirectory(InstallerDir);
		}

		using HttpRequestMessage message = new(HttpMethod.Get, ReleaseURL);
		message.Headers.UserAgent.Add(new("WalletWasabi", "2.0"));
		var response = await HttpClient.SendAsync(message).ConfigureAwait(false);

		JObject jsonResponse = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

		string softwareVersion = jsonResponse["tag_name"]?.ToString() ?? throw new InvalidDataException("Endpoint gave back wrong json data or it's changed.");

		// If the version we are looking for is not the one on github, somethings wrong.
		Version githubVersion = new(softwareVersion[1..]);
		Version shortGithubVersion = new(githubVersion.Major, githubVersion.Minor, githubVersion.Build);
		if (targetVersion != shortGithubVersion)
		{
			throw new InvalidDataException("Target version from backend does not match with the latest github release. This should be impossible.");
		}

		// Get all asset names and download urls to find the correct one.
		List<JToken> assetsInfos = jsonResponse["assets"]?.Children().ToList() ?? throw new InvalidDataException("Missing assets from response.");
		List<string> assetDownloadUrls = new();
		foreach (JToken asset in assetsInfos)
		{
			assetDownloadUrls.Add(asset["browser_download_url"]?.ToString() ?? throw new InvalidDataException("Missing download url from response."));
		}

		(string url, string fileName) = GetAssetToDownload(assetDownloadUrls);

		var tmpFileName = Path.Combine(InstallerDir, $"{fileName}.tmp");
		var newFileName = Path.Combine(InstallerDir, fileName);

		// This should also be done using Tor.
		// TODO: https://github.com/zkSNACKs/WalletWasabi/issues/8800
		using HttpClient httpClient = new();

		// Get file stream and copy it to downloads folder to access.
		using var stream = await httpClient.GetStreamAsync(url).ConfigureAwait(false);
		Logger.LogInfo("Installer stream downloaded, copying...");
		using var file = File.OpenWrite(tmpFileName);
		await stream.CopyToAsync(file).ConfigureAwait(false);

		// Closing the file to rename.
		file.Close();
		File.Move(tmpFileName, newFileName);

		InstallerName = fileName;

		return true;
	}

	private (string url, string fileName) GetAssetToDownload(List<string> urls)
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			var url = urls.Where(url => url.Contains(".msi")).First();
			return (url, url.Split("/").Last());
		}
		else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
		{
			var cpu = RuntimeInformation.ProcessArchitecture;
			if (cpu.ToString() == "Arm64")
			{
				var arm64url = urls.Where(url => url.Contains("arm64.dmg")).First();
				return (arm64url, arm64url.Split("/").Last());
			}
			var url = urls.Where(url => url.Contains(".dmg") && !url.Contains("arm64")).First();
			return (url, url.Split("/").Last());
		}
		else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			throw new InvalidOperationException("For Linux, get the correct update manually.");
		}
		else
		{
			throw new InvalidOperationException("OS not recognized, download manually.");
		}
	}

	public event EventHandler<UpdateStatus>? UpdateAvailableToGet;

	public string InstallerDir { get; }
	public IHttpClient HttpClient { get; }

	///<summary> Comes from config file. Decides Wasabi should download the new installer in the background or not.</summary>
	public bool DownloadNewVersion { get; }

	///<summary> Install new version on shutdown or not.</summary>
	public bool DoUpdateOnClose { get; set; }

	private UpdateChecker? UpdateChecker { get; set; }

	public void StartInstallingNewVersion()
	{
		try
		{
			var installerPath = Path.Combine(InstallerDir, InstallerName);
			ProcessStartInfo startInfo;
			if (!File.Exists(installerPath))
			{
				throw new FileNotFoundException(installerPath);
			}
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			{
				startInfo = ProcessStartInfoFactory.Make(installerPath, "", true);
			}
			else
			{
				startInfo = new()
				{
					FileName = installerPath,
					UseShellExecute = true,
					WindowStyle = ProcessWindowStyle.Normal
				};
			}

			using Process? p = Process.Start(startInfo);

			if (p is null)
			{
				throw new InvalidOperationException($"Can't start {nameof(p)} {startInfo.FileName}.");
			}
			if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
			{
				// For MacOS, you need to start the process twice, first start => permission denied
				// TODO: find out why and fix.

				p!.WaitForExit(5000);
				p.Start();
			}
		}
		catch (Exception ex)
		{
			Logger.LogError("Failed to install latest release. File might be corrupted.", ex);
		}
	}

	public void Initialize(UpdateChecker updateChecker)
	{
		UpdateChecker = updateChecker;
		updateChecker.UpdateStatusChanged += UpdateChecker_UpdateStatusChangedAsync;
		updateChecker.CleanupAfterUpdate += UpdateChecker_CleanupAfterUpdate;
	}

	public void Dispose()
	{
		if (UpdateChecker is { } updateChecker)
		{
			updateChecker.UpdateStatusChanged -= UpdateChecker_UpdateStatusChangedAsync;
			updateChecker.CleanupAfterUpdate -= UpdateChecker_CleanupAfterUpdate;
		}
	}
}
