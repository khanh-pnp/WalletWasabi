using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Helpers;
using WalletWasabi.Logging;
using WalletWasabi.Microservices;
using WalletWasabi.Models;
using WalletWasabi.Tor.Http;

namespace WalletWasabi.Services;

public class UpdateManager : IDisposable
{
	private string InstallerPath { get; set; } = "";
	private string Sha256SumsFilePath { get; set; } = "";
	private const byte MaxTries = 2;
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
			Cleanup();
			return;
		}

		if (DownloadNewVersion && !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			do
			{
				tries++;
				try
				{
					(string installerPath, Version newVersion) = await GetInstallerAsync(targetVersion).ConfigureAwait(false);
					InstallerPath = installerPath;
					Logger.LogInfo($"Version {newVersion} downloaded successfuly.");
					updateStatus.IsReadyToInstall = true;
					updateStatus.ClientVersion = newVersion;
					break;
				}
				catch (OperationCanceledException ex)
				{
					Logger.LogTrace($"Getting new update was canceled.", ex);
					break;
				}
				catch (InvalidOperationException ex)
				{
					Logger.LogError($"Getting new update failed with error.", ex);
					Cleanup();
					break;
				}
				catch (Exception ex)
				{
					Logger.LogError($"Getting new update failed with error.", ex);
				}
			} while (tries < MaxTries);
		}

		UpdateAvailableToGet?.Invoke(this, updateStatus);
	}

	/// <summary>
	/// Get or download installer for the newest release.
	/// </summary>
	/// <param name="targetVersion">This does not contains the revision number, because backend always sends zero.</param>
	private async Task<(string filePath, Version newVersion)> GetInstallerAsync(Version targetVersion)
	{
		(Version newVersion, string url, string installerFileName) = await GetLatestReleaseFromGithubAsync(targetVersion).ConfigureAwait(false);

		var installerFilePath = Path.Combine(InstallerDir, installerFileName);

		try
		{
			if (!File.Exists(installerFilePath))
			{
				EnsureToRemoveCorruptedFiles();

				// This should also be done using Tor.
				// TODO: https://github.com/zkSNACKs/WalletWasabi/issues/8800
				Logger.LogInfo($"Trying to download new version: {newVersion}");
				using HttpClient httpClient = new();

				// Get file stream and copy it to downloads folder to access.
				using var stream = await httpClient.GetStreamAsync(url, CancellationToken).ConfigureAwait(false);
				Logger.LogInfo("Installer downloaded, copying...");

				await CopyStreamContentToFileAsync(stream, installerFilePath).ConfigureAwait(false);
			}
			await VerifyInstallerHashAsync(installerFileName, installerFilePath, newVersion).ConfigureAwait(false);
		}
		catch (IOException)
		{
			CancellationToken.ThrowIfCancellationRequested();
			throw;
		}

		return (installerFilePath, newVersion);
	}

	private async Task VerifyInstallerHashAsync(string expectedFileName, string installerFilePath, Version version)
	{
		var bytes = await WasabiSignerHelpers.GetShaComputedBytesOfFileAsync(installerFilePath, CancellationToken).ConfigureAwait(false);
		string downloadedHash = Convert.ToHexString(bytes).ToLower();

		string expectedHash = await GetHashFromSha256SumsFileAsync(expectedFileName, version).ConfigureAwait(false);
		if (expectedHash != downloadedHash)
		{
			throw new InvalidOperationException("Downloaded file hash doesn't match expected hash.");
		}
	}

	private async Task<string> GetHashFromSha256SumsFileAsync(string installerFileName, Version newVersion)
	{
		string[] lines = await File.ReadAllLinesAsync(Sha256SumsFilePath).ConfigureAwait(false);
		var installerLines = lines.Where(line => line.Contains($"Wasabi-{newVersion}")).ToList();
		var correctLine = installerLines.FirstOrDefault(line => line.Contains(installerFileName));
		if (correctLine == null)
		{
			throw new InvalidOperationException($"{installerFileName} was not found.");
		}
		return correctLine.Split(" ")[0];
	}

	private async Task CopyStreamContentToFileAsync(Stream stream, string filePath)
	{
		if (File.Exists(filePath))
		{
			return;
		}
		var tmpFilePath = $"{filePath}.tmp";
		IoHelpers.EnsureContainingDirectoryExists(tmpFilePath);
		using (var file = File.OpenWrite(tmpFilePath))
		{
			await stream.CopyToAsync(file, CancellationToken).ConfigureAwait(false);

			// Closing the file to rename.
			file.Close();
		};
		File.Move(tmpFilePath, filePath);
	}

	private async Task<(Version Version, string DownloadUrl, string FileName)> GetLatestReleaseFromGithubAsync(Version targetVersion)
	{
		using HttpRequestMessage message = new(HttpMethod.Get, ReleaseURL);
		message.Headers.UserAgent.Add(new("WalletWasabi", "2.0"));
		var response = await HttpClient.SendAsync(message, CancellationToken).ConfigureAwait(false);

		JObject jsonResponse = JObject.Parse(await response.Content.ReadAsStringAsync(CancellationToken).ConfigureAwait(false));

		string softwareVersion = jsonResponse["tag_name"]?.ToString() ?? throw new InvalidDataException("Endpoint gave back wrong json data or it's changed.");

		// "tag_name" will have a 'v' at the beggining, needs to be removed.
		Version githubVersion = new(softwareVersion[1..]);
		Version shortGithubVersion = new(githubVersion.Major, githubVersion.Minor, githubVersion.Build);
		if (targetVersion != shortGithubVersion)
		{
			throw new InvalidDataException("Target version from backend does not match with the latest GitHub release. This should be impossible.");
		}

		// Get all asset names and download urls to find the correct one.
		List<JToken> assetsInfos = jsonResponse["assets"]?.Children().ToList() ?? throw new InvalidDataException("Missing assets from response.");
		List<string> assetDownloadUrls = new();
		foreach (JToken asset in assetsInfos)
		{
			assetDownloadUrls.Add(asset["browser_download_url"]?.ToString() ?? throw new InvalidDataException("Missing download url from response."));
		}

		bool isReleaseValid = await ValidateWasabiSignatureAsync(assetDownloadUrls).ConfigureAwait(false);
		if (!isReleaseValid)
		{
			throw new InvalidOperationException($"Downloading new release was aborted, Wasabi signature was invalid.");
		}
		(string url, string fileName) = GetAssetToDownload(assetDownloadUrls);

		return (githubVersion, url, fileName);
	}

	private async Task<bool> ValidateWasabiSignatureAsync(List<string> assetDownloadUrls)
	{
		var sha256SumsFilePath = Path.Combine(InstallerDir, "SHA256SUMS.asc");
		var wasabiSigFilePath = Path.Combine(InstallerDir, "SHA256SUMS.wasabisig");

		using HttpClient httpClient = new();
		string sha256SumsUrl = assetDownloadUrls.Where(url => url.Contains("SHA256SUMS.asc")).First();
		string wasabiSigUrl = assetDownloadUrls.Where(url => url.Contains("SHA256SUMS.wasabisig")).First();

		try
		{
			using (var stream = await httpClient.GetStreamAsync(sha256SumsUrl, CancellationToken).ConfigureAwait(false))
			{
				await CopyStreamContentToFileAsync(stream, sha256SumsFilePath).ConfigureAwait(false);
				Sha256SumsFilePath = sha256SumsFilePath;
			}

			using (var stream = await httpClient.GetStreamAsync(wasabiSigUrl, CancellationToken).ConfigureAwait(false))
			{
				await CopyStreamContentToFileAsync(stream, wasabiSigFilePath).ConfigureAwait(false);
			}

			await WasabiSignerHelpers.VerifySha256SumsFileAsync(sha256SumsFilePath).ConfigureAwait(false);
			return true;
		}
		catch (HttpRequestException exc)
		{
			string message = "";
			if (exc.StatusCode is HttpStatusCode.NotFound)
			{
				message = "Wasabi signature files were not found under the API.";
			}
			else
			{
				message = "Something went wrong while getting Wasabi signature files.";
			}
			throw new InvalidOperationException(message, exc);
		}
		catch (IOException)
		{
			// There's a chance to get IOException when closing Wasabi during stream copying. Throw OperationCancelledException instead.
			CancellationToken.ThrowIfCancellationRequested();
			throw;
		}
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

	private void EnsureToRemoveCorruptedFiles()
	{
		DirectoryInfo folder = new(InstallerDir);
		if (folder.Exists)
		{
			IEnumerable<FileSystemInfo> corruptedFiles = folder.GetFileSystemInfos().Where(file => file.Name.Contains("tmp"));
			foreach (var file in corruptedFiles)
			{
				File.Delete(file.FullName);
			}
		}
	}

	private void Cleanup()
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

	public event EventHandler<UpdateStatus>? UpdateAvailableToGet;

	public string InstallerDir { get; }
	public IHttpClient HttpClient { get; }

	///<summary> Comes from config file. Decides Wasabi should download the new installer in the background or not.</summary>
	public bool DownloadNewVersion { get; }

	///<summary> Install new version on shutdown or not.</summary>
	public bool DoUpdateOnClose { get; set; }

	private UpdateChecker? UpdateChecker { get; set; }
	private CancellationToken CancellationToken { get; set; }

	public void StartInstallingNewVersion()
	{
		try
		{
			ProcessStartInfo startInfo;
			if (!File.Exists(InstallerPath))
			{
				throw new FileNotFoundException(InstallerPath);
			}
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			{
				startInfo = ProcessStartInfoFactory.Make(InstallerPath, "", true);
			}
			else
			{
				startInfo = new()
				{
					FileName = InstallerPath,
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

	public void Initialize(UpdateChecker updateChecker, CancellationToken cancelationToken)
	{
		UpdateChecker = updateChecker;
		CancellationToken = cancelationToken;
		updateChecker.UpdateStatusChanged += UpdateChecker_UpdateStatusChangedAsync;
	}

	public void Dispose()
	{
		if (UpdateChecker is { } updateChecker)
		{
			updateChecker.UpdateStatusChanged -= UpdateChecker_UpdateStatusChangedAsync;
		}
	}
}
