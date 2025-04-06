# DepotDumper

DepotDumper is a utility for extracting and managing Steam depot keys. It helps you dump depot information, manifest files, and create organized zip archives for later use.

## Features

- Dump depot keys and manifest files from your Steam account
- Organize dumps into folders by app ID, branch, and date
- Create ZIP archives of dumped data
- Generate detailed reports in HTML, CSV, JSON, and TXT formats
- Process multiple apps in parallel
- Track statistics of your dumping operations
- Configurable through command line or config.json
- Support for QR code login and token authentication

## Installation

1. Download the latest release from the releases page
2. Extract to a folder of your choice
3. Run DepotDumper.exe

## Usage

### Basic Usage

```
DepotDumper -username <username> -password <password> [options]
```

### Quick Start

Double-click the executable to run with the settings from `config.json` in the same directory.

### Configuration

You can configure DepotDumper through:

1. Command line parameters
2. Configuration file (config.json)
3. Interactive configuration utility (`DepotDumper config`)

### Command Line Options

#### Required Account Options:
- `-username <user>` - Your Steam username
- `-password <pass>` - Your Steam password (not needed with QR or remember-password)

#### Optional Login Options:
- `-qr` - Use QR code for login via Steam Mobile App
- `-remember-password` - Remember password/token for subsequent logins
- `-loginid <#>` - Unique 32-bit ID for running multiple instances

#### Dumping Options:
- `-appid <#>` - Dump ONLY depots for this specific App ID
- `-appids-file <path>` - Dump ONLY depots for App IDs listed in the file
- `-select` - Interactively select depots to dump

#### Configuration & Output Options:
- `-config <path>` - Use config from specified JSON file (Default: config.json)
- `-save-config` - Save current settings back to the config file
- `-dir <path>` - Directory to dump depots (Default: 'dumps')
- `-log-level <level>` - Set logging detail level
- `-logs-dir <path>` - Directory for log files (Default: dumps/logs)
- `-generate-reports` - Generate reports after completion
- `-reports-dir <path>` - Directory for reports (Default: dumps/reports)

#### Performance Options:
- `-max-downloads <#>` - Max concurrent manifest downloads per depot (Default: 4)
- `-max-servers <#>` - Max CDN servers to use (Default: 20)
- `-max-concurrent-apps <#>` - Max apps to process concurrently (Default: 1)
- `-cellid <#>` - Specify Cell ID for connection (Default: Auto)

#### Other Options:
- `-debug` - Enable verbose debug messages
- `-V | --version` - Show version information
- `config` - Enter configuration utility mode

## Configuration File

The configuration file (`config.json`) can be used to store your settings. Here's an example:

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
  "LogLevel": "Info",
  "AppIdsToProcess": [],
  "ExcludedAppIds": []
}
```

### LogLevel Options

The `LogLevel` property controls the verbosity of logging. Available levels are:

- `Debug` - Most verbose, shows all messages (useful for troubleshooting)
- `Info` - Shows informational messages and above (default)
- `Warning` - Shows warnings and errors only
- `Error` - Shows only errors 
- `Critical` - Shows only critical errors

## Configuration Utility

You can use the built-in configuration utility to interactively set up your config file:

```
DepotDumper config
```

## Output Structure

DepotDumper organizes dumps in the following structure:

```
dumps/
├── AppID/
│   ├── Branch/
│   │   ├── DepotID_ManifestID.manifest
│   │   └── AppID.lua
│   └── AppID.key
├── reports/
│   ├── report.html
│   ├── summary.txt
│   ├── apps.csv
│   └── full_report.json
└── logs/
    └── depotdumper_YYYYMMDD_HHMMSS.log
```

For each manifest processed, DepotDumper creates a ZIP archive with the format:
```
AppID.Branch.YYYY-MM-DD_HH-MM-SS.AppName/AppID.zip
```

## License

This utility is provided as-is without any warranty. Use at your own risk.

## Acknowledgements

Based on the Steam platform by Valve Corporation.
Uses SteamKit2 for Steam communication.