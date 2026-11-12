# DropScan for Windows

1. Extract the ZIP to a folder of your choice.
2. Run `DropScan.exe`.
3. Select files or folders, then choose how recipients will connect.

This package includes the .NET 8 runtime for Windows x64; a separate .NET runtime installation is not required.

LAN mode does not require `cloudflared`. Cloudflare Quick Tunnel is experimental and requires `cloudflared` installed from Cloudflare's official source. Its public URL is temporary and anyone with the URL may access the share subject to its PIN/approval settings. Do not use it for sensitive files.

Keep the sender PC and DropScan running while recipients download. If Windows Firewall prompts, limit access to a trusted private network.

