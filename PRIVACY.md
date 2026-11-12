# Privacy

DropScan serves selected files from the sender's Windows computer. In LAN mode it does not upload files to a hosted DropScan server. In Cloudflare Quick Tunnel mode, transfer traffic passes through Cloudflare; the sender must install and run Cloudflare's `cloudflared` program.

The application keeps a short summary of past shares in `%LocalAppData%\DropScan\history.json` (time, count, total size, and status). It does not save share URLs, tokens, PINs, selected filenames, or source paths in that history. Receiver IP addresses and approval requests are held in memory only for the active share. Windows notification behavior is controlled by Windows settings.

The sender chooses the files and the network mode. A share is available only while the share is active and before its expiry. LAN HTTP traffic is not encrypted. Tunnel mode is not end-to-end encrypted because Cloudflare handles the public connection.

DropScan does not currently include analytics, telemetry, an account system, or a hosted file-storage service.

