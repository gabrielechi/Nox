NOX Server
==========

This folder contains the NOX server, ready to run: there is no need to install .NET.

  appsettings.json   the only configuration file (port, address, data folder...)
  Server.exe         the server on Windows
  Server             the server on Linux
  data/              created on first start: database, encrypted files, token key, logs

STARTING
--------
Windows:  double-click Server.exe (or from a terminal: .\Server.exe)
Linux:    chmod +x Server      (first time only)
          ./Server

At startup the server shows the addresses it is listening on, for example:
  Listening on: http://localhost:5000, http://192.168.1.20:5000
In the NOX client of another computer: protocol HTTP, address 192.168.1.20:5000.
Every request received is shown on screen and written to data/logs/.
The server messages are in English; set "Language": "IT" in appsettings.json for Italian.

SECURITY AND BACKUP
-------------------
- File contents are end-to-end encrypted: the server cannot read them.
- Over HTTP, passwords travel unencrypted on the network: fine at home, not on public networks.
  For HTTPS set HttpsPort and a .pfx certificate in appsettings.json.
- Back up the data folder (with the server stopped). Never share data/jwt-secret.key.
