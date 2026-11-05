-- Channel bridges: relay a channel's messages to a channel in another community on the other platform.
-- Migration: 231-AddChannelBridges.sql

CREATE TABLE IF NOT EXISTS "ChannelBridges"
(
    "Id"                SERIAL PRIMARY KEY,
    "BridgeId"          UUID           NOT NULL UNIQUE,
    "GuildId"           NUMERIC(20, 0) NOT NULL,
    "ChannelId"         NUMERIC(20, 0) NOT NULL UNIQUE,
    "WebhookUrl"        TEXT           NOT NULL,
    "RemoteGuildId"     NUMERIC(20, 0) NOT NULL,
    "RemoteChannelId"   NUMERIC(20, 0) NOT NULL,
    "RemoteGuildName"   TEXT           NOT NULL DEFAULT '',
    "RemoteChannelName" TEXT           NOT NULL DEFAULT '',
    "Paused"            BOOLEAN        NOT NULL DEFAULT FALSE,
    "AllowBots"         BOOLEAN        NOT NULL DEFAULT FALSE,
    "CreatedBy"         NUMERIC(20, 0) NOT NULL,
    "DateAdded"         TIMESTAMP      NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS "IX_ChannelBridges_GuildId" ON "ChannelBridges" ("GuildId");
