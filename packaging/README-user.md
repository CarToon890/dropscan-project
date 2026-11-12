# DropScan for Windows

1. Extract the ZIP to a folder of your choice.
2. Run `DropScan.exe`.
3. Select files or folders, then choose how recipients will connect.

This package is built for Windows x64. .NET 10 currently supports Windows 11 and only Windows 10 LTSC/Enterprise editions listed in Microsoft's [supported Windows versions](https://learn.microsoft.com/dotnet/core/install/windows).

This package includes the .NET 10 runtime for Windows x64; a separate .NET runtime installation is not required.

DropScan source and the application are distributed under the MIT License in `LICENSE-DropScan.txt`. Third-party notices are in `THIRD-PARTY-NOTICES.md`, `DOTNET-LICENSE.txt`, and `DOTNET-THIRD-PARTY-NOTICES.txt`.

LAN mode does not require `cloudflared`. Cloudflare Quick Tunnel is experimental and requires `cloudflared` installed from Cloudflare's official source. Its public URL is temporary and anyone with the URL may access the share subject to its PIN/approval settings. Do not use it for sensitive files.

Keep the sender PC and DropScan running while recipients download. If Windows Firewall prompts, limit access to a trusted private network.

LAN transfers use HTTP and are not encrypted in transit. Cloudflare Quick Tunnel is experimental and is not end-to-end encrypted. This application is currently unsigned, so Windows may show a SmartScreen warning. Obtain packages only from the official DropScan GitHub repository and verify the published SHA-256 checksum.

