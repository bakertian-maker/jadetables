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

On your development PC:

```
dotnet test
dotnet publish GameWeb -p:PublishProfile=IIS
```

This writes the site to `GameWeb\bin\publish\iis\`. Copy that folder's contents to the server's site folder (for example `C:\inetpub\jadetables`).

To replace files that are in use, put a file named `app_offline.htm` into the site folder first. IIS then stops the app and shows that page. Copy the new files, then delete `app_offline.htm`.

## Checks after deploying

- https://www.jadetables.com opens.
- http://jadetables.com ends up at https://www.jadetables.com.
- A game against the computer starts and moves.
- A finished game appears in the `Games` table.
- If the site doesn't start, set `stdoutLogEnabled="true"` in the published `web.config`, create a `logs` folder next to it, and read the log. Switch it off again afterwards.
