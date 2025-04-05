# Credits
https://github.com/SteamAutoCracks/DepotDumper for the code he did then the credits he gave to the people before that.
# DepotDumper

A Steam depot key and manifest dumping utility utilizing the SteamKit2 library. This tool helps archive and extract Steam depots' encryption keys and manifests, which can be useful for game preservation and research purposes.

## Features

- Dump Steam depot keys and manifests for games in your library
- Process multiple apps in parallel
- Generate detailed reports in HTML, JSON, CSV, and TXT formats
- Support for branch-specific manifest extraction
- QR code login support for Steam Guard
- Automatic DLC detection and inclusion
- Batch processing via config files or command line
- Options to save results as ZIP archives

## Requirements

- .NET 9.0 runtime or later
- A valid Steam account with games in your library
- Windows, macOS, or Linux operating system

## Usage

### Basic Usage

```bash
# Dumping keys for all owned games
.\DepotDumper.exe -username steamuser -password yourpassword

# Dumping keys for a specific app
.\DepotDumper.exe -username steamuser -password yourpassword -appid 440

# Using remembered password/token
.\DepotDumper.exe -username steamuser -remember-password

# Using QR code login (through Steam Mobile App)
.\DepotDumper.exe -username steamuser -qr
```

### Configuration Utility

```bash
# Launch configuration utility
.\DepotDumper.exe config create

# Add app to configuration
.\DepotDumper.exe config add-app 440

# List configured apps
.\DepotDumper.exe config list-apps
```

## Command Line Parameters

### Authentication Options
| Parameter | Description |
|-----------|-------------|
| `-username` | Steam account username |
| `-password` | Steam account password |
| `-qr` | Use QR code for login via Steam Mobile App |
| `-remember-password` | Remember password/token for subsequent logins |
| `-loginid` | Unique 32-bit ID for multiple concurrent instances |

### App Selection Options
| Parameter | Description |
|-----------|-------------|
| `-appid` | Dump ONLY depot keys for this specific App ID |
| `-appids-file` | Process App IDs listed in specified file (one per line) |
| `-select` | Interactively select which depots to process |

### Output Options
| Parameter | Description |
|-----------|-------------|
| `-dir` | Directory for dumped files (Default: 'dumps') |
| `-generate-reports` | Generate HTML, JSON, CSV, and TXT reports |
| `-reports-dir` | Directory for report files (Default: dumps/reports) |
| `-logs-dir` | Directory for log files (Default: dumps/logs) |
| `-log-level` | Logging detail level (debug, info, warning, error, critical) |

### Performance Options
| Parameter | Description |
|-----------|-------------|
| `-max-downloads` | Maximum concurrent manifest downloads per depot (Default: 4) |
| `-max-servers` | Maximum CDN servers to use (Default: 20) |
| `-max-concurrent-apps` | Maximum apps to process concurrently (Default: 1) |
| `-cellid` | Specify Cell ID for connection (Default: Auto) |

### Configuration Options
| Parameter | Description |
|-----------|-------------|
| `-config` | Use configuration from the specified JSON file |
| `-save-config` | Save current settings to config file |

## Output Files

The program creates several files in the output directory:

- **App folders**: Each app gets its own folder named by App ID
- **Manifests**: Files with pattern `[DepotID]_[ManifestID].manifest`
- **Key files**: Files with pattern `[AppID].key` containing depot keys
- **Info files**: Files with pattern `[AppID].info` containing app metadata
- **Lua files**: Helper files with pattern `[AppID].lua` for each branch
- **Reports**: HTML, JSON, CSV, and TXT reports in the reports directory

## Configuration

A JSON configuration file can be created with the configuration utility:

```bash
DepotDumper config create
```

The configuration file supports these settings:

```json
{
  "Username": "your_username",
  "Password": "your_password",
  "RememberPassword": true,
  "UseQrCode": false,
  "CellID": 0,
  "MaxDownloads": 4,
  "MaxServers": 20,
  "LoginID": null,
  "DumpDirectory": "dumps",
  "UseNewNamingFormat": true,
  "MaxConcurrentApps": 1,
  "AppIdsToProcess": [440, 570, 730],
  "ExcludedAppIds": [220]
}
```

## Notes

- All tokens and keys are global and identical for every Steam user. They do not identify you or your account.
- The program supports running with no parameters if a valid `config.json` file exists.
- When using QR code login, the code will display in the console for scanning with the Steam Mobile App.
- The application will generate logs in the specified logs directory for troubleshooting.

## Credits

DepotDumper is based on the SteamKit2 library and inspired by DepotDownloader.

## License

This project is licensed under GNU General Public License v2.0.
