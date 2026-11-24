# Remote play through Cloudflare

PixelPipe can be reached at a public name such as `https://pixelpipe.lv426.cloud` without opening the engine's UDP ports. The browser talks only to the web app on port 8080. Signaling and the other engine calls the page makes are proxied there. Video and audio still use WebRTC, and a remote browser falls back to TURN when a direct UDP path does not exist.

## Tunnel

Run `cloudflared` on the Unraid host (or on the PixelPipe desktop) and publish one hostname:

- Public hostname: `pixelpipe.lv426.cloud`
- Service: `http://<pixelpipe-desktop>:8080`

Do not publish port 9090. The engine stays on the machine. New Windows installs bind it to `127.0.0.1:9090`.

Sign-in through the tunnel uses the same account as on the desktop. Forwarded HTTPS headers from a proxy on the private LAN are trusted, so the browser session is Secure and the Blazor connection accepts `https://pixelpipe.lv426.cloud`. A public client that reaches port 8080 directly cannot spoof those headers. If the admin password was mistyped when the account was created, reset it on the PC from the tray: **Reset admin password…**. That dialog is not available through the tunnel.

In PixelPipe Settings, as an admin:

1. Turn on **Behind a reverse proxy**. That makes PixelPipe honour `X-Forwarded-For` and `X-Forwarded-Proto` from the tunnel.
2. Set **Public URL** to `https://pixelpipe.lv426.cloud`. Cookies and redirects then use that HTTPS origin even though cloudflared connects to local HTTP.

Sign-in uses an HttpOnly, SameSite=Lax cookie. It is marked Secure when the request is HTTPS, which the public URL and forwarded protocol provide. There is no anonymous play. The first visit on an upgraded install asks for an admin account.

Cloudflare Access (`Cf-Access-Jwt-Assertion`) is a possible later SSO path. It is not accepted yet.

## TURN

A Cloudflare Tunnel carries HTTP and WebSocket, not the UDP packets WebRTC wants for media. Remote play needs a TURN relay.

Cloudflare Realtime TURN is the first provider. In the Cloudflare dashboard, create a Realtime TURN key. In PixelPipe Settings paste the key ID and the API token once. They are stored with Windows DPAPI (CurrentUser) and are not shown again. On Docker, where DPAPI is not available, the same file is stored on the data volume and should not be published.

For each play session the server calls:

`POST https://rtc.live.cloudflare.com/v1/turn/keys/{keyId}/credentials/generate-ice-servers`

The short-lived ICE username and credential go to the browser and to the engine's WebRTC peer. The API token does not.

A generic TURN entry (URL, username, credential) can be saved as well. Use `turns:` on port 443 when UDP to the relay is blocked. Example: `turns:turn.example.com:443?transport=tcp`.

On a LAN, host candidates are still gathered and win when both sides can reach each other. TURN is the fallback for a browser that is not on that network.

## Latency

A direct LAN connection stays the fast path. A remote session adds a relay hop through Cloudflare, so expect more delay than sitting at the desktop: often a few tens of milliseconds extra for UDP TURN, and more when the only working path is TURN-over-TLS on 443. That is the trade-off for playing through the tunnel without opening UDP on the home router.
