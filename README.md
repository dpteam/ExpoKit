# ExpoKit

```
███████╗██╗  ██╗██████╗  ██████╗ ██╗  ██╗██╗████████╗
██╔════╝╚██╗██╔╝██╔══██╗██╔═══██╗██║ ██╔╝██║╚══██╔══╝
█████╗   ╚███╔╝ ██████╔╝██║   ██║█████╔╝ ██║   ██║   
██╔══╝   ██╔██╗ ██╔═══╝ ██║   ██║██╔═██╗ ██║   ██║   
███████╗██╔╝ ██╗██║     ╚██████╔╝██║  ██╗██║   ██║   
╚══════╝╚═╝  ╚═╝╚═╝      ╚═════╝ ╚═╝  ╚═╝╚═╝   ╚═╝   
```

**ExpoKit** — Ultimate Dump Solution. A multi-functional tool for scanning and dumping data from exposed repositories and files on web servers. Designed for security auditing and identifying accidentally published sensitive data.

## ⚠️ Disclaimer

> This tool is intended **for legal use only** within the scope of penetration testing with written permission from the system owner or for auditing your own resources. Unauthorized access to computer systems is illegal.

## 📋 Features

- **Git Dumper** — Full dump of exposed `.git` repositories (Recursive & Brute-force modes)
- **SVN Dumper** — Dump `.svn` repositories (Supports `wc.db` and `entries`)
- **DS_Store Dumper** — Extract filenames from `.DS_Store`
- **Index Dumper** — Dump files from open directory listings
- **Scanner** — Search for exposed repositories by IP/CIDR/File list
- **Link Extractor** — Parse HTTP links from files
- **Domain Extractor** — Parse domain names from files
- **Multi-threading** — Configurable job threads
- **Proxy Support** — HTTP/HTTPS proxy support
- **Retry Logic** — Automatic retries on failure
- **Abort Control** — Interrupt operation (Double press `S`)
- **Safety** — Sanitizes `.git/config` to prevent RCE

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
| `--scan` | Scan CIDR/IP/File for exposed `.git`/`.svn`/repos |
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
ExpoKit.exe --extract-domains ./data_folder domains.txt
```

### 6. Verbose Mode with Thread Control

```bash
ExpoKit.exe --dump https://target.com/.git ./output -v --jobs=20 --retry=5
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

- **`.git/config` Sanitization** — Automatically disables dangerous commands to prevent RCE during checkout.
- **SSL Validation** — Bypasses certificate validation (useful for test environments with self-signed certs).
- **Timeouts** — Prevents hanging on network requests.
- **Logging** — All actions are logged to files in the `Logs/` directory.

## 📁 Output Structure

```
output/
├── .git/                   # Dumped Git repository
│   ├── objects/
│   ├── refs/
│   └── config
├── wc.db                   # SVN database
├── pristine/               # SVN files
└── Logs/
    └── log_YYYYMMDD_HHMMSS.log
```

*Note: To fully restore a Git repository, `git.exe` must be installed and available in your PATH, as the tool attempts to run `git checkout .` automatically.*

## ⌨️ Runtime Controls

| Key | Action |
|-----|--------|
| `S` (Press twice within 5 sec) | Abort current operation |

## 🔍 Git Dumper Algorithm

1. Check for directory listing availability.
2. If available: Recursive directory traversal.
3. If not available: Brute-force mode:
   - Fetch `HEAD`
   - Search for refs in config
   - Process `packed-refs`
   - Parse `index` file
   - Process pack files
4. Decompress objects and search for additional refs.
5. Execute `git checkout` to restore files.

## 📄 License

Use responsibly and only for lawful purposes.

## 🤝 Contributing

To contribute:
1. Fork the repository.
2. Create a feature branch.
3. Make your changes.
4. Submit a pull request.

## 📞 Support

If you encounter issues:
1. Check logs in the `Logs/` folder.
2. Use `--verbose` mode for detailed output.
3. Ensure the target resource is accessible.

---

**ExpoKit** — Ultimate Dump Solution for web resource security auditing.
