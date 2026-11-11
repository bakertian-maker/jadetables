# Deploying Jade Tables to IIS

The site runs at **https://www.jadetables.com**. Requests to `jadetables.com` are redirected to `www`, and plain HTTP is redirected to HTTPS.

The steps below are for a Windows server running IIS, with SQL Server Express on the same machine.

## One-time server setup

1. **Install the .NET 10 Hosting Bundle** (ASP.NET Core Runtime 10.x, "Hosting Bundle" link) from https://dotnet.microsoft.com/download/dotnet/10.0. Restart IIS afterwards (`iisreset`).
2. **Turn on WebSockets.** Server Manager → Add Roles and Features → Web Server (IIS) → Web Server → Application Development → **WebSocket Protocol**. The games use a live connection; without WebSockets they fall back to slower polling.
3. **SQL Server Express.** Create the `Games` database and a SQL login that is `db_owner` on it. The tables are created on the first start (`Database:MigrateOnStartup` is on in production).
4. **Certificate.** Import the certificate (with its private key) into *Local Computer → Personal*. It must cover both `www.jadetables.com` and `jadetables.com`; the redirect from `jadetables.com` happens over HTTPS, so that name needs the certificate too.
5. **Application pool** (IIS Manager → Application Pools → Add):
   - Name `JadeTables`, .NET CLR version **No Managed Code**.
   - Advanced Settings:
     - **Start Mode** `AlwaysRunning`.
     - **Idle Time-out** `0`.
     - **Regular Time Interval** (under Recycling) `0`.
     - **Load User Profile** `True`. This keeps the encryption keys between restarts, so players aren't disconnected after a deploy.
     - **Maximum Worker Processes** `1`. Rooms live in memory, so there must be exactly one copy of the app.

   Games in progress are lost whenever the pool restarts, which is why the automatic recycling is switched off.
6. **Connection string.** Set it on the application pool, so it never sits in the site folder and publishing never overwrites it. Run this in an administrator command prompt, with your own password:

   ```
   %windir%\system32\inetsrv\appcmd set config -section:system.applicationHost/applicationPools /+"[name='JadeTables'].environmentVariables.[name='ConnectionStrings__Games',value='Server=.\SQLEXPRESS;Database=Games;User ID=...;Password=...;TrustServerCertificate=True']" /commit:apphost
   ```
7. **Site** (IIS Manager → Sites → Add Website):
   - Name `JadeTables`, application pool `JadeTables`, physical path for example `C:\inetpub\jadetables`.
   - Bindings:
     - `https`, port 443, host `www.jadetables.com`, **Require Server Name Indication**, your certificate.
     - `https`, port 443, host `jadetables.com`, **Require Server Name Indication**, your certificate.
     - `http`, port 80, host `www.jadetables.com`.
     - `http`, port 80, host `jadetables.com`.
8. **DNS.** Point both `jadetables.com` (A record) and `www.jadetables.com` (A record or CNAME) at the server's public IP. Open ports 80 and 443 in the firewall.

## Each release

The scripts in `Deploy/` do the work. Copy `install-release.ps1` and `backup-database.ps1` to `C:\Deploy` on the server once (and again if they change).

**1. On your PC:** build the release.

```
powershell -ExecutionPolicy Bypass -File Deployuild-release.ps1
```

It runs the tests (and stops if any fail), publishes for IIS and writes `Deploy\out\jadetables-<date>-<commit>.zip`. It warns if you have uncommitted changes. Add `-CopyTo \your-server\deploy` to copy the zip to a shared folder.

**2. Copy the zip to the server**, for example into `C:\Deploy` through Remote Desktop.

**3. On the server**, in an administrator PowerShell:

```
powershell -ExecutionPolicy Bypass -File C:\Deploy\install-release.ps1
```

It installs the newest zip in `C:\Deploy` (or the one given with `-Zip`):
- Keeps a copy of the current version in `C:\Websites\_releases` (the last 5 are kept).
- Shows a short "back in a moment" page while it copies the files.
- Checks that https://www.jadetables.com/ answers afterwards.

If the defaults don't match your server, pass `-Site <site folder>` or `-Url <address>`.

Games in progress end when the site restarts, because rooms are kept in memory. Install when it's quiet.

**If something is wrong**, put the previous version back:

```
powershell -ExecutionPolicy Bypass -File C:\Deploy\install-release.ps1 -Rollback
```

Files that a new release no longer uses are left in place, which is harmless. Releases with database changes also need the SQL script from `Database/` (or `Database:MigrateOnStartup`) before or along with the install.

## Nightly database backups

Set up once, on the server in an administrator PowerShell:

```
powershell -ExecutionPolicy Bypass -File C:\Deployackup-database.ps1 -Install
```

This does three things:
- Gives the Windows SYSTEM account permission to back up the `Games` database.
- Creates the scheduled task **Jade Tables database backup**, which runs every night at 03:30.
- Makes a first backup straight away, so you can see that it works.

Backups go to `C:\Backups\JadeTables` as `Games-<date>.bak`. SQL Server checks every page as it writes them, and anything older than 14 days is deleted. Change these with `-Destination`, `-KeepDays` and `-At`. To back up by hand at any time, run the script without `-Install`.

- **Copy backups off the server too**, for example to your PC or cloud storage, every week or so. A backup on the same disk doesn't help if the server itself is lost.
- **To restore**, use SQL Server Management Studio: right-click **Databases** → **Restore Database…** → **Device**, and pick the `.bak` file.

## Checks after deploying

- https://www.jadetables.com opens.
- http://jadetables.com ends up at https://www.jadetables.com.
- A game against the computer starts and moves.
- A finished game appears in the `Games` table.
- If the site doesn't start, set `stdoutLogEnabled="true"` in the published `web.config`, create a `logs` folder next to it, and read the log. Switch it off again afterwards.

## Accounts: email and Google sign-in

Accounts work as soon as the database is connected. Two optional settings turn on email and Google sign-in. Like the connection string, put them on the application pool as environment variables, never in a file in the site folder. Recycle the pool after changing them.

Until email is set up, new players can play straight away without confirming their address, and the confirmation and password-reset links are only written to the log.

### Email (SMTP)

Use the SMTP details from your mail provider:

| Variable | Example |
|---|---|
| `Email__Host` | `smtp.yourprovider.com` |
| `Email__Port` | `587` (or `465`) |
| `Email__UserName` | `noreply@jadetables.com` |
| `Email__Password` | the mailbox or app password |
| `Email__From` | `noreply@jadetables.com` |
| `Email__FromName` | `Jade Tables` (optional) |

Add each one like the connection string, for example:

```
%windir%\system32\inetsrv\appcmd set config -section:system.applicationHost/applicationPools /+"[name='JadeTables'].environmentVariables.[name='Email__Host',value='smtp.yourprovider.com']" /commit:apphost
```

Once `Email__Host` and `Email__From` are set, new players must confirm their address before they can log in. To help emails reach the inbox, set up SPF and DKIM for jadetables.com with your mail provider.

### Sign in with Google

1. Go to https://console.cloud.google.com, create a project, and open **APIs & Services → OAuth consent screen**. Choose **External**, enter "Jade Tables", your support email, and `jadetables.com` as an authorised domain. Add the privacy page, https://www.jadetables.com/privacy. Then publish the app.
2. Under **Credentials → Create credentials → OAuth client ID**, choose **Web application**.
   - **Authorised redirect URI:** `https://www.jadetables.com/signin-google` (exactly this, for every language).
3. Copy the client ID and secret into two app-pool variables:
   - `Authentication__Google__ClientId`
   - `Authentication__Google__ClientSecret`
4. Recycle the pool. The **Continue with Google** button then appears on the log-in and register pages.
