DepotDumper
===============

Steam depot key dumper utilizing the SteamKit2 library. Supports .NET 5.0  
Modified from https://github.com/SteamRE/DepotDownloader  
All tokens and keys are global and are always the same to every Steam user, they are not unique to your account and do not identify you.  

### Dumping all depots in the steam account
```
dotnet DepotDumper.dll -username <username> -password <password> [other options]
```

## Parameters

Parameter | Description
--------- | -----------
-username \<user>		| the username of the account to dump keys.
-password \<pass>		| the password of the account to dump keys.
-remember-password		| if set, remember the password for subsequent logins of this user. (Use -username <username> -remember-password as login credentials)
-loginid \<#>			| a unique 32-bit integer Steam LogonID in decimal, required if running multiple instances of DepotDownloader concurrently. 
-select                 | select depot to dump key.
## Result files
**steam.appids**
* Contains "appId;appName" lines

**steam.keys**
* Contains "depotId;depotKey" lines

