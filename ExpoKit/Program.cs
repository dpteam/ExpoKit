#define TRACE
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ExpoKit;

public class Program
{
	public abstract class DumperBase
	{
		protected string BaseUrl;
		protected string OutputDir;
		protected string ProxyUrl;
		protected string UserAgent;
		protected int Timeout = 5;
		protected Dictionary<string, string> Headers;
		protected DumperBase(string url, string outDir, string ua, string proxy, Dictionary<string, string> headers = null)
		{
			BaseUrl = url.TrimEnd('/');
			OutputDir = outDir;
			UserAgent = ua;
			ProxyUrl = proxy;
			Headers = headers ?? new Dictionary<string, string>();
		}
		public abstract void Start();
		protected WebClient CreateWebClient()
		{
			TimeoutWebClient client = new TimeoutWebClient(Timeout * 1000);
			if (!string.IsNullOrEmpty(ProxyUrl))
			{
				client.Proxy = new WebProxy(ProxyUrl);
			}
			else
			{
				client.Proxy = WebRequest.GetSystemWebProxy();
			}
			client.Headers["User-Agent"] = UserAgent;
			foreach (KeyValuePair<string, string> h in Headers)
			{
				client.Headers[h.Key] = h.Value;
			}
			return client;
		}
		protected HttpWebRequest CreateRequest(string url)
		{
			HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
			req.UserAgent = UserAgent;
			req.Timeout = Timeout * 1000;
			req.ReadWriteTimeout = Timeout * 1000;
			Verbose("Creating Request: " + url);
			Verbose("   UA: " + UserAgent + " | Proxy: " + (ProxyUrl ?? "Default"));
			if (!string.IsNullOrEmpty(ProxyUrl))
			{
				req.Proxy = new WebProxy(ProxyUrl);
			}
			else
			{
				req.Proxy = WebRequest.GetSystemWebProxy();
			}
			foreach (KeyValuePair<string, string> h in Headers)
			{
				req.Headers.Add(h.Key, h.Value);
				Verbose("   Header: " + h.Key + "=" + h.Value);
			}
			return req;
		}
		protected void SafeCreateDir(string path)
		{
			if (!Directory.Exists(path))
			{
				Verbose("Creating directory: " + path);
				Directory.CreateDirectory(path);
			}
		}
		protected byte[] DownloadData(string url)
		{
			try
			{
				Verbose("DownloadData: " + url);
				using WebClient client = CreateWebClient();
				return client.DownloadData(url);
			}
			catch (Exception ex)
			{
				Verbose("DownloadData failed " + url + ": " + ex.Message);
				return null;
			}
		}
		protected string DownloadString(string url)
		{
			try
			{
				Verbose("DownloadString: " + url);
				using WebClient client = CreateWebClient();
				return client.DownloadString(url);
			}
			catch (Exception ex)
			{
				Verbose("DownloadString failed " + url + ": " + ex.Message);
				return null;
			}
		}
	}
	public class SvnDumper : DumperBase
	{
		public SvnDumper(string url, string outDir, int jobs, int retry, int timeout, string ua, string proxy, Dictionary<string, string> headers)
			: base(url, outDir, ua, proxy, headers)
		{
			Timeout = timeout;
		}
		public override void Start()
		{
			Log("Starting SVN Dump: " + BaseUrl, ConsoleColor.White);
			UpdateTitleStatus("SVN Dump", "Initializing...");
			SafeCreateDir(OutputDir);
			string entriesUrl = BaseUrl + "/entries";
			string wcDbUrl = BaseUrl + "/wc.db";
			byte[] wcData = DownloadData(wcDbUrl);
			if (wcData != null && wcData.Length != 0)
			{
				Log("Found SVN 1.7+ format (wc.db).", ConsoleColor.Green);
				ParseWcDb(wcData);
				return;
			}
			string entriesContent = DownloadString(entriesUrl);
			if (!string.IsNullOrEmpty(entriesContent))
			{
				Log("Found Legacy SVN format (entries).", ConsoleColor.Yellow);
				ParseEntries(entriesContent);
			}
			else
			{
				Log("Failed to fetch SVN metadata.", ConsoleColor.Red);
			}
		}
        private void ParseWcDb(byte[] dbData)
        {
            // Проверка на ложное срабатывание (HTML вместо базы данных)
            string sample = Encoding.ASCII.GetString(dbData, 0, Math.Min(dbData.Length, 512));
            if (sample.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0 || sample.IndexOf("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Log("wc.db contains HTML content. Invalid SVN repository (False Positive).", ConsoleColor.Red);
                return;
            }

            string dbPath = Path.Combine(OutputDir, "wc.db");
            File.WriteAllBytes(dbPath, dbData);
            Verbose("wc.db saved.");
            string raw = Encoding.ASCII.GetString(dbData);
            MatchCollection shaMatches = Regex.Matches(raw, "\\$sha1\\$([a-f0-9]{40})", RegexOptions.IgnoreCase);
            Log($"Potential SHA1 hashes found: {shaMatches.Count}", ConsoleColor.Cyan);
            int count = 0;
            foreach (Match m in shaMatches)
            {
                string sha = m.Groups[1].Value;
                string pathUrl = BaseUrl + "/pristine/" + sha.Substring(0, 2) + "/" + sha + ".svn-base";
                string localPath = Path.Combine(OutputDir, "pristine", sha.Substring(0, 2), sha + ".svn-base");
                SafeCreateDir(Path.GetDirectoryName(localPath));
                try
                {
                    SetStatus("Rev: " + sha.Substring(0, 7));
                    byte[] data = DownloadData(pathUrl);
                    if (data != null)
                    {
                        File.WriteAllBytes(localPath, data);
                        Log("[OK] Rev: " + sha.Substring(0, 7), ConsoleColor.Green);
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    Verbose("Failed to download rev " + sha + ": " + ex.Message);
                }
            }
            Log($"SVN Dump finished. Downloaded {count} files.", ConsoleColor.Green);
        }
        private void ParseEntries(string content)
		{
			Log("Legacy SVN dump logic not fully implemented.", ConsoleColor.Yellow);
		}
	}
	public class GitRecursiveDumper : DumperBase
	{
		private int _jobs;
		private int _retry;
		private ConcurrentDictionary<string, byte> _downloadedFiles = new ConcurrentDictionary<string, byte>();
		private ConcurrentQueue<string> _pendingTasks = new ConcurrentQueue<string>();
		private int _activeTasks = 0;
		private bool _isFinished = false;
		private int _totalObjects = 0;
		private int _downloadedObjects = 0;
		private object _statLock = new object();
		private object _lock = new object();
		private string _gitPathPrefix = ".git/";
		public GitRecursiveDumper(string url, string outDir, int jobs, int retry, int timeout, string ua, string proxy, Dictionary<string, string> headers)
			: base(url, outDir, ua, proxy, headers)
		{
			_jobs = jobs;
			_retry = retry;
			Timeout = timeout;
			if (BaseUrl.EndsWith("/.git"))
			{
				BaseUrl = BaseUrl.Substring(0, BaseUrl.Length - "/.git".Length);
			}
			else if (BaseUrl.EndsWith(".git"))
			{
				BaseUrl = BaseUrl.Substring(0, BaseUrl.Length - ".git".Length);
			}
			BaseUrl = BaseUrl.TrimEnd('/');
			Verbose("Normalized BaseUrl: " + BaseUrl);
		}
        public override void Start()
        {
            Log("Starting Git Dump: " + BaseUrl, ConsoleColor.White);
            UpdateTitleStatus("Git Dump", "Initializing...");
            SafeCreateDir(OutputDir);
            Verbose("Checking for directory listing...");
            if (CheckDirectoryListing())
            {
                Log("Directory listing detected. Switching to recursive download mode.", ConsoleColor.Green);
                RunRecursiveDownload();
                FinalizeGit();
                return;
            }
            Log("Directory listing not available. Using brute-force mode.", ConsoleColor.Yellow);
            Verbose("Downloading HEAD...");
            string headContent = FetchFileAndQueue("HEAD");
            if (string.IsNullOrEmpty(headContent))
            {
                Log("Failed to download HEAD. Aborting.", ConsoleColor.Red);
                return;
            }

            // Проверка на ложное срабатывание (HTML вместо Git данных)
            if (headContent.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0 || headContent.IndexOf("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Log("HEAD contains HTML content. Invalid Git repository (False Positive).", ConsoleColor.Red);
                return;
            }

            EnqueueTask(_gitPathPrefix + "config");
			EnqueueTask(_gitPathPrefix + "description");
			EnqueueTask(_gitPathPrefix + "COMMIT_EDITMSG");
			EnqueueTask(_gitPathPrefix + "index");
			FindRefs(headContent);
			Verbose("Checking packed-refs...");
			string packedRefs = FetchFileAndQueue("packed-refs");
			if (!string.IsNullOrEmpty(packedRefs))
			{
				FindRefs(packedRefs);
			}
			ProcessIndex();
			ProcessPacks();
			Verbose($"Starting {_jobs} worker threads...");
			List<Thread> threads = new List<Thread>();
			for (int i = 0; i < _jobs; i++)
			{
				Thread t = new Thread(WorkerLoop)
				{
					IsBackground = true
				};
				t.Start();
				threads.Add(t);
			}
			while (true)
			{
				Thread.Sleep(100);
				if (_skipConfirmed)
				{
					_isFinished = true;
					Verbose("Skip detected in main loop. Aborting dump.");
					throw new SkipException();
				}
				lock (_lock)
				{
					if (_activeTasks == 0 && _pendingTasks.IsEmpty)
					{
						break;
					}
				}
			}
			_isFinished = true;
			FinalizeGit();
		}
		private bool CheckDirectoryListing()
		{
			try
			{
				string url = BaseUrl + "/" + _gitPathPrefix;
				string html = DownloadString(url);
				if (!string.IsNullOrEmpty(html) && html.Contains("<a href=\"HEAD"))
				{
					return true;
				}
			}
			catch (Exception ex)
			{
				Verbose("Dir listing check failed: " + ex.Message);
			}
			return false;
		}
		private void RunRecursiveDownload()
		{
			HashSet<string> visited = new HashSet<string>();
			Queue<string> dirs = new Queue<string>();
			string startDir = _gitPathPrefix.TrimStart('/');
			if (!startDir.EndsWith("/"))
			{
				startDir += "/";
			}
			dirs.Enqueue(startDir);
			visited.Add(startDir);
			int dirCount = 0;
			while (dirs.Count > 0)
			{
				if (_skipConfirmed)
				{
					throw new SkipException();
				}
				string currentDir = dirs.Dequeue();
				dirCount++;
				if (dirCount % 5 == 0 || dirCount == 1)
				{
					SetStatus("Scanning dir: " + currentDir);
				}
				if (dirCount % 20 == 0)
				{
					Log($"Scanning directory #{dirCount}: {currentDir}", ConsoleColor.Cyan);
				}
				Verbose("Scanning directory: " + currentDir);
				string url = BaseUrl + "/" + currentDir;
				string html = DownloadString(url);
				if (html == null)
				{
					Verbose("Failed to list directory: " + url);
					continue;
				}
				foreach (Match m in Regex.Matches(html, "<a href=\"([^\"]+)\""))
				{
					string rawLink = m.Groups[1].Value;
					if (rawLink == "../" || rawLink == "./" || string.IsNullOrEmpty(rawLink))
					{
						continue;
					}
					Uri baseUri = new Uri(BaseUrl + "/" + currentDir);
					Uri fullUri = new Uri(baseUri, rawLink);
					string basePath = new Uri(BaseUrl + "/" + _gitPathPrefix).AbsolutePath;
					if (!fullUri.AbsolutePath.StartsWith(basePath))
					{
						Verbose("Skipping external link: " + fullUri.AbsolutePath);
						continue;
					}
					string decodedPath = Uri.UnescapeDataString(fullUri.AbsolutePath);
					if (decodedPath.StartsWith("/"))
					{
						decodedPath = decodedPath.Substring(1);
					}
					if (rawLink.EndsWith("/") || fullUri.AbsolutePath.EndsWith("/"))
					{
						if (!visited.Contains(decodedPath))
						{
							visited.Add(decodedPath);
							dirs.Enqueue(decodedPath);
						}
					}
					else
					{
						EnqueueTask(decodedPath);
					}
				}
			}
			Log($"Directory scan finished. Found {_pendingTasks.Count} files in {dirCount} directories.", ConsoleColor.Green);
			List<Thread> threads = new List<Thread>();
			for (int i = 0; i < _jobs; i++)
			{
				Thread t = new Thread(WorkerLoop)
				{
					IsBackground = true
				};
				t.Start();
				threads.Add(t);
			}
			while (true)
			{
				Thread.Sleep(500);
				lock (_lock)
				{
					if (_activeTasks == 0 && _pendingTasks.IsEmpty)
					{
						break;
					}
				}
			}
			_isFinished = true;
		}
		private void FinalizeGit()
		{
			UpdateTitleStatus("Git Dump", "Finalizing...");
			string gitDir = Path.Combine(OutputDir, ".git");
			string configFile = Path.Combine(gitDir, "config");
			if (File.Exists(configFile))
			{
				try
				{
					string content = File.ReadAllText(configFile);
					content = Regex.Replace(content, "^\\s*(fsmonitor|sshcommand|askpass|editor|pager)\\s*=", "# $1 = ", RegexOptions.IgnoreCase | RegexOptions.Multiline);
					File.WriteAllText(configFile, content);
					Log("Sanitized .git/config to prevent RCE.", ConsoleColor.Yellow);
				}
				catch (Exception ex)
				{
					Verbose("Failed to sanitize config: " + ex.Message);
				}
			}
			try
			{
				if (Directory.Exists(gitDir))
				{
					ProcessStartInfo psi = new ProcessStartInfo("git.exe", "checkout .")
					{
						WorkingDirectory = OutputDir,
						UseShellExecute = false,
						CreateNoWindow = true,
						RedirectStandardOutput = true,
						RedirectStandardError = true
					};
					Process proc = Process.Start(psi);
					if (proc != null)
					{
						if (proc.WaitForExit(30000))
						{
							Verbose($"Git checkout finished with code: {proc.ExitCode}");
							if (proc.ExitCode != 0)
							{
								Log("Git checkout finished with errors.", ConsoleColor.Yellow);
							}
							else
							{
								Log("Attempted 'git checkout .'", ConsoleColor.Cyan);
							}
						}
						else
						{
							Log("Git checkout timed out.", ConsoleColor.Red);
							proc.Kill();
						}
					}
				}
			}
			catch (Exception ex2)
			{
				Verbose("Git checkout failed: " + ex2.Message);
			}
			Log("Git Dump finished.", ConsoleColor.Green);
		}
		private string FetchFileAndQueue(string relativePath)
		{
			string fullPath = _gitPathPrefix + relativePath;
			string localPath = Path.Combine(OutputDir, fullPath.Replace("/", "\\"));
			SafeCreateDir(Path.GetDirectoryName(localPath));
			string content = DownloadString(BaseUrl + "/" + fullPath);
			if (content != null)
			{
				File.WriteAllText(localPath, content);
				return content;
			}
			return null;
		}
		private void FindRefs(string content)
		{
			Verbose("Finding refs in content...");
			foreach (Match m in Regex.Matches(content, "[a-f0-9]{40}"))
			{
				string sha = m.Value;
				EnqueueTask(_gitPathPrefix + "objects/" + sha.Substring(0, 2) + "/" + sha.Substring(2));
			}
			foreach (Match m2 in Regex.Matches(content, "refs/([a-zA-Z0-9\\-_./]+)"))
			{
				string refPath = m2.Value;
				EnqueueTask(_gitPathPrefix + refPath);
				EnqueueTask(_gitPathPrefix + "logs/" + refPath);
			}
		}
		private void ProcessIndex()
		{
			string indexPath = Path.Combine(OutputDir, ".git", "index");
			if (!File.Exists(indexPath))
			{
				return;
			}
			Verbose("Parsing index file...");
			try
			{
				byte[] data = File.ReadAllBytes(indexPath);
				string raw = Encoding.ASCII.GetString(data);
				foreach (Match m in Regex.Matches(raw, "[a-f0-9]{40}"))
				{
					string sha = m.Value;
					EnqueueTask(_gitPathPrefix + "objects/" + sha.Substring(0, 2) + "/" + sha.Substring(2));
				}
			}
			catch (Exception ex)
			{
				Verbose("Index parsing error: " + ex.Message);
			}
		}
		private void ProcessPacks()
		{
			Verbose("Checking for packs...");
			string packsList = DownloadString(BaseUrl + "/" + _gitPathPrefix + "objects/info/packs");
			if (packsList == null)
			{
				return;
			}
			foreach (Match m in Regex.Matches(packsList, "pack-([a-f0-9]{40})\\.pack"))
			{
				string sha = m.Groups[1].Value;
				string packPath = _gitPathPrefix + "objects/pack/pack-" + sha + ".pack";
				string idxPath = _gitPathPrefix + "objects/pack/pack-" + sha + ".idx";
				string localPack = Path.Combine(OutputDir, packPath.Replace("/", "\\"));
				string localIdx = Path.Combine(OutputDir, idxPath.Replace("/", "\\"));
				SafeCreateDir(Path.GetDirectoryName(localPack));
				byte[] packData = DownloadDataWithRetry(BaseUrl + "/" + packPath);
				if (packData != null)
				{
					File.WriteAllBytes(localPack, packData);
				}
				byte[] idxData = DownloadDataWithRetry(BaseUrl + "/" + idxPath);
				if (idxData != null)
				{
					File.WriteAllBytes(localIdx, idxData);
				}
			}
		}
		private void WorkerLoop()
		{
			if (_skipConfirmed)
			{
				return;
			}
			while (!_isFinished)
			{
				string task = null;
				lock (_lock)
				{
					if (!_pendingTasks.TryDequeue(out task))
					{
						Monitor.Wait(_lock, 100);
						continue;
					}
					_activeTasks++;
				}
				try
				{
					ProcessTask(task);
				}
				catch (Exception ex)
				{
					Verbose("[ERR] Task " + task + ": " + ex.Message);
				}
				finally
				{
					lock (_lock)
					{
						_activeTasks--;
						Monitor.PulseAll(_lock);
					}
				}
			}
		}
		private void ProcessTask(string relativePath)
		{
			if (_skipConfirmed)
			{
				return;
			}
			SetStatus("Downloading: " + Path.GetFileName(relativePath));
			string localPath = Path.Combine(OutputDir, relativePath.Replace("/", "\\"));
			if (File.Exists(localPath))
			{
				UpdateProgress();
				return;
			}
			byte[] data = DownloadDataWithRetry(BaseUrl + "/" + relativePath);
			if (data != null)
			{
				SafeCreateDir(Path.GetDirectoryName(localPath));
				File.WriteAllBytes(localPath, data);
				Log($"[OK] {relativePath} ({data.Length} bytes)", ConsoleColor.Green);
				if (relativePath.Contains("/objects/"))
				{
					byte[] decompressed = ZlibHelper.Decompress(data);
					if (decompressed != null)
					{
						ParseObject(decompressed);
					}
				}
				else if (relativePath.Contains("refs/") || relativePath.Contains("logs/"))
				{
					string content = Encoding.UTF8.GetString(data);
					FindRefs(content);
				}
			}
			UpdateProgress();
		}
		private void UpdateProgress()
		{
			lock (_statLock)
			{
				_downloadedObjects++;
				int percent = ((_totalObjects > 0) ? (_downloadedObjects * 100 / _totalObjects) : 0);
				UpdateTitleStatus(null, $"{_downloadedObjects}/{_totalObjects} ({percent}%)");
			}
		}
		private void ParseObject(byte[] data)
		{
			int nullIdx = Array.IndexOf(data, (byte)0);
			if (nullIdx != -1)
			{
				string header = Encoding.UTF8.GetString(data, 0, nullIdx);
				string type = header.Split(' ')[0];
				if (type == "commit" || type == "tag")
				{
					string body = Encoding.UTF8.GetString(data, nullIdx + 1, data.Length - nullIdx - 1);
					FindRefs(body);
				}
				else if (type == "tree")
				{
					ParseTree(data, nullIdx + 1);
				}
			}
		}
		private void ParseTree(byte[] data, int start)
		{
			int i = start;
			while (i < data.Length)
			{
				int space = Array.IndexOf(data, (byte)32, i);
				int nullTerm = Array.IndexOf(data, (byte)0, space + 1);
				if (space == -1 || nullTerm == -1)
				{
					break;
				}
				byte[] shaBytes = new byte[20];
				Buffer.BlockCopy(data, nullTerm + 1, shaBytes, 0, 20);
				string sha = BitConverter.ToString(shaBytes).Replace("-", "").ToLower();
				EnqueueTask(_gitPathPrefix + "objects/" + sha.Substring(0, 2) + "/" + sha.Substring(2));
				i = nullTerm + 21;
			}
		}
		private byte[] DownloadDataWithRetry(string url)
		{
			for (int r = 0; r < _retry; r++)
			{
				if (_skipConfirmed)
				{
					throw new SkipException();
				}
				try
				{
					Verbose($"Requesting ({r + 1}/{_retry}): {url}");
					HttpWebRequest req = CreateRequest(url);
					using HttpWebResponse resp = (HttpWebResponse)req.GetResponse();
					if (resp.StatusCode == HttpStatusCode.OK)
					{
						Verbose($"Response OK: {url} [{resp.ContentLength} bytes]");
						using MemoryStream ms = new MemoryStream();
						resp.GetResponseStream().CopyTo(ms);
						return ms.ToArray();
					}
				}
				catch (WebException ex) when (ex.Response is HttpWebResponse hresp && hresp.StatusCode == HttpStatusCode.NotFound)
				{
					Verbose("Not Found: " + url);
					return null;
				}
				catch (Exception ex2)
				{
					Verbose($"Retry {r + 1}/{_retry} failed for {Path.GetFileName(url)}: {ex2.Message}");
					if (_skipConfirmed)
					{
						throw new SkipException();
					}
					Thread.Sleep(200);
				}
			}
			return null;
		}
		private void EnqueueTask(string path)
		{
			if (string.IsNullOrEmpty(path) || path.Contains("..") || !_downloadedFiles.TryAdd(path, 0))
			{
				return;
			}
			_pendingTasks.Enqueue(path);
			lock (_statLock)
			{
				_totalObjects++;
				UpdateTitleStatus(null, $"Scanning... Found {_totalObjects} objects");
			}
		}
	}
	public class DsStoreDumper : DumperBase
	{
		public DsStoreDumper(string url, string outDir, string ua, string proxy)
			: base(url, outDir, ua, proxy)
		{
		}
		public override void Start()
		{
			string dsUrl = BaseUrl;
			if (!dsUrl.EndsWith(".DS_Store"))
			{
				dsUrl += "/.DS_Store";
			}
			Log("Fetching DS_Store: " + dsUrl, ConsoleColor.Cyan);
			UpdateTitleStatus("DS_Store", "Downloading...");
			byte[] data = DownloadData(dsUrl);
			if (data == null)
			{
				Log("Failed to download .DS_Store", ConsoleColor.Red);
				return;
			}

			// Проверка на ложное срабатывание (HTML вместо бинарника)
			string sample = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 512));
			if (sample.IndexOf("<html", StringComparison.OrdinalIgnoreCase) >= 0 || sample.IndexOf("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				Log(".DS_Store content is HTML. Invalid (False Positive).", ConsoleColor.Red);
				return;
			}

			List<string> names = DSStoreParser.Parse(data);
			Log($"Found {names.Count} entries.", ConsoleColor.Green);
			SafeCreateDir(OutputDir);

			// Используем Uri для базового адреса, чтобы корректно резолвить ссылки
			// Если URL заканчивался на .DS_Store, мы берем родительскую директорию
			Uri baseFolderUri = new Uri(dsUrl.EndsWith(".DS_Store") ? dsUrl.Substring(0, dsUrl.LastIndexOf('/')) : dsUrl);

			foreach (string name in names)
			{
				try
				{
					// Формируем URL через Uri, чтобы избежать ошибок формата пути
					string fileUrl = new Uri(baseFolderUri, Uri.EscapeDataString(name)).ToString();
					string localPath = Path.Combine(OutputDir, name);

					SetStatus("Downloading: " + name);
					if (!File.Exists(localPath))
					{
						CreateWebClient().DownloadFile(fileUrl, localPath);
						Log("[OK] " + name, ConsoleColor.Green);
					}
				}
				catch (Exception ex)
				{
					Log("[FAIL] " + name + ": " + ex.Message, ConsoleColor.Red);
				}
			}
		}
	}
	public class IndexDumper : DumperBase
	{
		public IndexDumper(string url, string outDir, string ua, string proxy)
			: base(url, outDir, ua, proxy)
		{
		}
		public override void Start()
		{
			Log("Starting Index Dump: " + BaseUrl, ConsoleColor.White);
			UpdateTitleStatus("Index Dump", "Scanning...");
			SafeCreateDir(OutputDir);
			Queue<string> q = new Queue<string>();
			HashSet<string> visited = new HashSet<string>();
			q.Enqueue(BaseUrl);
			visited.Add(BaseUrl);
			while (q.Count > 0)
			{
				string url = q.Dequeue();
				SetStatus("Scanning: " + new Uri(url).AbsolutePath);
				Verbose("Index scanning: " + url);
				string html = DownloadString(url);
				if (html == null)
				{
					continue;
				}
				foreach (Match m in Regex.Matches(html, "href=[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase))
				{
					string link = m.Groups[1].Value;
					if (string.IsNullOrEmpty(link) || link.StartsWith("?") || link.StartsWith("#"))
					{
						continue;
					}
					Uri baseUri = new Uri(url);
					Uri fullUri = new Uri(baseUri, link);
					if (fullUri.Host != baseUri.Host)
					{
						continue;
					}
					string name = Uri.UnescapeDataString(fullUri.Segments.Last());
					if (link.EndsWith("/"))
					{
						if (!visited.Contains(fullUri.ToString()))
						{
							visited.Add(fullUri.ToString());
							q.Enqueue(fullUri.ToString());
						}
						continue;
					}
					string path = Path.Combine(OutputDir, name);
					if (!File.Exists(path))
					{
						try
						{
							SetStatus("Downloading: " + name);
							CreateWebClient().DownloadFile(fullUri.ToString(), path);
							Log("[OK] " + name, ConsoleColor.Green);
						}
						catch
						{
							Log("[FAIL] " + name, ConsoleColor.Red);
						}
					}
				}
			}
        }
    }

    private static string _logFilePath = "";
	private static bool _isVerbose = false;
	private static int _globalJobs = 10;
	private static int _globalRetry = 3;
	private static int _globalTimeout = 5;
	private static string _globalUserAgent = "Mozilla/5.0 (Windows NT 6.1; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/50.0.2661.102 Safari/537.36";
	private static string _globalProxy = "";
	private static Dictionary<string, string> _globalHeaders = new Dictionary<string, string>();
	private static string _currentMainStatus = "Idle";
	private static string _currentSubStatus = "";
	private static object _titleLock = new object();
    private static string _sessionTimestamp = "";
    private static bool _skipRequested = false;
	private static bool _skipConfirmed = false;
	private static DateTime _skipRequestTime = DateTime.MinValue;
	private static Thread _inputThread;

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetConsoleTitle(string lpConsoleTitle);

	private static void StartKeyListener()
	{
		_inputThread = new Thread((ThreadStart)delegate
		{
			while (true)
			{
				if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.S)
				{
					if (_skipRequested && (DateTime.Now - _skipRequestTime).TotalSeconds < 5.0)
					{
						_skipConfirmed = true;
						_skipRequested = false;
						Log("[CMD] SKIP CONFIRMED! Aborting current task...", ConsoleColor.Magenta);
					}
					else
					{
						_skipRequested = true;
						_skipRequestTime = DateTime.Now;
						Log("[CMD] Press 'S' again within 5 seconds to confirm SKIP.", ConsoleColor.Yellow);
					}
				}
				Thread.Sleep(100);
			}
		})
		{
			IsBackground = true
		};
		_inputThread.Start();
	}
	private static void ResetSkipState()
	{
		_skipConfirmed = false;
		_skipRequested = false;
	}
	public static void Main(string[] args)
	{
		SetupLogging();
		StartKeyListener();
		PrintHeader();
		if (args.Length == 0)
		{
			PrintUsage();
			Environment.Exit(1);
			return;
		}
		try
		{
			Verbose($"Process Started. PID: {Process.GetCurrentProcess().Id}");
			Verbose($"OS Version: {Environment.OSVersion}");
			Verbose("Command Args: " + string.Join(" ", args));
			try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)768; // TLS 1.1
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls;  // TLS 1.0
                Verbose("Security protocols configured: Tls, Tls11, Tls12");
            }
			catch (Exception ex)
			{
				Log("[WARN] TLS setup failed: " + ex.Message + ".", ConsoleColor.Yellow);
			}
			ServicePointManager.ServerCertificateValidationCallback = (RemoteCertificateValidationCallback)Delegate.Combine(ServicePointManager.ServerCertificateValidationCallback, (RemoteCertificateValidationCallback)((object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors sslPolicyErrors) => true));
			ServicePointManager.DefaultConnectionLimit = 100;
			Verbose("Certificate validation bypassed. Connection limit: 100.");
			RunPipeline(args);
		}
		catch (Exception ex2)
		{
			Log("[CRITICAL] Fatal error: " + ex2.Message, ConsoleColor.Red);
			Verbose(ex2.StackTrace);
		}
		finally
		{
			UpdateTitleStatus("Finished", "Press any key to exit");
			Log("All tasks finished. Press any key to exit...");
			Console.ReadKey();
		}
	}
	private static void SetupLogging()
	{
		string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
		if (!Directory.Exists(logDir))
		{
			Directory.CreateDirectory(logDir);
		}
		_logFilePath = Path.Combine(logDir, $"log_{DateTime.Now:yyyyMMdd_HHmmss}.log");
		Trace.Listeners.Clear();
		Trace.Listeners.Add(new ColorConsoleTraceListener());
		Trace.Listeners.Add(new TextWriterTraceListener(_logFilePath, "FileLog"));
		Trace.AutoFlush = true;
	}
	public static void Log(string message, ConsoleColor color = ConsoleColor.Gray)
	{
		lock (Console.Out)
		{
			Trace.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
		}
	}
	public static void UpdateTitleStatus(string mainStatus = null, string subStatus = null)
	{
		lock (_titleLock)
		{
			if (mainStatus != null)
			{
				_currentMainStatus = mainStatus;
			}
			if (subStatus != null)
			{
				_currentSubStatus = subStatus;
			}
			string finalTitle = "ExpoKit - " + _currentMainStatus;
			if (!string.IsNullOrEmpty(_currentSubStatus))
			{
				string sub = ((_currentSubStatus.Length > 50) ? (_currentSubStatus.Substring(0, 50) + "...") : _currentSubStatus);
				finalTitle = finalTitle + " | " + sub;
			}
			try
			{
				Console.Title = finalTitle;
			}
			catch
			{
			}
		}
	}
	public static void SetStatus(string status)
	{
		UpdateTitleStatus(null, status);
	}
	public static void Verbose(string message)
	{
		if (_isVerbose)
		{
			Log("[VERB] " + message, ConsoleColor.DarkGray);
		}
	}
	private static void PrintHeader()
	{
		Console.ForegroundColor = ConsoleColor.Red;
		Console.WriteLine("░██████████                                  ░██     ░██ ░██   ░██    ");
		Console.WriteLine("░██                                          ░██    ░██        ░██    ");
		Console.WriteLine("░██         ░██    ░██ ░████████   ░███████  ░██   ░██   ░██░████████ ");
		Console.WriteLine("░█████████   ░██  ░██  ░██    ░██ ░██    ░██ ░███████    ░██   ░██    ");
		Console.WriteLine("░██           ░█████   ░██    ░██ ░██    ░██ ░██   ░██   ░██   ░██    ");
		Console.WriteLine("░██          ░██  ░██  ░███   ░██ ░██    ░██ ░██    ░██  ░██   ░██    ");
		Console.WriteLine("░██████████ ░██    ░██ ░██░█████   ░███████  ░██     ░██ ░██    ░████ ");
		Console.WriteLine("                       ░██ Ultimate Dump Solution.                    ");
		Console.ResetColor();
		Console.WriteLine("");
	}
	private static void PrintUsage()
	{
		Console.ForegroundColor = ConsoleColor.Cyan;
		Console.WriteLine("Usage: ExpoKit.exe [Modes] [Target] [Options]");
		Console.WriteLine("");
		Console.ForegroundColor = ConsoleColor.White;
		Console.WriteLine("MODES (Can be combined):");
		Console.ResetColor();
		Console.WriteLine("  --scan                 Scan CIDR/IP/File for exposed .git/.svn/repos.");
		Console.WriteLine("  --dump                 Dump targets (Git/SVN/DS_Store/Index). Default if URL provided.");
		Console.WriteLine("  --extract-links        Extract HTTP links from files.");
		Console.WriteLine("  --extract-domains      Extract domains from files.");
		Console.WriteLine("");
		Console.ForegroundColor = ConsoleColor.White;
		Console.WriteLine("EXECUTION STRATEGY:");
		Console.ResetColor();
		Console.WriteLine("  --strategy=batch       (Default) Scan all targets first, then dump all found.");
		Console.WriteLine("  --strategy=immediate   Scan one, dump one immediately.");
		Console.WriteLine("");
		Console.ForegroundColor = ConsoleColor.White;
		Console.WriteLine("TARGET:");
		Console.ResetColor();
		Console.WriteLine("  URL, Domain, IP, CIDR (192.168.1.0/24), Range (10.0.0.1-50), File path.");
		Console.WriteLine("");
		Console.ForegroundColor = ConsoleColor.White;
		Console.WriteLine("OPTIONS:");
		Console.ResetColor();
		Console.WriteLine("  -v, --verbose          Enable verbose logging.");
		Console.WriteLine("  --jobs=N               Threads (default: 10)");
		Console.WriteLine("  --retry=N              Retries (default: 3)");
		Console.WriteLine("  --timeout=N            Timeout sec (default: 5)");
		Console.WriteLine("  --user-agent=UA        User-Agent string");
		Console.WriteLine("  --proxy=URL            Proxy URL");
		Console.WriteLine("  -H \"NAME=VALUE\"        Custom Header");
		Console.WriteLine("");
	}
	private static void RunPipeline(string[] args)
    {
        _sessionTimestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        List<string> modes = new List<string>();
		string target = null;
		string strategy = "batch";
		string outputDir = null;
		Verbose("Parsing arguments...");
		for (int i = 0; i < args.Length; i++)
		{
			string arg = args[i].ToLower();
			if (arg.StartsWith("--") || arg.StartsWith("-"))
			{
				string[] parts = (arg.StartsWith("--") ? arg.Substring(2).Split(new char[1] { '=' }, 2) : new string[1] { arg.TrimStart('-') });
				string key = parts[0];
				string val = ((parts.Length > 1) ? parts[1] : "");
				switch (key)
				{
				case "v":
				case "verbose":
					_isVerbose = true;
					Verbose("Verbose mode enabled by argument.");
					break;
				case "scan":
				case "dump":
				case "extract-links":
				case "extract-domains":
					modes.Add(key);
					Verbose("Mode added: " + key);
					break;
				case "strategy":
					if (val == "immediate" || val == "batch")
					{
						strategy = val;
					}
					Verbose("Strategy set: " + val);
					break;
				case "jobs":
					int.TryParse(val, out _globalJobs);
					Verbose($"Jobs (Threads): {_globalJobs}");
					break;
				case "retry":
					int.TryParse(val, out _globalRetry);
					Verbose($"Retries: {_globalRetry}");
					break;
				case "timeout":
					int.TryParse(val, out _globalTimeout);
					Verbose($"Timeout: {_globalTimeout} sec");
					break;
				case "user-agent":
					_globalUserAgent = args[i].Substring(args[i].IndexOf('=') + 1);
					Verbose("User-Agent: " + _globalUserAgent);
					break;
				case "proxy":
					_globalProxy = args[i].Substring(args[i].IndexOf('=') + 1);
					Verbose("Proxy: " + _globalProxy);
					break;
				}
			}
			else if (arg == "-h" && i + 1 < args.Length)
			{
				string[] hp = args[++i].Split(new char[1] { '=' }, 2);
				if (hp.Length == 2)
				{
					_globalHeaders[hp[0].Trim()] = hp[1].Trim();
					Verbose("Header added: " + hp[0] + " = " + hp[1]);
				}
			}
			else if (!arg.StartsWith("-"))
			{
				if (target == null)
				{
					target = args[i];
					Verbose("Target set: " + target);
				}
				else if (outputDir == null)
				{
					outputDir = args[i];
					Verbose("Output Dir set: " + outputDir);
				}
			}
		}
		if (modes.Count == 0 && !string.IsNullOrEmpty(target))
		{
			modes.Add("dump");
			Verbose("No mode specified, defaulting to 'dump'.");
		}
		if (modes.Count == 0 || string.IsNullOrEmpty(target))
		{
			Log("[ERROR] No target or valid mode specified.", ConsoleColor.Red);
			PrintUsage();
			return;
		}
		List<string> currentTargets = new List<string>();
		if (modes.Contains("scan"))
		{
			UpdateTitleStatus("Scanning", "Initializing...");
			Log("[INFO] Starting Scan phase...", ConsoleColor.Cyan);
			if (strategy == "immediate" && modes.Contains("dump"))
			{
				RunScannerImmediate(target, delegate(string foundUrl)
				{
					RunSingleDump(foundUrl, outputDir);
				});
			}
			else
			{
				currentTargets = RunScannerBatch(target);
			}
		}
		else if (File.Exists(target) || Directory.Exists(target) || target.Contains(",") || target.Contains("/"))
		{
			currentTargets = ExpandTargetFileOrList(target);
		}
		else
		{
			currentTargets.Add(target);
		}
		if (modes.Contains("dump") && strategy != "immediate")
		{
            UpdateTitleStatus("Dumping", "Preparing...");
            Log($"[INFO] Starting Dump phase for {currentTargets.Count} targets...", ConsoleColor.Cyan);
            if (!modes.Contains("scan"))
			{
				if (IsLocalPath(target))
				{
					ProcessBatchDump(target, outputDir);
				}
				else
				{
					RunSingleDump(target, outputDir);
				}
			}
			else
			{
                // Убираем создание batchRoot и ручное управление папками, RunSingleDump теперь это делает
                int count = 0;
                foreach (string url in currentTargets)
                {
                    count++;
                    ResetSkipState();
                    // Выводим лог, но папку не генерируем здесь
                    Log($"--- Dumping {count}/{currentTargets.Count}: {url} ---", ConsoleColor.Cyan);
                    try
                    {
                        // Передаем null, чтобы RunSingleDump сам выбрал папку по типу
                        RunSingleDump(url, null);
                    }
                    catch (SkipException)
                    {
                        Log("[WARN] Skipped by user: " + url, ConsoleColor.Yellow);
                    }
                    catch (Exception ex2)
                    {
                        Log("[ERR] Dump failed " + url + ": " + ex2.Message, ConsoleColor.Red);
                        Verbose(ex2.StackTrace);
                    }
                }
            }
		}
		if (modes.Contains("extract-links"))
		{
			RunLinkExtractorMode(new string[2]
			{
				"--extract-links",
				outputDir ?? target
			});
		}
		if (modes.Contains("extract-domains"))
		{
			RunDomainExtractorMode(new string[3]
			{
				"--extract-domains",
				outputDir ?? target,
				"extracted_domains.txt"
			});
		}
	}
	private static List<string> RunScannerBatch(string target)
	{
		List<string> targets = ExpandTargetFileOrList(target);
		List<string> foundUrls = new List<string>();
		object lck = new object();
		Log($"Loaded {targets.Count} targets for scanning.", ConsoleColor.White);
		UpdateTitleStatus("Scanning", "0/0 | Found: 0");
		int checkedCount = 0;
		int foundCount = 0;
		Parallel.ForEach(targets, new ParallelOptions
		{
			MaxDegreeOfParallelism = _globalJobs
		}, delegate(string t)
		{
			try
			{
				string text = CheckExposedRepo(t);
				if (text != null)
				{
					lock (lck)
					{
						foundUrls.Add(text);
						foundCount++;
                        string scanResultsDir = Path.Combine("ExpoKit_Results", $"ScanResults_{_sessionTimestamp}");
                        Directory.CreateDirectory(scanResultsDir); // Убедимся, что папка существует
                        File.AppendAllText(Path.Combine(scanResultsDir, "valid.txt"), text + Environment.NewLine);
                        Log("[FOUND] " + text, ConsoleColor.Green);
					}
				}
				lock (lck)
				{
					checkedCount++;
					if (checkedCount % 10 == 0 || text != null)
					{
						UpdateTitleStatus(null, $"{checkedCount}/{targets.Count} | Found: {foundCount}");
					}
				}
			}
			catch (Exception ex)
			{
				Verbose("[ERR] Scanner thread error for " + t + ": " + ex.Message);
			}
		});
		Log($"Scan finished. Found: {foundCount}", ConsoleColor.Green);
		return foundUrls;
	}
	private static void RunScannerImmediate(string target, Action<string> onFound)
	{
		List<string> targets = ExpandTargetFileOrList(target);
		Log($"Scanning {targets.Count} targets (Immediate Mode)...", ConsoleColor.White);
		int count = 0;
		Parallel.ForEach(targets, new ParallelOptions
		{
			MaxDegreeOfParallelism = Math.Max(1, _globalJobs / 2)
		}, delegate(string t)
		{
			try
			{
				string text = CheckExposedRepo(t);
				if (text != null)
				{
					Log("[FOUND & DUMP] " + text, ConsoleColor.Green);
					try
					{
						onFound(text);
					}
					catch (Exception ex)
					{
						Log("[ERR] Immediate dump failed: " + ex.Message, ConsoleColor.Red);
					}
				}
				Interlocked.Increment(ref count);
			}
			catch (Exception ex2)
			{
				Verbose("[ERR] Immediate scanner error: " + ex2.Message);
			}
		});
	}
	private static string CheckExposedRepo(string target)
	{
		string cleanTarget = Sanitizer.Clean(target);
		if (string.IsNullOrEmpty(cleanTarget))
		{
			return null;
		}
		SetStatus("Check: " + cleanTarget);
		Verbose("[Scanner] Checking target: " + cleanTarget);
		string[] protos = new string[2] { "https", "http" };
		string[] array = protos;
		foreach (string proto in array)
		{
			string baseU = proto + "://" + cleanTarget;
			string gitHead = baseU + "/.git/HEAD";
			Verbose("Probing: " + gitHead);
			if (CheckUrlExists(gitHead))
			{
				Verbose("URL Exists: " + gitHead);
				string headContent = FetchUrlSimple(gitHead);
				if (!string.IsNullOrEmpty(headContent) && (headContent.Contains("ref:") || Regex.IsMatch(headContent, "[a-f0-9]{40}")))
				{
					return baseU + "/.git/";
				}
			}
			Verbose("Probing SVN: " + baseU + "/.svn/");
			if (CheckUrlExists(baseU + "/.svn/wc.db"))
			{
				return baseU + "/.svn/";
			}
			if (CheckUrlExists(baseU + "/.svn/entries"))
			{
				return baseU + "/.svn/";
			}
			if (CheckUrlExists(baseU + "/.DS_Store"))
			{
				return baseU + "/.DS_Store";
			}
		}
		return null;
	}
    private static void RunSingleDump(string url, string outputDir, string[] overrides = null)
    {
        Verbose("RunSingleDump invoked for " + url);

        // Если папка вывода не задана, формируем её автоматически
        if (string.IsNullOrEmpty(outputDir))
        {
            string urlLower = url.ToLower();
            string dumpType;

            // Определяем тип по URL
            if (urlLower.Contains(".git")) dumpType = "GitDumps";
            else if (urlLower.Contains(".svn")) dumpType = "SvnDumps";
            else if (urlLower.Contains(".ds_store")) dumpType = "DsStoreDumps";
            else dumpType = "IndexDumps"; // По умолчанию для --index или неизвестных

            string safeName = GetDirFromUrl(url);
            // Формируем путь: ExpoKit_Results\Тип_Дата\Сайт
            outputDir = Path.Combine("ExpoKit_Results", $"{dumpType}_{_sessionTimestamp}", safeName);
            Verbose("Output directory auto-generated: " + outputDir);
        }

        // Определяем дампер
        string urlLower2 = url.ToLower();
        DumperBase dumper = null;
        dumper = (urlLower2.Contains(".git") ? new GitRecursiveDumper(url, outputDir, _globalJobs, _globalRetry, _globalTimeout, _globalUserAgent, _globalProxy, _globalHeaders) : (urlLower2.Contains(".svn") ? new SvnDumper(url, outputDir, _globalJobs, _globalRetry, _globalTimeout, _globalUserAgent, _globalProxy, _globalHeaders) : ((!urlLower2.Contains(".ds_store")) ? ((DumperBase)new IndexDumper(url, outputDir, _globalUserAgent, _globalProxy)) : ((DumperBase)new DsStoreDumper(url, outputDir, _globalUserAgent, _globalProxy)))));
        dumper.Start();
    }

    private static void ProcessBatchDump(string source, string baseDir)
	{
		List<string> urls = new List<string>();
		if (File.Exists(source))
		{
			string[] array = File.ReadAllLines(source);
			foreach (string line in array)
			{
				string t = line.Trim();
				if (!string.IsNullOrEmpty(t) && (t.StartsWith("http://") || t.StartsWith("https://")) && !urls.Contains(t))
				{
					urls.Add(t);
				}
			}
		}
		else
		{
			if (!Directory.Exists(source))
			{
				Log("Source '" + source + "' not found.", ConsoleColor.Red);
				return;
			}
			Log("Scanning directory '" + source + "' recursively...", ConsoleColor.White);
			string[] files = Directory.GetFiles(source, "*.txt", SearchOption.AllDirectories);
			foreach (string file in files)
			{
				string[] array2 = File.ReadAllLines(file);
				foreach (string line2 in array2)
				{
					string t2 = line2.Trim();
					if (!string.IsNullOrEmpty(t2) && (t2.StartsWith("http://") || t2.StartsWith("https://")) && !urls.Contains(t2))
					{
						urls.Add(t2);
					}
				}
			}
		}

        Log($"Found {urls.Count} URLs to process.", ConsoleColor.Green);

        // Если baseDir не задан, создаем общий для батча
        if (string.IsNullOrEmpty(baseDir))
        {
            baseDir = Path.Combine("ExpoKit_Results", $"BatchDumps_{_sessionTimestamp}");
        }

        int count = 0;
        foreach (string url in urls)
        {
            count++;
            string safeName = GetDirFromUrl(url);
            // Складываем всё в папку батча
            string targetDir = Path.Combine(baseDir, safeName);
            Log($"--- Processing {count}/{urls.Count}: {url} -> {targetDir} ---", ConsoleColor.Cyan);
            try
            {
                RunSingleDump(url, targetDir);
            }
            catch (Exception ex)
            {
                Log("Error processing " + url + ": " + ex.Message, ConsoleColor.Red);
                Verbose(ex.StackTrace);
            }
        }
    }
	private static void RunLinkExtractorMode(string[] args)
	{
		if (args.Length < 2)
		{
			Log("Usage: --extract-links <Folder>", ConsoleColor.Yellow);
			return;
		}
		string folder = args[1];
		if (!Directory.Exists(folder))
		{
			Log("Dir not found.", ConsoleColor.Red);
			return;
		}
		HashSet<string> links = new HashSet<string>();
		Regex reg = new Regex("(?<![a-zA-Z0-9])https?://[a-zA-Z0-9\\-._~:/?#[\\]@!$&'()*+,;=%]+", RegexOptions.IgnoreCase);
		Encoding enc = Encoding.GetEncoding("iso-8859-1");
		string[] files = Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories);
		Log($"Scanning {files.Length} files for links...", ConsoleColor.Cyan);
		string[] array = files;
		foreach (string f in array)
		{
			try
			{
				string content = enc.GetString(File.ReadAllBytes(f));
				foreach (Match m in reg.Matches(content))
				{
					if (links.Add(m.Value))
					{
						Log("[LINK] " + m.Value, ConsoleColor.Green);
					}
				}
			}
			catch
			{
			}
		}
		File.WriteAllLines("found_links.txt", links.ToArray());
		Log($"Done. Found {links.Count} unique links.", ConsoleColor.Green);
	}
	private static void RunDomainExtractorMode(string[] args)
	{
		if (args.Length < 3)
		{
			Log("Usage: --extract-domains <InputPath> <OutputFile>", ConsoleColor.Yellow);
			return;
		}
		string input = args[1];
		string outFile = args[2];
		Dictionary<string, bool> domains = new Dictionary<string, bool>();
		Regex reg = new Regex("\\b(?:https?://)?([a-zA-Z0-9][a-zA-Z0-9-]*(?:\\.[a-zA-Z0-9][a-zA-Z0-9-]*)+)\\b", RegexOptions.IgnoreCase);
		List<string> files = new List<string>();
		if (Directory.Exists(input))
		{
			files.AddRange(Directory.GetFiles(input, "*.*", SearchOption.AllDirectories));
		}
		else
		{
			if (!File.Exists(input))
			{
				Log("Input not found.", ConsoleColor.Red);
				return;
			}
			files.Add(input);
		}
		Log($"Processing {files.Count} files for domains...", ConsoleColor.Cyan);
		foreach (string f in files)
		{
			try
			{
				string content = File.ReadAllText(f);
				foreach (Match m in reg.Matches(content))
				{
					string dom = m.Groups[1].Value.ToLower();
					if (dom.Contains(".") && !domains.ContainsKey(dom))
					{
						domains[dom] = true;
						Log("[DOMAIN] " + dom, ConsoleColor.Green);
					}
				}
			}
			catch
			{
			}
		}
		File.WriteAllLines(outFile, domains.Keys.ToArray());
		Log($"Done. Found {domains.Count} unique domains.", ConsoleColor.Green);
	}
	private static string FetchUrlSimple(string url)
	{
		try
		{
			HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
			req.Timeout = _globalTimeout * 1000;
			req.UserAgent = _globalUserAgent;
			req.Method = "GET";
			using HttpWebResponse resp = (HttpWebResponse)req.GetResponse();
			using StreamReader sr = new StreamReader(resp.GetResponseStream());
			return sr.ReadToEnd();
		}
		catch
		{
			return null;
		}
	}
	private static bool CheckUrlExists(string url)
	{
		try
		{
			HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
			req.Method = "HEAD";
			req.Timeout = 5000;
			req.UserAgent = _globalUserAgent;
			using HttpWebResponse resp = (HttpWebResponse)req.GetResponse();
			return resp.StatusCode == HttpStatusCode.OK;
		}
		catch (WebException ex)
		{
			if (ex.Response == null)
			{
				return false;
			}
			if (ex.Response is HttpWebResponse { StatusCode: HttpStatusCode.MethodNotAllowed })
			{
				try
				{
					HttpWebRequest reqGet = (HttpWebRequest)WebRequest.Create(url);
					reqGet.Method = "GET";
					reqGet.Timeout = 5000;
					using HttpWebResponse r = (HttpWebResponse)reqGet.GetResponse();
					return r.StatusCode == HttpStatusCode.OK;
				}
				catch
				{
					return false;
				}
			}
			return false;
		}
	}
	private static List<string> ExpandTargetFileOrList(string input)
	{
		List<string> res = new List<string>();
		if (File.Exists(input))
		{
			string[] array = File.ReadAllLines(input);
			foreach (string line in array)
			{
				string clean = Sanitizer.Clean(line);
				if (!string.IsNullOrEmpty(clean))
				{
					ExpandTarget(clean, res);
				}
			}
		}
		else if (Directory.Exists(input))
		{
			string[] files = Directory.GetFiles(input, "*.txt", SearchOption.AllDirectories);
			foreach (string file in files)
			{
				string[] array2 = File.ReadAllLines(file);
				foreach (string line2 in array2)
				{
					string clean2 = Sanitizer.Clean(line2);
					if (!string.IsNullOrEmpty(clean2))
					{
						ExpandTarget(clean2, res);
					}
				}
			}
		}
		else
		{
			ExpandTarget(input, res);
		}
		return res;
	}
	private static void ExpandTarget(string input, List<string> output)
	{
		try
		{
			if (input.Contains("-"))
			{
				string[] p = input.Split('-');
				if (p.Length == 2 && IPAddress.TryParse(p[0].Trim(), out var sIp) && int.TryParse(p[1].Trim(), out var endNum))
				{
					byte[] bytes = sIp.GetAddressBytes();
					if (bytes.Length == 4)
					{
						int start = bytes[3];
						if (endNum > start && endNum < 255)
						{
							Log($"Expanding Range: {input} -> {endNum - start + 1} IPs", ConsoleColor.Cyan);
							for (int i = start; i <= endNum; i++)
							{
								bytes[3] = (byte)i;
								output.Add(new IPAddress(bytes).ToString());
							}
							return;
						}
					}
				}
			}
			if (input.Contains("/"))
			{
				string[] p2 = input.Split('/');
				if (p2.Length == 2 && IPAddress.TryParse(p2[0], out var baseIp) && int.TryParse(p2[1], out var prefix) && baseIp.AddressFamily == AddressFamily.InterNetwork && prefix >= 16 && prefix <= 32)
				{
					uint b = IpToUint(baseIp);
					uint mask = (uint)(-1 << 32 - prefix);
					uint s = b & mask;
					uint e = s | ~mask;
					uint count = e - s;
					Log($"Expanding CIDR: {input} -> ~{count} IPs", ConsoleColor.Cyan);
					if (count > 1000)
					{
						Log("[WARN] Large CIDR range detected. This may take time.", ConsoleColor.Yellow);
					}
					for (uint i2 = s; i2 <= e; i2++)
					{
						output.Add(UintToIp(i2).ToString());
					}
					return;
				}
			}
			output.Add(input);
		}
		catch
		{
			output.Add(input);
		}
	}
	private static uint IpToUint(IPAddress ip)
	{
		byte[] b = ip.GetAddressBytes();
		Array.Reverse(b);
		return BitConverter.ToUInt32(b, 0);
	}
	private static IPAddress UintToIp(uint ip)
	{
		byte[] b = BitConverter.GetBytes(ip);
		Array.Reverse(b);
		return new IPAddress(b);
	}
	private static bool IsLocalPath(string path)
	{
		return File.Exists(path) || Directory.Exists(path) || (!path.StartsWith("http") && (path.Contains("\\") || path.Contains(":")));
	}
	private static string GetDirFromUrl(string url)
	{
		try
		{
			return Regex.Replace(new Uri(url).Host, "[^a-zA-Z0-9.-]", "_");
		}
		catch
		{
			return "output";
		}
	}
}
public class ColorConsoleTraceListener : TraceListener
{
	public override void Write(string message)
	{
		WriteColor(message);
	}
	public override void WriteLine(string message)
	{
		WriteColor(message + Environment.NewLine);
	}
	private void WriteColor(string message)
	{
		if (!Console.Out.Equals(Stream.Null))
		{
			ConsoleColor c = ConsoleColor.Gray;
			if (message.Contains("[ERR]") || message.Contains("[FAIL]") || message.Contains("[CRITICAL]"))
			{
				c = ConsoleColor.Red;
			}
			else if (message.Contains("[WARN]"))
			{
				c = ConsoleColor.Yellow;
			}
			else if (message.Contains("[FOUND]") || message.Contains("[OK]"))
			{
				c = ConsoleColor.Green;
			}
			else if (message.Contains("[INFO]"))
			{
				c = ConsoleColor.Cyan;
			}
			else if (message.Contains("[VERB]"))
			{
				c = ConsoleColor.DarkGray;
			}
			Console.ForegroundColor = c;
			Console.Write(message);
			Console.ResetColor();
		}
	}
}
public static class DSStoreParser
{
	public static List<string> Parse(byte[] data)
	{
		List<string> files = new List<string>();
		try
		{
			string s = Encoding.ASCII.GetString(data);
			foreach (Match m in Regex.Matches(s, "[a-zA-Z0-9_\\-\\.]{3,40}"))
			{
				string val = m.Value;
				if (val.Contains(".") && !files.Contains(val) && !val.StartsWith("DSStore") && !val.StartsWith("http"))
				{
					files.Add(val);
				}
			}
		}
		catch
		{
		}
		return files;
	}
}
public static class Sanitizer
{
	public static string Clean(string input)
	{
		if (string.IsNullOrWhiteSpace(input))
		{
			return "";
		}
		input = Regex.Replace(input, "^https?://", "", RegexOptions.IgnoreCase);
		input = input.Split('/')[0];
		return input.Trim();
	}
}
public class SkipException : Exception
{
	public SkipException()
		: base("User requested skip")
	{
	}
}
public class TimeoutWebClient : WebClient
{
	private int _timeout;
	public TimeoutWebClient(int timeout)
	{
		_timeout = timeout;
	}
	protected override WebRequest GetWebRequest(Uri uri)
	{
		WebRequest w = base.GetWebRequest(uri);
		w.Timeout = _timeout;
		return w;
	}
}
public static class ZlibHelper
{
	public static byte[] Decompress(byte[] data)
	{
		if (data == null || data.Length < 2)
		{
			return null;
		}
		if (data[0] == 120)
		{
			try
			{
				using MemoryStream ms = new MemoryStream(data, 2, data.Length - 2);
				using DeflateStream ds = new DeflateStream(ms, CompressionMode.Decompress);
				using MemoryStream outMs = new MemoryStream();
				ds.CopyTo(outMs);
				return outMs.ToArray();
			}
			catch
			{
			}
		}
		return null;
	}
}