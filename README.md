# ExpoKit

```
███████╗██╗  ██╗██████╗  ██████╗ ██╗  ██╗██╗████████╗
██╔════╝╚██╗██╔╝██╔══██╗██╔═══██╗██║ ██╔╝██║╚══██╔══╝
█████╗   ╚███╔╝ ██████╔╝██║   ██║█████╔╝ ██║   ██║   
██╔══╝   ██╔██╗ ██╔═══╝ ██║   ██║██╔═██╗ ██║   ██║   
███████╗██╔╝ ██╗██║     ╚██████╔╝██║  ██╗██║   ██║   
╚══════╝╚═╝  ╚═╝╚═╝      ╚═════╝ ╚═╝  ╚═╝╚═╝   ╚═╝   
```

**ExpoKit** — Ultimate Dump Solution. A comprehensive security auditing tool for scanning and dumping data from exposed repositories and files on web servers. Designed for identifying accidentally published sensitive data during penetration testing.

## ⚠️ Disclaimer

> This tool is intended **for legal use only** within the scope of penetration testing with written permission from the system owner or for auditing your own resources. Unauthorized access to computer systems is illegal.

## 📋 Features

- **Git Dumper** — Full dump of exposed `.git` repositories (Recursive & Brute-force modes)
- **SVN Dumper** — Dump `.svn` repositories (Supports `wc.db` and `entries` formats)
- **DS_Store Dumper** — Extract filenames from `.DS_Store` and download listed files
- **Index Dumper** — Dump files from open directory listings
- **Network Scanner** — Search for exposed repositories by IP/CIDR/Range/File list
- **Link Extractor** — Parse HTTP links from files
- **Domain Extractor** — Parse domain names from files
- **Multi-threading** — Configurable job threads (default: 10)
- **Proxy Support** — HTTP/HTTPS proxy support
- **Retry Logic** — Automatic retries on failure (default: 3)
- **Abort Control** — Interrupt operation (Double press `S` within 5 seconds)
- **Safety** — Sanitizes `.git/config` to prevent RCE during checkout
- **Progress Tracking** — Console title updates with real-time progress
- **Color Logging** — Color-coded console output with file logging

## 🚀 Quick Start

### Compilation

```bash
# Using .NET CLI
dotnet build -c Release

# Or using MSBuild
msbuild ExpoKit.csproj /p:Configuration=Release
```

*Note: Requires .NET Framework 4.0 (For potential Windows XP Compatiable) or .NET Core/5+ depending on project configuration.*

### Basic Usage

```bash
# Dump a Git repository
ExpoKit.exe --dump https://example.com/.git ./output

# Scan and dump immediately
ExpoKit.exe --scan --dump --strategy=immediate 192.168.1.0/24

# Dump from a list of URLs (or domains list)
ExpoKit.exe --dump targets.txt ./output
```

## 📖 Modes and Commands

### Operation Modes

| Mode | Description |
|------|----------|
| `--scan` | Scan CIDR/IP/File for exposed `.git`/`.svn`/`.DS_Store` |
| `--dump` | Dump data from target URLs (Default if URL provided) |
| `--extract-links` | Extract HTTP links from files |
| `--extract-domains` | Extract domains from files |

### Execution Strategies

| Strategy | Description |
|----------|----------|
| `--strategy=batch` | (Default) Scan all targets first, then dump all found |
| `--strategy=immediate` | Scan and dump each target immediately upon discovery |

## ⚙️ Command Line Options

```
Usage: ExpoKit.exe [Modes] [Target] [Options]

OPTIONS:
  -v, --verbose          Enable verbose logging
  --jobs=N               Number of threads (default: 10)
  --retry=N              Number of retry attempts (default: 3)
  --timeout=N            Timeout in seconds (default: 5)
  --user-agent=UA        Custom User-Agent string
  --proxy=URL            Proxy server URL
  -H "NAME=VALUE"        Custom HTTP Header
```

## 📝 Usage Examples

### 1. Dump a Git Repository

```bash
ExpoKit.exe --dump https://target.com/.git ./git_dump
```

### 2. Scan a Subnet

```bash
ExpoKit.exe --scan --dump 10.0.0.0/24 ./scan_results
```

### 3. Work with a Target List

```bash
ExpoKit.exe --dump targets.txt ./output
```

### 4. Use Proxy and Custom Headers

```bash
ExpoKit.exe --dump https://target.com/.git ./output --proxy=http://127.0.0.1:8080 -H "Authorization=Bearer token123"
```

### 5. Extract Domains from Files

```bash
ExpoKit.exe --extract-domains ./data_folder extracted_domains.txt
```

### 6. Verbose Mode with Thread Control

```bash
ExpoKit.exe --dump https://target.com/.git ./output -v --jobs=20 --retry=5
```

### 7. Scan with Immediate Dump

```bash
ExpoKit.exe --scan --dump --strategy=immediate 192.168.1.0/24
```

### 8. Extract Links from Directory

```bash
ExpoKit.exe --extract-links ./data_folder
```

## 🗂️ Project Structure

```
ExpoKit/
├── Program.cs              # Single file containing all functionality
├── Logs/                   # Log files directory (auto-created)
└── ExpoKit_Results/        # Dump results (auto-created)
    ├── GitDumps_YYYYMMDD_HHMMSS/
    ├── SvnDumps_YYYYMMDD_HHMMSS/
    ├── DsStoreDumps_YYYYMMDD_HHMMSS/
    ├── IndexDumps_YYYYMMDD_HHMMSS/
    ├── BatchDumps_YYYYMMDD_HHMMSS/
    └── ScanResults_YYYYMMDD_HHMMSS/
```

## 📊 Target Formats

The tool supports various input formats:

```
# Single URL
https://example.com/.git

# CIDR Range
192.168.1.0/24

# IP Range
10.0.0.1-50

# File List
targets.txt

# Directory (reads all .txt files recursively)
./targets/
```

## 🛡️ Security Features

- **`.git/config` Sanitization** — Automatically disables dangerous commands (`fsmonitor`, `sshcommand`, `askpass`, `editor`, `pager`) to prevent RCE during checkout
- **SSL Validation Bypass** — Automatically bypasses certificate validation (useful for test environments with self-signed certs)
- **Timeouts** — Prevents hanging on network requests (configurable)
- **Logging** — All actions are logged to files in the `Logs/` directory with timestamps
- **Connection Limits** — Default connection limit set to 100

## 📁 Output Structure

```
ExpoKit_Results/
├── GitDumps_20240101_120000/
│   └── example_com/
│       └── .git/
│           ├── objects/
│           ├── refs/
│           └── config
├── SvnDumps_20240101_120000/
│   └── example_com/
│       ├── wc.db
│       └── pristine/
├── ScanResults_20240101_120000/
│   └── valid.txt
└── Logs/
    └── log_20240101_120000.log
```

*Note: To fully restore a Git repository, `git.exe` must be installed and available in your PATH, as the tool attempts to run `git checkout .` automatically.*

## ⌨️ Runtime Controls

| Key | Action |
|-----|--------|
| `S` (Press twice within 5 sec) | Abort current operation |

## 🔍 Git Dumper Algorithm

1. Check for directory listing availability
2. If available: Recursive directory traversal
3. If not available: Brute-force mode:
   - Fetch `HEAD`
   - Search for refs in config
   - Process `packed-refs`
   - Parse `index` file
   - Process pack files
4. Decompress objects and search for additional refs
5. Execute `git checkout` to restore files
6. Sanitize `.git/config` to prevent RCE

## 🔍 SVN Dumper Algorithm

1. Check for `wc.db` (SVN 1.7+ format)
2. If found: Parse SHA1 hashes and download pristine files
3. If not found: Check for `entries` (Legacy format)
4. Download all available revision files

## 🔍 Scanner Detection

The scanner checks for:
- `.git/HEAD` (Git repositories)
- `.svn/wc.db` (SVN 1.7+)
- `.svn/entries` (SVN Legacy)
- `.DS_Store` (macOS metadata)

Both HTTP and HTTPS protocols are tested for each target.

## 📄 Log File Format

Logs are saved to `Logs/log_YYYYMMDD_HHMMSS.log` with color-coded entries:

| Level | Color | Example |
|-------|-------|---------|
| `[OK]` / `[FOUND]` | Green | `[OK] .git/objects/ab/cdef123...` |
| `[INFO]` | Cyan | `[INFO] Starting Scan phase...` |
| `[WARN]` | Yellow | `[WARN] Large CIDR range detected` |
| `[ERR]` / `[FAIL]` | Red | `[ERR] Download failed` |
| `[VERB]` | Dark Gray | `[VERB] Requesting: https://...` |

## 📈 Performance Tips

- Increase `--jobs` for faster scanning (default: 10)
- Use `--strategy=immediate` for quick results on small ranges
- Use `--strategy=batch` for large scans to avoid duplicate work
- Enable `--verbose` for debugging connection issues
- Use proxy for anonymous scanning

## 🐛 Troubleshooting

| Issue | Solution |
|-------|----------|
| Git checkout fails | Ensure `git.exe` is in PATH |
| SSL errors | Tool auto-bypasses cert validation |
| Slow scanning | Increase `--jobs` value |
| Connection timeouts | Increase `--timeout` value |
| Missing files | Check `--retry` value and network |

## 📄 License

Use responsibly and only for lawful purposes.

## 🤝 Contributing

To contribute:
1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Submit a pull request

## 📞 Support

If you encounter issues:
1. Check logs in the `Logs/` folder
2. Use `--verbose` mode for detailed output
3. Ensure the target resource is accessible
4. Verify network connectivity and proxy settings

---

**ExpoKit** — Ultimate Dump Solution for web resource security auditing.
