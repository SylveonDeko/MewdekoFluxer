# Mewdeko Docker image

`sylveondeko/mewdeko-fluxer` runs the Mewdeko bot for Fluxer. It does not contain a bot token, database password, API key, or any other credentials. Supply a `credentials.json` file when starting the container.

## Tags

- `nightly`: newest successful build from `main`
- `<commit>`: immutable build from a specific commit, tagged with its short hash
- `latest` and a version tag: published for a versioned release

The image supports `linux/amd64` and `linux/arm64`.

## Quick start with Docker Compose

This is the recommended setup. It starts PostgreSQL, Redis, the bot, and the optional dashboard on one private Docker network.

```bash
curl -O https://raw.githubusercontent.com/SylveonDeko/MewdekoFluxer/main/docker-compose.example.yml
curl -O https://raw.githubusercontent.com/SylveonDeko/MewdekoFluxer/main/credentials.docker.example.json
curl -O https://raw.githubusercontent.com/SylveonDeko/MewdekoFluxer/main/dashboard.docker.env.example

mv credentials.docker.example.json credentials.json
mv dashboard.docker.env.example dashboard.env
mv docker-compose.example.yml compose.yaml
```

Edit `credentials.json`, `dashboard.env`, and the PostgreSQL password in `compose.yaml`. Then start the stack:

```bash
docker compose up -d
docker compose logs -f mewdeko
```

The dashboard is available at `http://localhost:3000`. For production, place it behind an HTTPS reverse proxy and register that public URL's `/api/auth/callback` as a redirect on your Fluxer OAuth2 application.

## Required bot credentials

| `credentials.json` key | Purpose |
| --- | --- |
| `Token` | Fluxer bot token. Never commit or publish it. |
| `OwnerIds` | Fluxer user IDs allowed to use owner-only commands. |
| `FluxerInstanceUrl` | API URL of a self-hosted Fluxer instance. Leave empty for fluxer.app. |
| `PsqlConnectionString` | PostgreSQL connection string. The Compose template uses the `postgres` service hostname. |
| `RedisConnections` | Redis address. The Compose template uses `redis:6379`. |
| `IsApiEnabled` | Must be `true` when using the dashboard. |
| `ApiPort` | Bot HTTP API port; use `5001` unless deliberately changing it everywhere. |
| `ApiKey` | Shared secret for dashboard-to-bot API calls. Must equal dashboard `MEWDEKO_API_KEY`. |
| `JwtSecret` | Shared secret for dashboard user JWTs. Must equal dashboard `BOT_JWT_SECRET`. |

Generate independent secrets with `openssl rand -hex 32`.

## Self-hosted Fluxer instances

To run Mewdeko on your own Fluxer instance instead of fluxer.app, set its API URL, such as `https://api.example.org`, in two places:

- `FluxerInstanceUrl` in `credentials.json`, or the `Mewdeko_FluxerInstanceUrl` environment variable on the bot container
- `FLUXER_INSTANCE_URL` in `dashboard.env`

Both read the instance's `/.well-known/fluxer` document for its gateway, sign in page and media hosts, so nothing else needs changing. The bot account and the OAuth2 application have to be created on that instance.

## Bot-only deployment

If PostgreSQL and Redis already exist, create `credentials.json` from [`credentials_example.json`](src/Mewdeko/credentials_example.json), point it at those services, and run:

```bash
docker run -d \
  --name mewdeko \
  --restart unless-stopped \
  -v "$PWD/credentials.json:/app/credentials.json:ro" \
  -v mewdeko-data:/app/data \
  -v mewdeko-logs:/app/logs \
  sylveondeko/mewdeko-fluxer:nightly
```

Only publish port `5001` when another service outside Docker needs the bot API. Keep it private when the dashboard runs on the same Docker network.

## Owner Docker page

Bot owners get a Docker page in the dashboard's Owner Panel that lists the containers on the bot's host, samples their CPU and memory, follows their logs, and can start, stop and restart them. It talks to the daemon over its socket, so the bot needs it mounted:

```yaml
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
```

The page also shows, per registered instance, the commit it runs (stamped into the image by CI as `MEWDEKO_GIT_SHA`) against the newest `sylveondeko/mewdeko-fluxer:nightly` on Docker Hub, with an Update button per bot and an Update all for the whole compose project. Compose pull, up and update run in a throwaway `docker:cli` helper container the bot creates through the socket, so they survive the bot's own container being recreated; the project's compose directory has to be mounted into the bot at the same path it has on the host for that to work. Without the socket the page just reports that Docker is not configured. Anyone the bot treats as an owner can then control every container on that machine, so keep the owner list short.

Forks publishing under another name can point the version check elsewhere with `MEWDEKO_IMAGE_REPO` and `MEWDEKO_IMAGE_TAG`.

## Dashboard

The companion image is [`sylveondeko/mewdash-fluxer`](https://hub.docker.com/r/sylveondeko/mewdash-fluxer). Its required environment variables and standalone launch command are documented in the [dashboard Docker guide](https://github.com/SylveonDeko/MewdekoDashFluxer/blob/main/DOCKER.md).
