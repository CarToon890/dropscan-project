# Security policy

## Reporting a vulnerability

Please do not publish details of an unpatched vulnerability in a public issue. Use GitHub's private vulnerability reporting or Security Advisories for this repository. If private reporting is unavailable, open a minimal issue asking for a private contact channel without including exploit details.

Reports should include the affected version/commit, the impact, reproduction steps, and any suggested mitigation. Please allow time for a fix and coordinated disclosure before publishing details.

## Supported releases

Only the latest published release is considered for security fixes. Development builds and Cloudflare Quick Tunnel are experimental.

## Security boundaries

- LAN mode uses HTTP and does not encrypt file contents, PINs, URLs, or cookies in transit.
- Cloudflare Quick Tunnel sends public traffic through Cloudflare and connects from the tunnel process to the sender app over loopback HTTP. It is not end-to-end encryption and has no uptime guarantee.
- A share link is a bearer secret. Anyone who obtains it can access the share unless PIN or sender approval is enabled.
- The sender computer and DropScan must remain running during transfer. Stopping a share prevents future requests; it cannot revoke bytes already downloaded.
- DropScan is intended for direct, short-lived transfers and is not a managed file-hosting or enterprise service.

